using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

/// <summary>
/// Puts back the casing the source carried and the answer dropped.
///
/// Case is not a translation decision, it is a property of the text, and a model
/// treats it as neither. Measured against TranslateGemma on five labelled lines
/// of one message: `PROJECT:` came back `PROJEKT:`, `EXISTING:` came back
/// `STÁVAJÍCÍ:`, a whole shouting line came back shouting -- and `FEATURE:` came
/// back `Funkce:`, character for character what the model returns for the
/// ordinary `Feature:`. The capitalisation was simply lost for that token, and
/// nothing about the sentence said which way it would go.
///
/// So it is restored here rather than asked for. The rules only ever copy a
/// shape the source actually had: a source that was not shouting cannot make an
/// answer shout, which is why the title-case `Feature:` above is left alone.
/// </summary>
public static class CasingGuard
{
    /// <summary>
    /// How far into an answer a label's colon may sit. A label is short in every
    /// language; a colon further in belongs to the sentence, and uppercasing
    /// everything before it would shout half a paragraph.
    /// </summary>
    private const int MaxLabelLength = 40;

    /// <summary>
    /// A leading run of capitals ending in a colon -- `FEATURE:`, `DO NOT:`.
    /// Digits are allowed inside it so `STAGE 0:` matches; lower case anywhere
    /// in the run means it is an ordinary capitalised word and not a label.
    /// </summary>
    private static readonly Regex UpperLabel = new(
        @"^\s*\p{Lu}[\p{Lu}\p{Nd}]*(?:[ \t]+[\p{Lu}\p{Nd}]+)*\s*:",
        RegexOptions.Compiled);

    public static string Restore(string? source, string? translated)
    {
        if (string.IsNullOrEmpty(translated) || string.IsNullOrEmpty(source))
        {
            return translated ?? string.Empty;
        }

        if (IsAllUpper(source))
        {
            return translated.ToUpperInvariant();
        }

        var labelled = RestoreLabel(source, translated);

        return RestoreFirstLetter(source, labelled);
    }

    /// <summary>
    /// Every letter is a capital and there are at least two of them. One capital
    /// is a normal sentence opening, not a shout.
    /// </summary>
    private static bool IsAllUpper(string text)
    {
        var letters = 0;

        foreach (var c in text)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            if (!char.IsUpper(c))
            {
                return false;
            }

            letters++;
        }

        return letters >= 2;
    }

    private static string RestoreLabel(string source, string translated)
    {
        var match = UpperLabel.Match(source);

        if (!match.Success || CountLetters(match.Value) < 2)
        {
            return translated;
        }

        var colon = translated.IndexOf(':', StringComparison.Ordinal);

        // No colon means the answer did not keep the label as a label. Guessing
        // where it went would be worse than leaving it.
        if (colon <= 0 || colon > MaxLabelLength)
        {
            return translated;
        }

        return translated[..colon].ToUpperInvariant() + translated[colon..];
    }

    /// <summary>
    /// A sentence that opened with a capital opens with one afterwards. Only in
    /// that direction: a source that opened lower case says nothing about
    /// whether the target language capitalises the word it was translated into.
    /// </summary>
    private static string RestoreFirstLetter(string source, string translated)
    {
        var sourceLetter = FirstLetter(source);
        var targetLetter = FirstLetter(translated);

        if (sourceLetter < 0 || targetLetter < 0)
        {
            return translated;
        }

        if (!char.IsUpper(source[sourceLetter]) || !char.IsLower(translated[targetLetter]))
        {
            return translated;
        }

        return string.Concat(
            translated.AsSpan(0, targetLetter),
            char.ToUpperInvariant(translated[targetLetter]).ToString(),
            translated.AsSpan(targetLetter + 1));
    }

    private static int FirstLetter(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsLetter(text[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static int CountLetters(string text)
    {
        var letters = 0;

        foreach (var c in text)
        {
            if (char.IsLetter(c))
            {
                letters++;
            }
        }

        return letters;
    }
}
