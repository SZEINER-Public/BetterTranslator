using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Verification.Structure;

namespace BetterTranslator.Engine.Documents;

public sealed class ProseStructure : IFormatAdapter
{
    public const string FormatName = "prose";

    private static readonly IReadOnlyDictionary<string, string> Blank = new Dictionary<string, string> { ["blank"] = "true" };

    private static readonly IReadOnlyDictionary<string, string> Filled = new Dictionary<string, string> { ["blank"] = "false" };

    public static ProseStructure Instance { get; } = new();

    public string Format => FormatName;

    public DocumentModel Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var nodes = new List<DocumentNode>
        {
            new(DocumentModel.RootPath, string.Empty, DocumentNodeKind.Document, new CheckRange(DocumentModel.RootPath, 0, text.Length), 0, Filled),
        };

        var chunks = new List<ChunkNode>();
        var invariants = new List<InvariantToken>();
        var lineStart = 0;
        var index = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] != '\n')
            {
                continue;
            }

            var end = i;

            if (end > lineStart && text[end - 1] == '\r')
            {
                end--;
            }

            var path = "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var range = new CheckRange(path, lineStart, end - lineStart);
            var blank = IsBlank(text, lineStart, end);

            nodes.Add(new DocumentNode(path, DocumentModel.RootPath, DocumentNodeKind.Line, range, 1, blank ? Blank : Filled));
            chunks.Add(new ChunkNode(blank ? "blank" : "line", "line", range));

            if (!blank)
            {
                invariants.AddRange(InvariantScanner.Scan(text.Substring(lineStart, end - lineStart), path, lineStart));
            }

            lineStart = i + 1;
            index++;
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

    private static bool IsBlank(string text, int start, int end)
    {
        for (var i = start; i < end; i++)
        {
            if (!char.IsWhiteSpace(text[i]))
            {
                return false;
            }
        }

        return true;
    }
}
