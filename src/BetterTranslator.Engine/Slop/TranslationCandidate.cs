using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Slop;

/// <summary>
/// Is this line worth sending to the model at all? Ported from `Test-Candidate`
/// in `translate-sweep.ps1`.
///
/// Everything that is supposed to look identical in both languages is excluded
/// here rather than translated and then rejected. That ordering is the point: a
/// line of pure markup sent to a model comes back changed, fails a structural
/// gate, and costs a request and a retry to end up exactly where it started.
/// </summary>
public static class TranslationCandidate
{
    /// <summary>
    /// Below this many letters, once everything non-prose is removed, there is
    /// nothing to translate.
    /// </summary>
    public const int DefaultMinLetters = 8;

    private static readonly Regex[] NeverProse =
    [
        new("`[^`]*`", RegexOptions.Compiled),              // inline code
        new(@"https?://\S+", RegexOptions.Compiled),        // urls
        new(@"[A-Za-z]:\\\S+", RegexOptions.Compiled),      // windows paths
        new(@"\[\[\d+\]\]", RegexOptions.Compiled),         // protected-block sentinels
        new("--[a-z][a-z0-9-]*", RegexOptions.Compiled),    // flags
    ];

    private static readonly Regex NonLetter = new(@"[^\p{L}\s]", RegexOptions.Compiled);
    private static readonly Regex Letter = new(@"\p{L}", RegexOptions.Compiled);

    /// <summary>A table separator row, a horizontal rule, a bare heading marker.</summary>
    private static readonly Regex StructureOnly = new(@"^[\|\-\:\s]+$", RegexOptions.Compiled);

    private static readonly Regex Heading = new(@"^\s*#{1,6}\s", RegexOptions.Compiled);

    /// <summary>
    /// A heading is held to a much lower bar than a paragraph.
    ///
    /// The prose floor works for prose. Applied to headings it silently skipped
    /// every short one -- "## Layout" is six letters, "## Run locally" is ten --
    /// so a document came back with its body translated and its section titles in
    /// English, which reads worse than either extreme.
    ///
    /// Lowering it used to be unsafe: at four letters the title "# biotank" was
    /// sent and came back "# biologicky reaktor", a product name destroyed. It is
    /// safe now for a reason visible above -- do-not-translate terms are stripped
    /// BEFORE this count, so a title that is nothing but a product name reduces to
    /// zero letters and is still never sent, while a real heading keeps its words.
    /// The config is what makes the difference, so this holds only as long as the
    /// name is in it.
    /// </summary>
    public const int HeadingMinLetters = 3;

    public static bool IsSingleWord(string? text)
    {
        var trimmed = text?.Trim();

        return !string.IsNullOrEmpty(trimmed) && !trimmed.Any(char.IsWhiteSpace) && trimmed.Any(char.IsLetter);
    }

    public static bool EchoIsDefect(string? source, IReadOnlyList<string>? doNotTranslate = null, int minLetters = DefaultMinLetters) =>
        !IsSingleWord(source) && IsWorthSending(source, doNotTranslate, minLetters);

    public static bool IsWorthSending(
        string? line,
        IReadOnlyList<string>? doNotTranslate = null,
        int minLetters = DefaultMinLetters)
    {
        var trimmed = line?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        var stripped = trimmed;

        foreach (var pattern in NeverProse)
        {
            stripped = pattern.Replace(stripped, " ");
        }

        // A line whose only words are terms the config says never to translate
        // has nothing left to translate once they are removed.
        foreach (var term in doNotTranslate ?? [])
        {
            if (!string.IsNullOrEmpty(term))
            {
                stripped = Regex.Replace(stripped, Regex.Escape(term), " ");
            }
        }

        stripped = NonLetter.Replace(stripped, " ");

        var needed = Heading.IsMatch(trimmed) ? Math.Min(HeadingMinLetters, minLetters) : minLetters;

        return Letter.Matches(stripped).Count >= needed && !StructureOnly.IsMatch(trimmed);
    }

    /// <summary>
    /// The one-line system prompt, deliberately short, and deliberately WITHOUT
    /// the rules file attached.
    ///
    /// Measured: with forty lines of rules attached to a single short line, the
    /// model answered about the rules instead of translating -- it pasted the
    /// rules text into the document, narrated the task, and invented
    /// explanations. The rules belong in the whole-document path where the input
    /// is large enough to hold the model's attention. Here, brevity is what
    /// keeps the answer a translation.
    ///
    /// It asks for meaning rather than words because a word-by-word rendering is
    /// grammatically wrong, not merely clumsy: cases, aspect and word order all
    /// have to move.
    /// </summary>
    public static string LinePrompt(string language) =>
        $"You translate one line of text into {language}. "
        + $"Translate the MEANING of the whole sentence, not word by word: use natural {language} "
        + "grammar, correct case endings and natural word order, formal register. "
        + "Output exactly one line. Output only the translation, nothing else. "
        + "Keep every backtick, code span, URL, path, --flag, number and product name exactly as given.";
}
