using System.Text.RegularExpressions;

namespace BetterTranslator.Engine.Corpus;

/// <summary>
/// Did the extraction actually produce text? Ported from `Test-ExtractedText`
/// in `rag-extract.ps1`.
///
/// A PDF with no text layer, a DOCX whose body is one embedded image, a file
/// read with the wrong encoding -- each of these returns a string rather than
/// failing, and the string is glyph indices, mojibake or stream residue. Indexed
/// as if it were prose, it produces chunks that match nothing and embeddings
/// that pull real queries towards noise, and nothing downstream can tell it
/// apart from a document that is genuinely about punctuation.
/// </summary>
public static class ExtractedText
{
    /// <summary>
    /// Shorter than this and the ratio below is meaningless -- a two-word
    /// heading is 100% letters and still is not a document.
    /// </summary>
    public const int MinimumCharacters = 20;

    /// <summary>
    /// Real prose in any script is overwhelmingly letters, digits and spaces.
    /// Measured against extraction failures: successful extracts sit well above
    /// 0.9, failures well below 0.5, and nothing observed landed near the line.
    /// </summary>
    public const double MinimumGoodRatio = 0.75;

    private static readonly Regex Good = new(@"[\p{L}\p{Nd}\s]", RegexOptions.Compiled);

    public static bool LooksLikeText(string? text)
    {
        var trimmed = text?.Trim();

        if (trimmed is null || trimmed.Length < MinimumCharacters)
        {
            return false;
        }

        return (double)Good.Matches(trimmed).Count / trimmed.Length >= MinimumGoodRatio;
    }

    /// <summary>
    /// Why a document was rejected, for the log. The count is the interesting
    /// part: "8% letters" tells someone their PDF is scanned, "too short" tells
    /// them the file is empty.
    /// </summary>
    public static string? RejectionReason(string? text)
    {
        var trimmed = text?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return "extraction produced nothing";
        }

        if (trimmed.Length < MinimumCharacters)
        {
            return $"only {trimmed.Length} characters extracted";
        }

        var ratio = (double)Good.Matches(trimmed).Count / trimmed.Length;

        return ratio >= MinimumGoodRatio
            ? null
            : $"{ratio:P0} of the extract is letters, digits or spaces - no text layer";
    }
}
