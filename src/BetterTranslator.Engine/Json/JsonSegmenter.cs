using System.Text;
using System.Text.Json;

namespace BetterTranslator.Engine.Json;

/// <summary>
/// Reads a JSON document and reports every scalar in it with the byte span its
/// raw text occupies.
///
/// A reader rather than a document object model, because a DOM has no source
/// positions: re-serialising one would reformat the file, reorder nothing but
/// re-indent everything, and lose the difference between minified and pretty
/// input. Spans mean the output is the input with a few ranges of bytes
/// replaced, and everything else is untouched by construction.
/// </summary>
public static class JsonSegmenter
{
    /// <summary>
    /// Comments and trailing commas are tolerated. Neither is standard JSON, but
    /// both are common in resource files that people actually paste, and reading
    /// past them costs nothing: they are not scalars, so no rule here can reach
    /// them and they survive the splice untouched.
    /// </summary>
    internal static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 128,
    };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 128,
    };

    /// <summary>
    /// Every scalar in the document, in the order it appears.
    ///
    /// Returns null when the text is not JSON at all, which is how the caller
    /// tells "nothing to translate here" from "not this path's business".
    /// </summary>
    public static IReadOnlyList<JsonScalar>? Segment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // A document opens with a brace or a bracket. Checking that first is not
        // an optimisation of the parse -- it is the difference between answering
        // "is this JSON" by looking and answering it by throwing. Measured: every
        // ordinary chat message rendered raised and swallowed two
        // JsonReaderExceptions, one per column, because the view binds both key
        // and value columns whether or not it shows them.
        var head = text.AsSpan().TrimStart();

        if (head.Length == 0 || (head[0] != '{' && head[0] != '['))
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(text);

        try
        {
            return Walk(bytes);
        }
        catch (JsonException)
        {
            // Opens like a document and is not one. Reached only by text that
            // passed the check above, so it is rare rather than routine.
            return null;
        }
    }

    private static List<JsonScalar> Walk(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        var scalars = new List<JsonScalar>();

        // The path to where the reader currently is, one segment per container.
        var segments = new List<string>();

        // Whether each open container is an array, and how many of its elements
        // have been passed. An object names its children; an array counts them.
        var frames = new List<(bool IsArray, int Index)>();

        string? pending = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    pending = reader.GetString();
                    break;

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    segments.Add(Name(ref pending, frames));
                    frames.Add((reader.TokenType == JsonTokenType.StartArray, 0));
                    break;

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    if (frames.Count > 0)
                    {
                        frames.RemoveAt(frames.Count - 1);
                    }

                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    break;

                case JsonTokenType.String:
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    scalars.Add(Scalar(ref reader, segments, Name(ref pending, frames)));
                    break;
            }
        }

        return scalars;
    }

    /// <summary>
    /// What to call the thing about to be read: the property name that
    /// introduced it, or its index in the array holding it.
    /// </summary>
    private static string Name(ref string? pending, List<(bool IsArray, int Index)> frames)
    {
        if (pending is not null)
        {
            var name = pending;
            pending = null;
            return name;
        }

        if (frames.Count > 0 && frames[^1].IsArray)
        {
            var index = frames[^1].Index;
            frames[^1] = (true, index + 1);
            return $"[{index}]";
        }

        return string.Empty;
    }

    private static JsonScalar Scalar(ref Utf8JsonReader reader, List<string> segments, string leaf)
    {
        var kind = reader.TokenType switch
        {
            JsonTokenType.String => JsonScalarKind.String,
            JsonTokenType.Number => JsonScalarKind.Number,
            JsonTokenType.True => JsonScalarKind.True,
            JsonTokenType.False => JsonScalarKind.False,
            _ => JsonScalarKind.Null,
        };

        // The raw text, not the token. For a string that is what sits between
        // the quotes, still escaped exactly as the document wrote it; the quotes
        // themselves stay put so the splice cannot unbalance them.
        var start = (int)reader.TokenStartIndex + (kind == JsonScalarKind.String ? 1 : 0);
        var length = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;

        var text = kind switch
        {
            JsonScalarKind.String => reader.GetString() ?? string.Empty,
            JsonScalarKind.True => "true",
            JsonScalarKind.False => "false",
            JsonScalarKind.Null => "null",
            _ => Encoding.UTF8.GetString(reader.ValueSpan),
        };

        var path = segments.Where(s => s.Length > 0).ToList();

        if (leaf.Length > 0)
        {
            path.Add(leaf);
        }

        return new JsonScalar(string.Join('.', path), segments.Count, kind, start, length, text);
    }

    /// <summary>
    /// A `\uXXXX` escape, not itself escaped. `\\u0041` is a backslash followed
    /// by the letters u041, which is not an escape at all.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex UnicodeEscape =
        new(@"(?<!\\)(?:\\\\)*\\u[0-9a-fA-F]{4}", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// True when a translated value should be written with its accents intact
    /// rather than as `\uXXXX`.
    ///
    /// Read off the source, so a file that chose one convention keeps it. Only
    /// two things count as choosing: a literal non-ASCII character somewhere, or
    /// a `\u` escape. A document with neither has expressed no preference -- an
    /// English resource file is all ASCII because English is, not because
    /// somebody wanted escapes -- and gets the modern default, which is literal
    /// UTF-8. Escaping there would turn one Czech translation into a wall of
    /// `č` that no reviewer can read.
    /// </summary>
    public static bool PrefersLiteralNonAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var character in text)
        {
            if (character > 127)
            {
                return true;
            }
        }

        return !UnicodeEscape.IsMatch(text);
    }
}
