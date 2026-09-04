using System.Text;
using System.Text.Json;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Text;
using BetterTranslator.Engine.Verification.Structure;

namespace BetterTranslator.Engine.Json;

public sealed class JsonStructure : IFormatAdapter
{
    public const string FormatName = "json";

    public const string MalformedAttribute = "malformed";

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 128,
    };

    public static JsonStructure Instance { get; } = new();

    public string Format => FormatName;

    public DocumentModel Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var nodes = new List<DocumentNode>();
        var chunks = new List<ChunkNode>();
        var invariants = new List<InvariantToken>();
        var rootAttributes = new Dictionary<string, string>();

        nodes.Add(new DocumentNode(
            DocumentModel.RootPath,
            string.Empty,
            DocumentNodeKind.Document,
            new CheckRange(DocumentModel.RootPath, 0, text.Length),
            0,
            rootAttributes));

        try
        {
            Walk(text, nodes, chunks, invariants);
        }
        catch (JsonException)
        {
            nodes.RemoveRange(1, nodes.Count - 1);
            chunks.Clear();
            invariants.Clear();
            rootAttributes[MalformedAttribute] = "true";
        }

        return new DocumentModel
        {
            Format = FormatName,
            Length = text.Length,
            Nodes = nodes,
            Chunks = chunks,
            Invariants = invariants,
            Placeholders = [.. PlaceholderGuard.Residue(text)
                .Select(r => new PlaceholderResidue(new CheckRange(DocumentModel.RootPath, r.Index, r.Length), r.Text))],
        };
    }

    private sealed class Frame(string path, bool isArray, Dictionary<string, string> attributes)
    {
        public string Path { get; } = path;

        public bool IsArray { get; } = isArray;

        public Dictionary<string, string> Attributes { get; } = attributes;

        public int Count { get; set; }

        public Dictionary<string, int> Names { get; } = new(StringComparer.Ordinal);
    }

    private static void Walk(string text, List<DocumentNode> nodes, List<ChunkNode> chunks, List<InvariantToken> invariants)
    {
        var head = text.AsSpan().TrimStart();

        if (head.Length == 0 || (head[0] != '{' && head[0] != '['))
        {
            throw new JsonException();
        }

        var bytes = Encoding.UTF8.GetBytes(text);
        var offsets = Utf8Offsets.Of(text);
        var reader = new Utf8JsonReader(bytes, ReaderOptions);
        var frames = new List<Frame>();
        string? pendingKey = null;
        CheckRange? pendingKeyRange = null;

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    pendingKey = Encoding.UTF8.GetString(reader.ValueSpan);
                    var (keyStart, keyLength) = offsets.CharRange((int)reader.TokenStartIndex, reader.ValueSpan.Length + 2);
                    pendingKeyRange = new CheckRange(string.Empty, keyStart, keyLength);
                    break;

                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                {
                    var (path, parent) = Name(frames, ref pendingKey, ref pendingKeyRange, nodes);
                    var attributes = new Dictionary<string, string>();
                    var isArray = reader.TokenType == JsonTokenType.StartArray;
                    var (start, _) = offsets.CharRange((int)reader.TokenStartIndex, 1);

                    nodes.Add(new DocumentNode(
                        path,
                        parent,
                        isArray ? DocumentNodeKind.JsonArray : DocumentNodeKind.JsonObject,
                        new CheckRange(path, start, 0),
                        frames.Count + 1,
                        attributes));

                    frames.Add(new Frame(path, isArray, attributes));
                    break;
                }

                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                {
                    if (frames.Count == 0)
                    {
                        break;
                    }

                    var frame = frames[^1];
                    frames.RemoveAt(frames.Count - 1);
                    frame.Attributes[frame.IsArray ? "length" : "keys"] = frame.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

                    var index = nodes.FindLastIndex(n => string.Equals(n.Path, frame.Path, StringComparison.Ordinal) && n.Kind is DocumentNodeKind.JsonArray or DocumentNodeKind.JsonObject);

                    if (index >= 0)
                    {
                        var opened = nodes[index];
                        var (end, _) = offsets.CharRange((int)reader.TokenStartIndex + 1, 0);
                        nodes[index] = opened with { Range = new CheckRange(opened.Path, opened.Range.Offset, Math.Max(0, end - opened.Range.Offset)) };
                    }

                    break;
                }

                case JsonTokenType.String:
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                {
                    var (path, parent) = Name(frames, ref pendingKey, ref pendingKeyRange, nodes);
                    var isString = reader.TokenType == JsonTokenType.String;
                    var byteStart = (int)reader.TokenStartIndex + (isString ? 1 : 0);
                    var byteLength = reader.HasValueSequence ? (int)reader.ValueSequence.Length : reader.ValueSpan.Length;
                    var (start, length) = offsets.CharRange(byteStart, byteLength);
                    var range = new CheckRange(path, start, length);

                    nodes.Add(new DocumentNode(
                        path,
                        parent,
                        DocumentNodeKind.JsonValue,
                        range,
                        frames.Count + 1,
                        new Dictionary<string, string> { ["type"] = reader.TokenType.ToString() }));

                    if (isString)
                    {
                        chunks.Add(new ChunkNode(path, "string", range));
                        invariants.AddRange(InvariantScanner.Scan(text.Substring(start, length), path, start));
                    }

                    break;
                }
            }
        }
    }

    private static (string Path, string Parent) Name(
        List<Frame> frames,
        ref string? pendingKey,
        ref CheckRange? pendingKeyRange,
        List<DocumentNode> nodes)
    {
        if (frames.Count == 0)
        {
            return ("/$", DocumentModel.RootPath);
        }

        var frame = frames[^1];
        frame.Count++;

        string segment;

        if (pendingKey is not null)
        {
            var seen = frame.Names.GetValueOrDefault(pendingKey);
            frame.Names[pendingKey] = seen + 1;
            var escaped = pendingKey.Replace("~", "~0", StringComparison.Ordinal)
                .Replace("/", "~1", StringComparison.Ordinal)
                .Replace("@", "~2", StringComparison.Ordinal)
                .Replace("#", "~3", StringComparison.Ordinal);
            segment = seen == 0 ? escaped : escaped + "#" + seen.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var path = frame.Path + "/" + segment;

            nodes.Add(new DocumentNode(
                path + "@key",
                frame.Path,
                DocumentNodeKind.JsonProperty,
                new CheckRange(path + "@key", pendingKeyRange!.Offset, pendingKeyRange.Length),
                frames.Count,
                new Dictionary<string, string> { ["key"] = pendingKey }));

            pendingKey = null;
            pendingKeyRange = null;
            return (path, frame.Path);
        }

        segment = (frame.Count - 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return (frame.Path + "/" + segment, frame.Path);
    }
}
