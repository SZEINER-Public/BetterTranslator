using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace BetterTranslator.Indexing.Markdown;

/// <summary>
/// Turns Markdown into the block model the preview renders. Markdig does the
/// parsing; this only maps its syntax tree onto types a DataTemplate can bind
/// to, so no Markdown viewer control is needed.
/// </summary>
public static class MarkdownBlockParser
{
    // UseAdvancedExtensions is what makes pipe tables parse.
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    public static IReadOnlyList<MdBlock> Parse(string markdown)
    {
        var document = Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);
        return MapBlocks(document);
    }

    private static List<MdBlock> MapBlocks(IEnumerable<Markdig.Syntax.Block> source)
    {
        var blocks = new List<MdBlock>();

        foreach (var block in source)
        {
            switch (block)
            {
                case Markdig.Syntax.HeadingBlock heading:
                    blocks.Add(new MdHeading(heading.Level, MapInlines(heading.Inline)));
                    break;

                case Markdig.Syntax.ParagraphBlock paragraph:
                    blocks.Add(new MdParagraph(MapInlines(paragraph.Inline)));
                    break;

                case Markdig.Syntax.ListBlock list:
                    blocks.Add(new MdBulletList(MapListItems(list), list.IsOrdered));
                    break;

                case Markdig.Syntax.FencedCodeBlock fenced:
                    blocks.Add(new MdCodeBlock(ReadCode(fenced), fenced.Info));
                    break;

                case Markdig.Syntax.CodeBlock code:
                    blocks.Add(new MdCodeBlock(ReadCode(code)));
                    break;

                case Table table:
                    blocks.Add(MapTable(table));
                    break;

                case Markdig.Syntax.QuoteBlock quote:
                    blocks.Add(new MdQuote(MapBlocks(quote)));
                    break;

                case Markdig.Syntax.ThematicBreakBlock:
                    blocks.Add(new MdThematicBreak());
                    break;

                // Raw HTML. There is no renderer for it here and there should
                // not be one, but dropping it silently left a document made
                // entirely of HTML rendering as a blank pane. Shown as the text
                // it is, so nothing in the message is ever simply missing.
                case Markdig.Syntax.HtmlBlock html:
                    blocks.Add(new MdCodeBlock(ReadCode(html)));
                    break;
            }
        }

        return blocks;
    }

    private static List<MdListItem> MapListItems(Markdig.Syntax.ListBlock list)
    {
        var items = new List<MdListItem>();
        var number = list.OrderedStart is { Length: > 0 } start
            && int.TryParse(start, System.Globalization.CultureInfo.InvariantCulture, out var first)
            ? first
            : 1;

        foreach (var child in list)
        {
            if (child is not Markdig.Syntax.ListItemBlock item)
            {
                continue;
            }

            // An item's own text lives in its first paragraph; anything after
            // that -- a nested list, a code block, a second paragraph -- is a
            // block of its own and is kept as one rather than flattened away.
            var inlines = new List<MdInline>();
            var children = new List<MdBlock>();
            var done = (bool?)null;
            var head = true;

            foreach (var itemBlock in item)
            {
                if (head && itemBlock is Markdig.Syntax.ParagraphBlock paragraph)
                {
                    done = TaskState(paragraph.Inline);
                    inlines.AddRange(MapInlines(paragraph.Inline));

                    // The box inline covers `[x]` but not the space after it, so
                    // the text starts with one. Rendered beside a drawn box that
                    // reads as a stray indent.
                    if (done is not null && inlines.Count > 0)
                    {
                        inlines[0] = inlines[0] with { Text = inlines[0].Text.TrimStart() };
                    }

                    head = false;
                    continue;
                }

                children.AddRange(MapBlocks([itemBlock]));
            }

            var marker = list.IsOrdered
                ? string.Create(System.Globalization.CultureInfo.CurrentCulture, $"{number}.")
                : "•";

            items.Add(new MdListItem(marker, inlines, children, done));
            number++;
        }

        return items;
    }

    /// <summary>
    /// Whether the item opens with a task box, and whether it is ticked. The
    /// extension puts the box in the inline stream, so it is read from there
    /// rather than by looking at the source characters.
    /// </summary>
    private static bool? TaskState(ContainerInline? container) =>
        container?.FirstChild is Markdig.Extensions.TaskLists.TaskList task ? task.Checked : null;

    private static MdTable MapTable(Table table)
    {
        var rows = new List<IReadOnlyList<string>>();
        var hasHeader = false;

        foreach (var child in table)
        {
            if (child is not TableRow row)
            {
                continue;
            }

            if (row.IsHeader)
            {
                hasHeader = true;
            }

            var cells = new List<string>();
            foreach (var cellChild in row)
            {
                if (cellChild is TableCell cell)
                {
                    cells.Add(PlainText(cell));
                }
            }

            rows.Add(cells);
        }

        return new MdTable(rows, hasHeader);
    }

    private static string PlainText(Markdig.Syntax.ContainerBlock container)
    {
        var text = new StringBuilder();

        foreach (var block in container)
        {
            if (block is Markdig.Syntax.LeafBlock leaf && leaf.Inline is not null)
            {
                foreach (var inline in MapInlines(leaf.Inline))
                {
                    text.Append(inline.Text);
                }
            }
        }

        return text.ToString().Trim();
    }

    private static string ReadCode(Markdig.Syntax.LeafBlock block)
    {
        if (block.Lines.Lines is null)
        {
            return string.Empty;
        }

        var text = new StringBuilder();
        for (var i = 0; i < block.Lines.Count; i++)
        {
            text.AppendLine(block.Lines.Lines[i].Slice.ToString());
        }

        return text.ToString().TrimEnd('\r', '\n');
    }

    private static List<MdInline> MapInlines(ContainerInline? container)
    {
        var inlines = new List<MdInline>();

        if (container is not null)
        {
            Walk(container, bold: false, italic: false, link: null, inlines);
        }

        return inlines;
    }

    private static void Walk(ContainerInline container, bool bold, bool italic, string? link, List<MdInline> into)
    {
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    Append(into, literal.Content.ToString(), bold, italic, code: false, link);
                    break;

                case CodeInline code:
                    Append(into, code.Content, bold, italic, code: true, link);
                    break;

                case EmphasisInline emphasis:
                    // Markdig reports the run length: two or more is strong.
                    var strong = emphasis.DelimiterCount >= 2;
                    Walk(emphasis, bold || strong, italic || !strong, link, into);
                    break;

                case LineBreakInline:
                    Append(into, " ", bold, italic, code: false, link);
                    break;

                // An image has no picture here: the preview and the chat both
                // render text. Its alt text is what it was written for, so that
                // is what shows, and the target is not offered as a link the
                // reader can follow into a download.
                case LinkInline { IsImage: true } image:
                    Walk(image, bold, italic, link: null, into);
                    break;

                case LinkInline anchor:
                    Walk(anchor, bold, italic, anchor.Url, into);
                    break;

                // The box is drawn by the item's own template. Left here it
                // would arrive as the literal text "[x]".
                case Markdig.Extensions.TaskLists.TaskList:
                    break;

                case ContainerInline nested:
                    Walk(nested, bold, italic, link, into);
                    break;
            }
        }
    }

    /// <summary>
    /// Merges a run into the previous one when the marks match, so a paragraph
    /// does not become one MdInline per character.
    /// </summary>
    private static void Append(List<MdInline> into, string text, bool bold, bool italic, bool code, string? link)
    {
        if (text.Length == 0)
        {
            return;
        }

        if (into.Count > 0)
        {
            var last = into[^1];
            if (last.Bold == bold && last.Italic == italic && last.Code == code
                && string.Equals(last.Link, link, StringComparison.Ordinal))
            {
                into[^1] = last with { Text = last.Text + text };
                return;
            }
        }

        into.Add(new MdInline(text, bold, italic, code, link));
    }
}
