namespace BetterTranslator.Core.Verification.Checks;

public enum DocumentNodeKind
{
    Document,
    Paragraph,
    Heading,
    CodeFence,
    List,
    ListItem,
    Table,
    TableRow,
    TableCell,
    Link,
    Image,
    BlockQuote,
    FrontMatter,
    HtmlBlock,
    ThematicBreak,
    Line,
    JsonObject,
    JsonArray,
    JsonProperty,
    JsonValue,
    Cue,
}

public enum DelimiterClass
{
    Start,
    End,
    Whitespace,
    Punctuation,
    Letter,
    Digit,
}

public enum InvariantClass
{
    Number,
    Date,
    Version,
    Currency,
    Url,
    FilePath,
    Identifier,
}

public sealed record DocumentNode(
    string Path,
    string ParentPath,
    DocumentNodeKind Kind,
    CheckRange Range,
    int Depth,
    IReadOnlyDictionary<string, string> Attributes)
{
    public string Attribute(string name) => Attributes.TryGetValue(name, out var value) ? value : string.Empty;
}

public sealed record ChunkNode(string Identity, string Kind, CheckRange Range);

public sealed record PlaceholderResidue(CheckRange Range, string Text);

public sealed record InvariantToken(InvariantClass Class, string Text, CheckRange Range);

public sealed record DocumentModel
{
    public const string RootPath = "/";

    public required string Format { get; init; }

    public int Length { get; init; }

    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<DocumentNode> Nodes { get; init; } = [];

    public IReadOnlyList<ChunkNode> Chunks { get; init; } = [];

    public IReadOnlyList<PlaceholderResidue> Placeholders { get; init; } = [];

    public IReadOnlyList<InvariantToken> Invariants { get; init; } = [];

    public CheckRange Whole => new(RootPath, 0, Length);

    public IEnumerable<DocumentNode> OfKind(DocumentNodeKind kind) => Nodes.Where(node => node.Kind == kind);

    public IEnumerable<DocumentNode> ChildrenOf(DocumentNode parent) =>
        Nodes.Where(node => string.Equals(node.ParentPath, parent.Path, StringComparison.Ordinal));
}
