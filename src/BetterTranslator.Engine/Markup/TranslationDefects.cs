using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

public enum DefectKind
{
    /// <summary>A run of the source that came back untranslated.</summary>
    Residue,

    /// <summary>A translated word abutting a protected term with no separator.</summary>
    Glue,
}

public enum DefectVerdict
{
    /// <summary>Repair without asking.</summary>
    CertainBad,

    /// <summary>Never report.</summary>
    CertainOk,

    /// <summary>A model decides; no rule available here separates these.</summary>
    Ambiguous,
}

/// <summary>One thing that looks wrong with a translation, and how sure we are.</summary>
public sealed record TranslationDefect(
    DefectKind Kind,
    string Text,
    int Words,
    DefectVerdict Verdict,
    string Reason)
{
    public int Index { get; init; } = -1;

    public int Boundary { get; init; } = -1;
}

/// <summary>
/// One detector, three verdicts. Ported from `Get-TranslationDefect`.
///
/// <see cref="SourceResidue.Find"/> answers "what survived" and
/// <see cref="SourceResidue.Check"/> answers "is anything wrong". Neither can
/// answer the question a repair pass actually has: is this particular survival a
/// DEFECT? At three words and up the answer is almost always yes. Below it the
/// answer is mostly no, and that is measured rather than assumed -- on a real
/// README, dropping the floor to two words surfaced "go build", "go test",
/// "Web Audio", "or CC0", "Bubble Surge" and "peer IPs", every one of them
/// correct output.
/// </summary>
public static class TranslationDefects
{
    /// <summary>
    /// Closed-class words of the SOURCE language.
    ///
    /// A content word can legitimately survive translation -- a product name, a
    /// technical term, a unit. A function word cannot: if "the", "into" or "you"
    /// is still sitting in the output, that clause was not translated. They are
    /// what separates "Web Audio" (fine) from "original assets" (not fine) at a
    /// length where nothing else can tell them apart.
    ///
    /// English only, because English is the source in every run this engine has
    /// seen. Passed as a parameter rather than read from here so a document in
    /// another source language can supply its own list instead of being measured
    /// against the wrong one.
    /// </summary>
    public static IReadOnlyList<string> EnglishFunctionWords { get; } =
    [
        "the", "a", "an", "of", "and", "or", "but", "if", "then", "than", "that", "this", "these", "those",
        "is", "are", "was", "were", "be", "been", "being", "has", "have", "had", "do", "does", "did",
        "to", "into", "onto", "from", "with", "without", "before", "after", "during", "until", "while",
        "for", "on", "in", "at", "by", "as", "about", "over", "under", "between", "through",
        "must", "should", "can", "could", "will", "would", "may", "might", "shall",
        "you", "your", "it", "its", "they", "their", "we", "our", "not", "no", "only", "also", "use", "used",
        "every", "each",
    ];

    /// <summary>Lowercase meeting uppercase inside one token.</summary>
    private static readonly Regex GlueToken =
        new(@"[\p{L}]*[\p{Ll}][\p{Lu}][\p{L}]*", RegexOptions.Compiled);

    private static readonly Regex AnyLetter = new(@"\p{L}", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    public static IReadOnlyList<TranslationDefect> Find(
        string? source,
        string? translated,
        string? protect = null,
        IReadOnlyList<string>? doNotTranslate = null,
        int minRun = 2,
        IReadOnlyList<string>? functionWords = null)
    {
        var defects = new List<TranslationDefect>();

        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(translated))
        {
            return defects;
        }

        doNotTranslate ??= [];
        functionWords = functionWords is { Count: > 0 } ? functionWords : EnglishFunctionWords;

        var dnt = new HashSet<string>(
            doNotTranslate.Where(d => !string.IsNullOrEmpty(d)).Select(d => d.ToLowerInvariant()),
            StringComparer.Ordinal);

        var fn = new HashSet<string>(
            functionWords.Where(w => !string.IsNullOrEmpty(w)).Select(w => w.ToLowerInvariant()),
            StringComparer.Ordinal);

        AddResidue(defects, source!, translated!, protect, minRun, dnt, fn);
        AddGlue(defects, source!, translated!, protect, doNotTranslate);

        return defects;
    }

    /// <summary>
    /// Scanned from ONE word up, not from minRun, for a case the two-word floor
    /// could never see. In "On Windows you can also use ..." the word "Windows"
    /// is a legitimate do-not-translate term and is masked away, so the
    /// surviving English collapses to the single word "On" -- below any floor,
    /// and invisible. But a lone "On" is not ambiguous at all: a function word
    /// still in the output means that clause was not translated.
    ///
    /// So single words are admitted, and then almost all of them are thrown
    /// away: a one-word run counts only if it is a function word.
    /// </summary>
    private static void AddResidue(
        List<TranslationDefect> defects,
        string source,
        string translated,
        string? protect,
        int minRun,
        HashSet<string> dnt,
        HashSet<string> fn)
    {
        var scanFrom = Math.Min(1, minRun);

        foreach (var run in SourceResidue.Find(source, translated, scanFrom, protect))
        {
            string? functionHit = null;

            foreach (var word in Whitespace.Split(run.Text))
            {
                if (fn.Contains(word.ToLowerInvariant()))
                {
                    functionHit = word;
                    break;
                }
            }

            if (dnt.Contains(run.Text.ToLowerInvariant()))
            {
                continue;                                   // never a defect
            }

            if (!AnyLetter.IsMatch(run.Text))
            {
                continue;                                   // digits, punctuation
            }

            if (run.Words < minRun && functionHit is null)
            {
                continue;                                   // too short to mean anything
            }

            // A one or two letter word is never evidence on its own, however
            // function-like it looks in the source language. "a" is an English
            // article AND the Czech word for "and", so a surviving "a" was
            // reported as untranslated English on a perfectly translated line.
            // Nothing is lost: a genuinely untranslated clause always carries a
            // longer run as well.
            if (run.Words == 1 && run.Text.Trim().Length < 3)
            {
                continue;
            }

            var (verdict, reason) = run.Words >= 4
                ? (DefectVerdict.CertainBad, $"{run.Words} words is a clause, not a term")
                : functionHit is not null
                    ? (DefectVerdict.CertainBad,
                        $"holds the function word '{functionHit}', which cannot survive translation")
                    : (DefectVerdict.Ambiguous, "short run, cannot be judged mechanically");

            defects.Add(new TranslationDefect(DefectKind.Residue, run.Text, run.Words, verdict, reason));
        }
    }

    /// <summary>
    /// Glue: a translated word abutting a protected term with no separator --
    /// "prezentaciBubble Surge". It loses no markup and invents no word, so
    /// every other gate passes it. The signature is a lowercase letter meeting
    /// an uppercase one inside a single token.
    ///
    /// The masking is to the SAME LENGTH, not to a shorter string: offsets have
    /// to survive it, because the term test asks "does a do-not-translate term
    /// begin at this exact position in the real text". Replacing a span with one
    /// space would shift every position after it.
    /// </summary>
    private static void AddGlue(
        List<TranslationDefect> defects,
        string source,
        string translated,
        string? protect,
        IReadOnlyList<string> doNotTranslate)
    {
        var masked = translated;

        if (!string.IsNullOrEmpty(protect))
        {
            masked = Regex.Replace(translated, protect, m => new string(' ', m.Value.Length));
        }

        foreach (Match match in GlueToken.Matches(masked))
        {
            var token = match.Value;
            var verdict = DefectVerdict.Ambiguous;
            var reason = "lowercase meets uppercase inside one word";

            // Where the lowercase run meets the uppercase one, as an offset into
            // the real string -- which is what makes the term test possible.
            var boundary = -1;

            for (var k = 1; k < token.Length; k++)
            {
                if (char.IsLower(token[k - 1]) && char.IsUpper(token[k]))
                {
                    boundary = match.Index + k;
                    break;
                }
            }

            if (Regex.IsMatch(source, @"(?<![\p{L}])" + Regex.Escape(token) + @"(?![\p{L}])"))
            {
                // Written that way in the source too, so it is the author's
                // spelling.
                verdict = DefectVerdict.CertainOk;
                reason = "the source spells it the same way";
            }
            else if (boundary > 0)
            {
                // Matched at the BOUNDARY, not inside the token.
                // "prezentaciBubble Surge" holds only "Bubble" within the token
                // -- the term runs past the token's end -- so looking for the
                // whole term inside it finds nothing and the real defect is
                // graded ambiguous.
                foreach (var term in doNotTranslate)
                {
                    if (string.IsNullOrEmpty(term) || boundary + term.Length > translated.Length)
                    {
                        continue;
                    }

                    if (!string.Equals(translated.Substring(boundary, term.Length), term, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (Regex.IsMatch(source, @"\s" + Regex.Escape(term), RegexOptions.IgnoreCase))
                    {
                        verdict = DefectVerdict.CertainBad;
                        reason = $"'{term}' is glued to the word before it; the source has a space there";
                        break;
                    }
                }
            }

            defects.Add(new TranslationDefect(DefectKind.Glue, token, 1, verdict, reason)
            {
                Index = match.Index,
                Boundary = boundary,
            });
        }
    }
}
