using System.Text;

namespace BetterTranslator.Core.Verification.Checks.Coverage;

public static class CoverageText
{
    public static string Slice(DocumentModel model, CheckRange range)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(range);

        var text = model.Text;
        var offset = Math.Clamp(range.Offset, 0, text.Length);
        var length = Math.Clamp(range.Length, 0, text.Length - offset);

        return text.Substring(offset, length);
    }

    public static bool IsBlank(string text) => text.All(char.IsWhiteSpace);

    public static bool HasLetter(string text) => text.Any(char.IsLetter);

    public static string Visible(string text, CheckRange range, IEnumerable<CheckRange> hidden)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(range);

        var builder = new StringBuilder(range.Length);
        var end = Math.Min(text.Length, range.End);
        var spans = hidden.Where(h => h.Overlaps(range)).OrderBy(h => h.Offset).ToList();

        for (var i = Math.Max(0, range.Offset); i < end; i++)
        {
            var covered = false;

            foreach (var span in spans)
            {
                if (i >= span.Offset && i < span.End)
                {
                    covered = true;
                    break;
                }
            }

            builder.Append(covered ? ' ' : text[i]);
        }

        return builder.ToString();
    }

    public static string RemoveTexts(string text, IEnumerable<string> needles)
    {
        ArgumentNullException.ThrowIfNull(text);

        var result = text;

        foreach (var needle in needles.Where(n => n.Length > 0).OrderByDescending(n => n.Length).ThenBy(n => n, StringComparer.Ordinal))
        {
            var at = result.IndexOf(needle, StringComparison.Ordinal);

            if (at >= 0)
            {
                result = result[..at] + new string(' ', needle.Length) + result[(at + needle.Length)..];
            }
        }

        return result;
    }

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var builder = new StringBuilder(text.Length);
        var pendingSpace = false;

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(c));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    public static int LetterCount(string text) => text.Count(char.IsLetter);
}
