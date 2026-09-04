using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Engine.Markup;
using BetterTranslator.Engine.Verification.Structure;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BetterTranslator.Engine.Markdown;

public sealed class MarkdownStructure : IFormatAdapter
{
    public const string FormatName = "markdown";

    private static readonly IReadOnlyDictionary<string, string> NoAttributes = new Dictionary<string, string>();

    public static MarkdownStructure Instance { get; } = new();

    public string Format => FormatName;

    public DocumentModel Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var document = MarkdownSyntax.Parse(text);
        var builder = new Builder(text);

        builder.Nodes.Add(new DocumentNode(
            DocumentModel.RootPath,
            string.Empty,
            DocumentNodeKind.Document,
            new CheckRange(DocumentModel.RootPath, 0, text.Length),
            0,
            NoAttributes));

        builder.Walk(document, DocumentModel.RootPath, string.Empty, 0, 0);

        return new DocumentModel
        {
            Format = FormatName,
            Length = text.Length,
            Nodes = builder.Nodes,
            Chunks = builder.Chunks,
            Invariants = builder.Invariants,
            Placeholders = [.. PlaceholderGuard.Residue(text)
                .Select(r => new PlaceholderResidue(new CheckRange(DocumentModel.RootPath, r.Index, r.Length), r.Text))],
        };
    }

    private sealed class Builder(string text)
    {
        public List<DocumentNode> Nodes { get; } = [];

        public List<ChunkNode> Chunks { get; } = [];

        public List<InvariantToken> Invariants { get; } = [];

        public void Walk(ContainerBlock container, string path, string kinds, int depth, int quoteDepth)
        {
            var index = 0;

            foreach (var block in container)
            {
                var childPath = path == DocumentModel.RootPath ? "/" + index : path + "/" + index;
                index++;

                var range = new CheckRange(childPath, Start(block), Length(block));

                switch (block)
                {
                    case YamlFrontMatterBlock:
                        Add(childPath, path, DocumentNodeKind.FrontMatter, range, depth, NoAttributes);
                        break;

                    case HeadingBlock heading:
                        Add(childPath, path, DocumentNodeKind.Heading, range, depth, One("level", heading.Level.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        Chunk(childPath, Join(kinds, "heading"), range);
                        Inlines(heading, childPath, depth + 1);
                        break;

                    case ParagraphBlock paragraph:
                        Add(childPath, path, DocumentNodeKind.Paragraph, range, depth, NoAttributes);
                        Chunk(childPath, Join(kinds, "paragraph"), range);
                        Inlines(paragraph, childPath, depth + 1);
                        break;

                    case FencedCodeBlock fence:
                        Add(childPath, path, DocumentNodeKind.CodeFence, range, depth, new Dictionary<string, string>
                        {
                            ["language"] = fence.Info ?? string.Empty,
                            ["fence"] = new string(fence.FencedChar, Math.Max(1, fence.OpeningFencedCharCount)),
                        });
                        break;

                    case CodeBlock:
                        Add(childPath, path, DocumentNodeKind.CodeFence, range, depth, new Dictionary<string, string>
                        {
                            ["language"] = string.Empty,
                            ["fence"] = "indent",
                        });
                        break;

                    case ListBlock list:
                        Add(childPath, path, DocumentNodeKind.List, range, depth, new Dictionary<string, string>
                        {
                            ["ordered"] = list.IsOrdered ? "true" : "false",
                            ["items"] = list.Count(child => child is ListItemBlock).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["start"] = list.OrderedStart ?? string.Empty,
                        });
                        Walk(list, childPath, Join(kinds, "list"), depth + 1, quoteDepth);
                        break;

                    case ListItemBlock item:
                        Add(childPath, path, DocumentNodeKind.ListItem, range, depth, NoAttributes);
                        Walk(item, childPath, Join(kinds, "item"), depth + 1, quoteDepth);
                        break;

                    case QuoteBlock quote:
                        Add(childPath, path, DocumentNodeKind.BlockQuote, range, depth, One("depth", (quoteDepth + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        Walk(quote, childPath, Join(kinds, "quote"), depth + 1, quoteDepth + 1);
                        break;

                    case Table table:
                        Add(childPath, path, DocumentNodeKind.Table, range, depth, new Dictionary<string, string>
                        {
                            ["rows"] = table.Count(child => child is TableRow).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["columns"] = Columns(table).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["delimiter"] = DelimiterRow(table),
                        });
                        Walk(table, childPath, Join(kinds, "table"), depth + 1, quoteDepth);
                        break;

                    case TableRow row:
                        Add(childPath, path, DocumentNodeKind.TableRow, range, depth, new Dictionary<string, string>
                        {
                            ["cells"] = WrittenCells(row).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["header"] = row.IsHeader ? "true" : "false",
                        });
                        Walk(row, childPath, Join(kinds, "row"), depth + 1, quoteDepth);
                        break;

                    case TableCell cell:
                        Add(childPath, path, DocumentNodeKind.TableCell, range, depth, NoAttributes);
                        Walk(cell, childPath, Join(kinds, "cell"), depth + 1, quoteDepth);
                        break;

                    case HtmlBlock:
                        Add(childPath, path, DocumentNodeKind.HtmlBlock, range, depth, NoAttributes);
                        break;

                    case ThematicBreakBlock:
                        Add(childPath, path, DocumentNodeKind.ThematicBreak, range, depth, NoAttributes);
                        break;

                    case ContainerBlock nested:
                        Walk(nested, childPath, kinds, depth + 1, quoteDepth);
                        break;
                }
            }
        }

        private void Inlines(LeafBlock block, string path, int depth)
        {
            var ordinal = 0;
            Links(block.Inline, path, depth, ref ordinal);
        }

        private void Links(ContainerInline? container, string path, int depth, ref int ordinal)
        {
            if (container is null)
            {
                return;
            }

            foreach (var inline in container)
            {
                if (inline is LinkInline link)
                {
                    var inlinePath = path + "/i" + ordinal;
                    ordinal++;

                    Add(
                        inlinePath,
                        path,
                        link.IsImage ? DocumentNodeKind.Image : DocumentNodeKind.Link,
                        new CheckRange(inlinePath, Clamp(link.Span.Start), Math.Max(0, Math.Min(link.Span.Length, text.Length - Clamp(link.Span.Start)))),
                        depth,
                        new Dictionary<string, string>
                        {
                            ["url"] = link.Url ?? string.Empty,
                            ["autolink"] = link.IsAutoLink ? "true" : "false",
                        });
                }

                if (inline is ContainerInline nested)
                {
                    Links(nested, path, depth, ref ordinal);
                }
            }
        }

        private void Add(string path, string parent, DocumentNodeKind kind, CheckRange range, int depth, IReadOnlyDictionary<string, string> attributes) =>
            Nodes.Add(new DocumentNode(path, parent, kind, range, depth, attributes));

        private void Chunk(string path, string identity, CheckRange range)
        {
            Chunks.Add(new ChunkNode(identity, identity, range));
            Invariants.AddRange(InvariantScanner.Scan(text.Substring(range.Offset, range.Length), path, range.Offset));
        }

        private int Start(Block block) => Clamp(block.Span.Start);

        private int Length(Block block)
        {
            var start = Start(block);
            var end = Math.Min(block.Span.End + 1, text.Length);
            return Math.Max(0, end - start);
        }

        private int Clamp(int offset) => Math.Clamp(offset, 0, text.Length);

        private static int Columns(Table table) => table.ColumnDefinitions.Count;

        private int WrittenCells(TableRow row)
        {
            var start = Start(row);
            var lineStart = text.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            var lineEnd = text.IndexOf('\n', start);

            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var line = text[lineStart..lineEnd].Trim();
            var pipes = 0;
            var inCode = false;

            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] == '`')
                {
                    inCode = !inCode;
                }
                else if (line[i] == '|' && !inCode && (i == 0 || line[i - 1] != '\\'))
                {
                    pipes++;
                }
            }

            var cells = pipes + 1;

            if (line.StartsWith('|'))
            {
                cells--;
            }

            if (line.Length > 1 && line.EndsWith('|') && line[^2] != '\\')
            {
                cells--;
            }

            return Math.Max(0, cells);
        }

        private string DelimiterRow(Table table)
        {
            var firstBreak = text.IndexOf('\n', Start(table));

            if (firstBreak < 0)
            {
                return string.Empty;
            }

            var lineStart = firstBreak + 1;
            var end = text.IndexOf('\n', lineStart);

            if (end < 0)
            {
                end = text.Length;
            }

            if (end > lineStart && text[end - 1] == '\r')
            {
                end--;
            }

            return end > lineStart ? text[lineStart..end] : string.Empty;
        }

        private static string Join(string kinds, string kind) => kinds.Length == 0 ? kind : kinds + "/" + kind;

        private static IReadOnlyDictionary<string, string> One(string name, string value) =>
            new Dictionary<string, string> { [name] = value };
    }
}
