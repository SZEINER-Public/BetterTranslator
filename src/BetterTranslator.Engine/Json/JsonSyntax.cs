using System.Text.Json;

namespace BetterTranslator.Engine.Json;

/// <summary>Whether a message is a JSON document, and whether it holds anything to translate.</summary>
public static class JsonSyntax
{
    /// <summary>
    /// Two conditions, both required. It has to open the way a JSON document
    /// opens, so an ordinary sentence is never parsed speculatively, and it has
    /// to actually parse, so a message that merely begins with a brace falls
    /// through to the path it would have taken before this existed.
    /// </summary>
    public static bool LooksLikeJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.AsSpan().Trim();

        if (trimmed.Length == 0 || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text, JsonSegmenter.Options);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// True when the JSON path has something to do. A document of nothing but
    /// numbers, or one whose every string is a slot or a colour code, parses
    /// fine and has no language in it; routing it here would report a failure
    /// for a document that was never going to change.
    /// </summary>
    public static bool HasTranslatableValues(string? text)
    {
        if (!LooksLikeJson(text))
        {
            return false;
        }

        var scalars = JsonSegmenter.Segment(text!);

        return scalars is not null
            && scalars.Any(s => s.IsString && JsonValueGuard.WorthSending(s.Text));
    }
}
