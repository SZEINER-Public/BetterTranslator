using System.Globalization;
using System.Text;

namespace BetterTranslator.Engine.Json;

/// <summary>
/// Writes a translated value back as JSON string content -- what goes between
/// the quotes, without them.
///
/// Hand-written rather than taken from a serializer because the requirement is
/// not "valid JSON" but "written the way this document writes JSON".
/// <c>JsonEncodedText</c> has two settings and neither is that: the default
/// escapes every non-ASCII character and also `&lt;`, `&gt;` and `&amp;`, which
/// would rewrite a file that had none of that, and the relaxed one escapes
/// nothing beyond the minimum, which would turn an all-escaped file into a
/// mixed one. The choice belongs to the source document, so it is passed in.
/// </summary>
public static class JsonEscape
{
    /// <param name="literalNonAscii">
    /// True to write accented and non-Latin characters as themselves, false to
    /// write them as `\uXXXX`. Read off the source document by
    /// <see cref="JsonSegmenter.WritesLiteralNonAscii"/>.
    /// </param>
    public static string Content(string value, bool literalNonAscii)
    {
        ArgumentNullException.ThrowIfNull(value);

        var text = new StringBuilder(value.Length + 8);

        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    text.Append("\\\"");
                    break;

                case '\\':
                    text.Append("\\\\");
                    break;

                case '\b':
                    text.Append("\\b");
                    break;

                case '\f':
                    text.Append("\\f");
                    break;

                case '\n':
                    text.Append("\\n");
                    break;

                case '\r':
                    text.Append("\\r");
                    break;

                case '\t':
                    text.Append("\\t");
                    break;

                default:
                    // Control characters have no literal form in JSON at all,
                    // whatever the document's convention is.
                    if (character < ' ' || (!literalNonAscii && character > '~'))
                    {
                        text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(character);
                    }

                    break;
            }
        }

        return text.ToString();
    }
}
