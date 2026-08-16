using System.Text;

namespace BetterTranslator.Engine.Terminology;

/// <summary>One term the corrector acted on, for the reader and for the log.</summary>
public sealed record TermCorrection(string Source, string From, string To, string Reason, TermAction Action);

public enum TermAction
{
    WrongRenderingReplaced,
    SourceRestored,
    Converged,
}

public sealed record TerminologyResult(string Text, IReadOnlyList<TermCorrection> Corrections)
{
    public bool Changed => Corrections.Count > 0;
}

/// <summary>
/// Applies the domain vocabulary to an answer that has already passed every
/// gate.
///
/// It corrects rather than refuses, which is the whole difference from
/// <see cref="Slop.Glossary"/>. A refusal costs the reader the entire line and
/// tells them nothing; `motor` where the source said `Engine` is one wrong word
/// in an otherwise good sentence, and the sentence is worth keeping.
///
/// It only ever replaces a rendering the table names as wrong. Blind
/// substitution of an accepted rendering into a sentence that never contained
/// the term would corrupt correct Czech, so the direction is always
/// named-wrong to accepted, never guessed-wrong to accepted.
/// </summary>
public static class TerminologyCorrector
{
    public static TerminologyResult Apply(string source, string translated, DomainTermTable table)
    {
        ArgumentNullException.ThrowIfNull(table);

        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translated))
        {
            return new TerminologyResult(translated ?? string.Empty, []);
        }

        var corrections = new List<TermCorrection>();
        var text = translated;

        foreach (var term in table.Present(source))
        {
            text = term.KeepInSource
                ? Restore(text, term, corrections)
                : Replace(text, term, corrections);
        }

        return new TerminologyResult(text, corrections);
    }

    /// <summary>
    /// A term that must survive byte-identical came back translated. The wrong
    /// renderings are the only handle on it, so a component name with no named
    /// wrong form is left alone rather than hunted for.
    /// </summary>
    private static string Restore(string text, DomainTerm term, List<TermCorrection> into)
    {
        foreach (var wrong in term.Wrong)
        {
            var swapped = Swap(text, wrong, term.Source);

            if (swapped is null)
            {
                continue;
            }

            into.Add(new TermCorrection(term.Source, wrong, term.Source, term.Reason, TermAction.SourceRestored));
            text = swapped;
        }

        return text;
    }

    private static string Replace(string text, DomainTerm term, List<TermCorrection> into)
    {
        if (DomainTermTable.Contains(text, Stem(term.Accepted)))
        {
            return text;
        }

        foreach (var wrong in term.Wrong)
        {
            var swapped = Swap(text, wrong, term.Accepted);

            if (swapped is null)
            {
                continue;
            }

            into.Add(new TermCorrection(
                term.Source,
                wrong,
                term.Accepted,
                term.Reason,
                TermAction.WrongRenderingReplaced));

            return swapped;
        }

        return text;
    }

    /// <summary>
    /// Replaces whole-word occurrences only, preserving the case shape of what
    /// was there: a sentence-initial `Motor` becomes `Engine` and not `engine`.
    /// Returns null when nothing matched, so the caller can tell a no-op from a
    /// replacement without comparing strings.
    /// </summary>
    private static string? Swap(string text, string wrongWord, string right)
    {
        // A wrong rendering inflects like any other Czech word, so it is matched
        // by stem: `pobočka` in the table has to find `pobočku` in the answer.
        // Four characters is the floor, below which a stem stops naming one word
        // -- `cen` would fire on `cením`, which is a different word entirely.
        var wrong = Stem(wrongWord);
        var allowInflection = wrong.Length >= 4;

        if (wrong.Length == 0)
        {
            return null;
        }

        StringBuilder? built = null;
        var at = 0;
        var copied = 0;

        while (at <= text.Length - wrong.Length)
        {
            var hit = text.IndexOf(wrong, at, StringComparison.OrdinalIgnoreCase);

            if (hit < 0)
            {
                break;
            }

            if (!DomainTermTable.IsWholeWord(text, hit, wrong.Length, allowInflection))
            {
                at = hit + 1;
                continue;
            }

            // The inflected tail of the wrong rendering goes with it. `motoru`
            // is one word and half of it must not survive the swap.
            var end = hit + wrong.Length;

            while (allowInflection && end < text.Length && char.IsLower(text[end]))
            {
                end++;
            }

            built ??= new StringBuilder(text.Length);
            built.Append(text, copied, hit - copied);
            built.Append(Shape(text.AsSpan(hit, end - hit), right));

            copied = end;
            at = end;
        }

        if (built is null)
        {
            return null;
        }

        built.Append(text, copied, text.Length - copied);

        return built.ToString();
    }

    /// <summary>
    /// Replaces one accepted rendering with another the document already
    /// settled on. Returns null when nothing matched.
    /// </summary>
    public static string? Converge(string text, string from, string to) => Swap(text, from, to);

    /// <summary>
    /// The part of a word every inflected form shares, which for Czech is the
    /// word without its final vowel. One vowel only: stripping further turns a
    /// stem into a fragment that matches unrelated words.
    /// </summary>
    private static string Stem(string word) =>
        word.Length > 4 && IsVowel(word[^1]) ? word[..^1] : word;

    private static bool IsVowel(char c) =>
        "aeiouyáéíóúůýě".Contains(char.ToLowerInvariant(c), StringComparison.Ordinal);

    /// <summary>
    /// Gives the replacement the case shape of what it replaced.
    ///
    /// The all-caps arm is not decoration: CasingGuard runs first and uppercases
    /// a whole answer whose source was shouting, so a term swapped in after it
    /// lands in a line of capitals. `NEROZBIJTE Engine` was the measured result
    /// before this looked at more than the first character.
    /// </summary>
    private static string Shape(ReadOnlySpan<char> replaced, string replacement)
    {
        if (replacement.Length == 0 || replaced.Length == 0)
        {
            return replacement;
        }

        var letters = 0;
        var capitals = 0;

        foreach (var c in replaced)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            letters++;

            if (char.IsUpper(c))
            {
                capitals++;
            }
        }

        if (letters >= 2 && capitals == letters)
        {
            return replacement.ToUpperInvariant();
        }

        return char.IsUpper(replaced[0]) && char.IsLower(replacement[0])
            ? char.ToUpperInvariant(replacement[0]) + replacement[1..]
            : replacement;
    }
}
