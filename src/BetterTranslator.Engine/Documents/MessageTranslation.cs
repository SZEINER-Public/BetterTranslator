using System.Text;
using BetterTranslator.Engine.Slop;

namespace BetterTranslator.Engine.Documents;

/// <summary>Why an answer could not be written over the text it was asked for.</summary>
public enum KeptCause
{
    /// <summary>Nothing came back: the model was empty, or a guard refused it.</summary>
    NoAnswer,

    /// <summary>The answer carried a line break, so it would split one line into two.</summary>
    Reflowed,

    /// <summary>The answer was the source, which is the model saying it did nothing.</summary>
    Echoed,
}

/// <summary>
/// One request the message made, and what came of it.
///
/// A count of kept lines says a message came back part English; it cannot say
/// which part or why, so nothing downstream can show a reader the failure or
/// aim a fix at it. The reason is the whole point of this record.
/// </summary>
/// <param name="Reason">Null when the model's answer was used.</param>
public sealed record MessageUnit(
    int Start,
    int Length,
    string Source,
    string Delivered,
    bool FromModel,
    string? Reason);

/// <summary>How a plain message came out.</summary>
/// <param name="Translated">Lines the model answered whole.</param>
/// <param name="Recovered">
/// Lines that came back unusable and were retranslated sentence by sentence.
/// Not an error: the text is translated, just with less context per call.
/// </param>
/// <param name="Kept">Lines nothing usable came back for, left in the source language.</param>
/// <param name="Units">Every request made, in the order it was made.</param>
public sealed record MessageTranslationResult(
    string Text,
    int Translated,
    int Recovered,
    int Kept,
    IReadOnlyList<MessageUnit> Units)
{
    public IEnumerable<MessageUnit> Failures => Units.Where(u => !u.FromModel);
}

/// <summary>
/// Translates an ordinary message a line at a time, and a line that fails a
/// sentence at a time.
///
/// The whole message used to go in one request. That works for the sentence
/// people usually type and fails quietly for anything longer: a model handed
/// three hundred characters of technical prose answers part of it, or answers
/// it unchanged, and the guards then refuse the whole thing -- so a quarter of
/// the message stays in the source language and nothing says which quarter or
/// why.
///
/// Line first, because a line is usually a whole thought and context makes a
/// better translation. Sentences only when the line failed, because that is
/// where the trade turns: less context per call is worth much less than a
/// paragraph left untranslated.
/// </summary>
public static class MessageTranslation
{
    public static async Task<MessageTranslationResult> TranslateAsync(
        string message,
        Func<string, CancellationToken, Task<string?>> translate,
        CancellationToken cancellationToken = default,
        Func<string?>? refusal = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(translate);

        var text = message.ReplaceLineEndings("\n");
        var lines = text.Split('\n');

        var edits = new List<(int Start, int Length, string Text)>();
        var units = new List<MessageUnit>();
        int translated = 0, recovered = 0, kept = 0;

        var offset = 0;

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trimmed = Trim(line, offset);

            if (trimmed.Length > 0 && TranslationCandidate.IsWorthSending(line, minLetters: 1))
            {
                var source = text.Substring(trimmed.Start, trimmed.Length);
                var answer = await translate(source, cancellationToken);
                var cause = Unusable(answer, source);

                if (cause is null)
                {
                    edits.Add((trimmed.Start, trimmed.Length, answer!.Trim()));
                    units.Add(new MessageUnit(trimmed.Start, trimmed.Length, source, answer.Trim(), true, null));
                    translated++;
                }
                else
                {
                    // The line came back empty, reflowed, or exactly as it went
                    // out. Its sentences go on their own.
                    var before = edits.Count;
                    units.Add(new MessageUnit(
                        trimmed.Start,
                        trimmed.Length,
                        source,
                        source,
                        false,
                        Explain(cause.Value, refusal)));

                    await SentencesAsync(text, trimmed, translate, edits, units, refusal, cancellationToken);

                    if (edits.Count > before)
                    {
                        recovered++;
                    }
                    else
                    {
                        kept++;
                    }
                }
            }

            // The newline that ended this line, which is not part of it.
            offset += line.Length + 1;
        }

        return new MessageTranslationResult(Splice(text, edits), translated, recovered, kept, units);
    }

    private static async Task SentencesAsync(
        string text,
        (int Start, int Length) line,
        Func<string, CancellationToken, Task<string?>> translate,
        List<(int Start, int Length, string Text)> edits,
        List<MessageUnit> units,
        Func<string?>? refusal,
        CancellationToken cancellationToken)
    {
        var source = text.Substring(line.Start, line.Length);
        var sentences = SentenceSplitter.Split(source);

        // One sentence means the split found nothing the whole-line request did
        // not already have. Asking again would be the same question.
        if (sentences.Count < 2)
        {
            return;
        }

        foreach (var (start, length) in sentences)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sentence = source.Substring(start, length);

            if (!TranslationCandidate.IsWorthSending(sentence, minLetters: 1))
            {
                continue;
            }

            var answer = await translate(sentence, cancellationToken);
            var cause = Unusable(answer, sentence);

            if (cause is null)
            {
                edits.Add((line.Start + start, length, answer!.Trim()));
                units.Add(new MessageUnit(line.Start + start, length, sentence, answer.Trim(), true, null));
            }
            else
            {
                units.Add(new MessageUnit(
                    line.Start + start,
                    length,
                    sentence,
                    sentence,
                    false,
                    Explain(cause.Value, refusal)));
            }
        }
    }

    /// <summary>
    /// Why an answer cannot be written over the text it replaces, or null when
    /// it can.
    ///
    /// A line break would split one line into two and the message would no
    /// longer line up with its source beside it. An answer identical to what was
    /// sent is the model saying it did nothing, which is the signal to try
    /// smaller rather than something to write back.
    /// </summary>
    internal static KeptCause? Unusable(string? answer, string source)
    {
        if (string.IsNullOrWhiteSpace(answer))
        {
            return KeptCause.NoAnswer;
        }

        if (answer.Contains('\n', StringComparison.Ordinal))
        {
            return KeptCause.Reflowed;
        }

        return string.Equals(answer.Trim(), source.Trim(), StringComparison.Ordinal)
            ? KeptCause.Echoed
            : null;
    }

    /// <summary>
    /// The cause in the reader's terms. A refused answer arrives here as no
    /// answer at all, so the guard's own words are the only thing that can say
    /// which guard refused it and why.
    /// </summary>
    private static string Explain(KeptCause cause, Func<string?>? refusal)
    {
        var verdict = cause == KeptCause.NoAnswer ? refusal?.Invoke() : null;

        var text = cause switch
        {
            KeptCause.Reflowed => "the answer spanned more than one line",
            KeptCause.Echoed => "the model returned the source unchanged",
            _ => "no answer came back",
        };

        return string.IsNullOrWhiteSpace(verdict) ? text : $"{text}: {verdict}";
    }

    /// <summary>The line without the whitespace that indents or trails it.</summary>
    private static (int Start, int Length) Trim(string line, int offset)
    {
        var start = 0;
        var end = line.Length;

        while (start < end && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(line[end - 1]))
        {
            end--;
        }

        return (offset + start, end - start);
    }

    private static string Splice(string text, List<(int Start, int Length, string Text)> edits)
    {
        if (edits.Count == 0)
        {
            return text;
        }

        edits.Sort((a, b) => b.Start.CompareTo(a.Start));

        var builder = new StringBuilder(text);

        foreach (var (start, length, replacement) in edits)
        {
            builder.Remove(start, length).Insert(start, replacement);
        }

        return builder.ToString();
    }
}
