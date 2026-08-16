using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Markup;

/// <summary>One run of source text that survived, and how long it is.</summary>
public sealed record ResidueRun(string Text, int Words, int Index);

/// <summary>
/// Did a run of the SOURCE survive untranslated inside the answer?
///
/// This is the hole every other gate leaves open. The coverage check compares
/// whole lines, so a line that changed at all counts as translated; the leak
/// gate looks for prompt text, not source text; and the structural gates only
/// ever count markup. A line that came back half done therefore passed
/// everything and shipped looking finished:
///
///   "On Windows you can also use `build.bat` to cross-build both binaries."
///   -> "Na Windows muzete take pouzit `build.bat` to cross-build both binaries."
///
/// That is what segmenting a sentence at its code spans produces when one
/// fragment translates and the next does not, and it is worse than no
/// translation at all, because nothing downstream reports it.
/// </summary>
public static class SourceResidue
{
    /// <summary>
    /// Words only: punctuation differs between languages and would break runs
    /// that are otherwise identical. The apostrophes are the straight and curly
    /// forms, written as escapes rather than as themselves.
    /// </summary>
    private static readonly Regex Word =
        new(@"[\p{L}\p{Nd}]+(?:['’-][\p{L}\p{Nd}]+)*", RegexOptions.Compiled);

    /// <summary>
    /// Every run of source text that survived, longest first.
    ///
    /// <see cref="Check"/> answers "is any of this untranslated", which is all a
    /// gate needs. Repairing it needs more: WHICH phrases, and in an order that
    /// lets each be replaced without disturbing the next. So each run is masked
    /// out of the search before the next is looked for -- otherwise
    /// "cross-build both binaries into" and "build both binaries" would both be
    /// reported and the second replacement would corrupt the first.
    /// </summary>
    /// <param name="protect">
    /// The pattern for spans that are SUPPOSED to survive: code spans, URLs,
    /// paths, product names. Anything inside one is removed from both sides
    /// before comparing, because a backtick span coming back identical is the
    /// system working, not a failure.
    /// </param>
    public static IReadOnlyList<ResidueRun> Find(
        string source,
        string? translated,
        int minRun = 3,
        string? protect = null)
    {
        var found = new List<ResidueRun>();

        if (string.IsNullOrWhiteSpace(translated) || string.IsNullOrWhiteSpace(source))
        {
            return found;
        }

        var src = source;
        var outText = translated!;

        if (!string.IsNullOrEmpty(protect))
        {
            src = Regex.Replace(src, protect, " ");
            outText = Regex.Replace(outText, protect, " ");
        }

        var words = Word.Matches(src).Select(m => m.Value).ToArray();

        if (words.Length < minRun)
        {
            return found;
        }

        var remaining = outText;

        for (var len = words.Length; len >= minRun; len--)
        {
            for (var i = 0; i <= words.Length - len; i++)
            {
                var phrase = string.Join(' ', words.Skip(i).Take(len));

                // Case-sensitive: a real translation rarely reproduces the
                // source's capitalisation as well as its words.
                var rx = @"(?<![\p{L}\p{Nd}])" + Regex.Escape(phrase) + @"(?![\p{L}\p{Nd}])";

                if (Regex.IsMatch(remaining, rx))
                {
                    found.Add(new ResidueRun(phrase, len, i));

                    // Masked so shorter runs inside it are not reported as well.
                    remaining = Regex.Replace(remaining, rx, " ");
                }
            }
        }

        return found;
    }

    private static readonly Regex[] Strippers =
    [
        new("`[^`]*`", RegexOptions.Compiled),
        new("<https?://[^>]+>", RegexOptions.Compiled),
        new(@"https?://\S+", RegexOptions.Compiled),
        new(@"\[[^\]]*\]\([^)]*\)", RegexOptions.Compiled),
        new(@"[A-Za-z]:\\\S+", RegexOptions.Compiled),
        new("--[a-z][a-z0-9-]*", RegexOptions.Compiled),
        new(@"[^\p{L}\s]", RegexOptions.Compiled),
    ];

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// The gate: a reason when a run of the source survived, null when nothing
    /// did. Four words by default -- shorter runs are ordinary, since a product
    /// name, a technical term or a units-and-numbers phrase all legitimately
    /// survive, and flagging those would refuse correct work. Four words in a
    /// row is a clause, and a clause that crossed unchanged was not translated.
    /// </summary>
    public static string? Check(string source, string? translated, int minRun = 4)
    {
        if (string.IsNullOrWhiteSpace(translated))
        {
            return null;
        }

        var src = Strip(source);
        var outText = Strip(translated!);

        if (src.Length == 0 || outText.Length == 0)
        {
            return null;
        }

        var words = src.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length < minRun)
        {
            return null;
        }

        // Longest source run that survived verbatim. Compared case-sensitively:
        // a genuine translation rarely reproduces the source's capitalisation.
        for (var i = 0; i <= words.Length - minRun; i++)
        {
            var run = string.Join(' ', words.Skip(i).Take(minRun));

            if (outText.Contains(run, StringComparison.Ordinal))
            {
                return $"source text survived untranslated: '{run}'";
            }
        }

        return null;
    }

    private static string Strip(string text)
    {
        var s = text;

        foreach (var stripper in Strippers)
        {
            s = stripper.Replace(s, " ");
        }

        return Whitespace.Replace(s, " ").Trim();
    }

    /// <summary>
    /// Is this line a directory-listing row rather than code?
    ///
    /// Fences are skipped wholesale, which is right for code but wrong for the
    /// layout block a README writes as an UNTAGGED fence purely to get a
    /// monospace column. Its right-hand side is ordinary prose a reader of the
    /// translation needs.
    ///
    /// The rule is deliberately narrow, because being wrong here means editing
    /// somebody's code: the first token must look like a path (it contains a
    /// slash), it must be followed by two or more spaces -- the column gap, not
    /// a sentence -- and the right-hand side must read as prose, three or more
    /// all-letter words. The caller applies this only inside a fence with no
    /// language tag.
    /// </summary>
    public static bool IsListingLine(string? line)
    {
        if (line is null)
        {
            return false;
        }

        var match = Listing.Match(line);

        if (!match.Success)
        {
            return false;
        }

        var words = DescriptionSplit
            .Split(match.Groups[4].Value)
            .Count(w => AllLetters.IsMatch(w));

        return words >= 3;
    }

    private static readonly Regex Listing =
        new(@"^(\s*)([\w.\-]+(?:[/\\][\w.\-]*)+)(\s{2,})(\S.*)$", RegexOptions.Compiled);

    private static readonly Regex DescriptionSplit = new(@"[\s,;:()]+", RegexOptions.Compiled);

    private static readonly Regex AllLetters = new(@"^\p{L}{2,}$", RegexOptions.Compiled);
}
