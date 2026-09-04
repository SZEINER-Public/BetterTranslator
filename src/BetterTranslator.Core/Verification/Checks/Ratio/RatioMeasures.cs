using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace BetterTranslator.Core.Verification.Checks.Ratio;

public static partial class RatioMeasures
{
    private const int MaxRepeatedGram = 4;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex(@"[.!?…]+(?=\s|$)")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"^(to translate\b|the provided text\b|this text\b|the text is\b|i will\b|i have\b|here is\b|here's\b|here are\b|sure[,!]|certainly[,!]|of course[,!]|note that\b|as an ai\b|the user\b|system\s*:|translation\s*:|translated text\s*:|zde je p[řr]eklad|p[řr]eklad\s*:|p[řr]elo[žz]en[ýy] text\s*:|jist[ěe][,!]|samoz[řr]ejm[ěe][,!]|toto je p[řr]eklad)", RegexOptions.IgnoreCase)]
    private static partial Regex Opener();

    [GeneratedRegex(@"(p[řr]elo[žz]|p[řr]eklad|translat\w*|instrukc|instruction\w*|as requested|jak bylo po[žz]adov[áa]no)", RegexOptions.IgnoreCase)]
    private static partial Regex Meta();

    public static string Collapse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Whitespace().Replace(text, " ").Trim();
    }

    public static IReadOnlyList<string> Words(string text) =>
        [.. WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant())];

    public static double LengthRatio(string source, string target)
    {
        var s = Collapse(source).Length;
        var t = Collapse(target).Length;

        return s == 0 ? 0 : (double)t / s;
    }

    public static int RepetitionRun(string text)
    {
        var words = Words(text);
        var longest = words.Count == 0 ? 0 : 1;

        for (var n = 1; n <= MaxRepeatedGram; n++)
        {
            var i = 0;

            while (i + n <= words.Count)
            {
                var run = 1;

                while (i + (run + 1) * n <= words.Count && SameGram(words, i, i + run * n, n))
                {
                    run++;
                }

                longest = Math.Max(longest, run);
                i += run > 1 ? run * n : 1;
            }
        }

        return longest;
    }

    public static double CompressionRatio(string text)
    {
        var raw = Encoding.UTF8.GetBytes(Collapse(text));

        if (raw.Length == 0)
        {
            return 0;
        }

        using var buffer = new MemoryStream();

        using (var deflate = new DeflateStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        return (double)raw.Length / Math.Max(1, buffer.Length);
    }

    public static int SentenceCount(string text)
    {
        var collapsed = Collapse(text);

        if (collapsed.Length == 0)
        {
            return 0;
        }

        var ends = SentenceEnd().Matches(collapsed).Count;

        return EndsWithTerminal(collapsed) ? Math.Max(1, ends) : ends + 1;
    }

    public static bool EndsWithTerminal(string text)
    {
        var trimmed = Collapse(text).TrimEnd('"', '\'', '»', '«', ')', ']', '”', '’', '*', '_', '`');

        return trimmed.Length > 0 && trimmed[^1] is '.' or '!' or '?' or '…' or ':' or ';';
    }

    public static string? InsertionMatch(string source, string target)
    {
        var collapsed = Collapse(target);
        var opener = Opener().Match(collapsed);

        if (opener.Success)
        {
            return opener.Value;
        }

        var meta = Meta().Match(collapsed);

        return meta.Success && !Meta().IsMatch(source) ? meta.Value : null;
    }

    private static bool SameGram(IReadOnlyList<string> words, int a, int b, int n)
    {
        for (var k = 0; k < n; k++)
        {
            if (!string.Equals(words[a + k], words[b + k], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
