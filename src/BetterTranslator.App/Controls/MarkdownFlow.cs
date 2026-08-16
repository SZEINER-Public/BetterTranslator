using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterTranslator.App.Services;
using BetterTranslator.Indexing.Markdown;

namespace BetterTranslator.App.Controls;

/// <summary>
/// Renders the block model into a FlowDocument, so the formatted view can be
/// selected across blocks the way any document can.
///
/// The DataTemplates it replaces produced one TextBlock per block, and WPF
/// cannot select a TextBlock at all: a reader looking at a translated file could
/// see the text and had no way to take a line of it. A FlowDocument in a
/// read-only RichTextBox is the only shape in WPF that keeps the marks and the
/// selection at once.
///
/// Every value still comes from a token, and the inline marks come from
/// <see cref="MarkdownInlines"/>, which the TextBlock renderer also uses.
/// </summary>
public static class MarkdownFlow
{
    public static readonly DependencyProperty BlocksProperty = DependencyProperty.RegisterAttached(
        "Blocks",
        typeof(IEnumerable<MdBlock>),
        typeof(MarkdownFlow),
        new PropertyMetadata(null, OnBlocksChanged));

    public static void SetBlocks(DependencyObject target, IEnumerable<MdBlock>? value) =>
        target.SetValue(BlocksProperty, value);

    public static IEnumerable<MdBlock>? GetBlocks(DependencyObject target) =>
        (IEnumerable<MdBlock>?)target.GetValue(BlocksProperty);

    private static void OnBlocksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is RichTextBox box)
        {
            box.Document = Build(e.NewValue as IEnumerable<MdBlock>);
        }
    }

    public static FlowDocument Build(IEnumerable<MdBlock>? blocks)
    {
        var document = new FlowDocument
        {
            FontFamily = Tokens.Get<FontFamily>("FontFamilyUi"),
            FontSize = Tokens.Number("TextBodySize"),
            Foreground = Tokens.Get<Brush>("BrushText"),
            LineHeight = Tokens.Number("ProseLineHeight"),
            PagePadding = new Thickness(0),
        };

        foreach (var block in blocks ?? [])
        {
            foreach (var rendered in Render(block, depth: 0))
            {
                document.Blocks.Add(rendered);
            }
        }

        return document;
    }

    private static IEnumerable<Block> Render(MdBlock block, int depth)
    {
        switch (block)
        {
            case MdHeading heading:
                yield return Heading(heading);
                break;

            case MdParagraph paragraph:
                yield return Prose(paragraph.Inlines, new Thickness(0, 0, 0, 10));
                break;

            case MdBulletList list:
                foreach (var item in list.Items)
                {
                    foreach (var rendered in Item(item, depth))
                    {
                        yield return rendered;
                    }
                }

                break;

            case MdCodeBlock code:
                yield return Code(code);
                break;

            case MdTable table:
                yield return Grid(table);
                break;

            case MdQuote quote:
                yield return Quote(quote, depth);
                break;

            case MdThematicBreak:
                yield return Rule();
                break;
        }
    }

    private static Paragraph Heading(MdHeading heading)
    {
        var paragraph = new Paragraph
        {
            FontWeight = Tokens.Get<FontWeight>("TextHeadingWeight"),
            FontSize = heading.Level switch
            {
                1 => Tokens.Number("TextTitleSize"),
                3 => Tokens.Number("TextSubheadingSize"),
                _ => Tokens.Number("TextHeadingSize"),
            },
            Margin = new Thickness(0, 14, 0, 6),
        };

        MarkdownInlines.Fill(paragraph.Inlines, heading.Inlines);

        return paragraph;
    }

    private static Paragraph Prose(IEnumerable<MdInline> inlines, Thickness margin)
    {
        var paragraph = new Paragraph { Margin = margin };

        MarkdownInlines.Fill(paragraph.Inlines, inlines);

        return paragraph;
    }

    /// <summary>
    /// A list item as an indented paragraph carrying its own marker, rather than
    /// a FlowDocument List.
    ///
    /// The marker is the reason. A List draws from ListMarkerStyle, which has no
    /// way to render the marker the parser already worked out, and no way at all
    /// to render a task box. Written into the text it survives selection and copy
    /// as what the reader sees.
    /// </summary>
    private static IEnumerable<Block> Item(MdListItem item, int depth)
    {
        var indent = Gutter * (depth + 1);
        var paragraph = new Paragraph { Margin = new Thickness(indent, 0, 0, 3), TextIndent = -Gutter };

        var marker = new Run(Marker(item))
        {
            Foreground = item.Done is null
                ? Tokens.Get<Brush>("BrushTextTertiary")
                : Tokens.Get<Brush>(item.Done is true ? "BrushSuccessDeep" : "BrushBorderStrong"),
        };

        paragraph.Inlines.Add(marker);

        foreach (var inline in item.Inlines)
        {
            paragraph.Inlines.Add(MarkdownInlines.Build(inline));
        }

        yield return paragraph;

        foreach (var child in item.Children)
        {
            foreach (var rendered in Render(child, depth + 1))
            {
                yield return rendered;
            }
        }
    }

    private static string Marker(MdListItem item) => item.Done switch
    {
        true => "[x]  ",
        false => "[ ]  ",
        null => item.Marker + "  ",
    };

    private const double Gutter = 18;

    /// <summary>
    /// Square rather than rounded: a FlowDocument Paragraph has a border and no
    /// corner radius. The trade is deliberate, because the alternative shape that
    /// keeps the radius is a BlockUIContainer, whose contents cannot be selected,
    /// and code is the text a reader most wants to copy.
    /// </summary>
    private static Paragraph Code(MdCodeBlock code)
    {
        var paragraph = new Paragraph(new Run(code.Text))
        {
            FontFamily = Tokens.Get<FontFamily>("FontFamilyMono"),
            FontSize = Tokens.Number("TextMonoPathSize"),
            Background = Tokens.Get<Brush>("BrushSurfaceSunken"),
            BorderBrush = Tokens.Get<Brush>("BrushBorderSoft"),
            BorderThickness = Tokens.Get<Thickness>("BorderThickness"),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 2, 0, 12),
        };

        return paragraph;
    }

    private static Table Grid(MdTable source)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 12) };
        var columns = source.Rows.Count == 0 ? 0 : source.Rows.Max(r => r.Count);

        for (var i = 0; i < columns; i++)
        {
            table.Columns.Add(new TableColumn());
        }

        var group = new TableRowGroup();
        table.RowGroups.Add(group);

        var border = Tokens.Get<Brush>("BrushBorderSoft");

        for (var r = 0; r < source.Rows.Count; r++)
        {
            var row = new TableRow();
            var header = source.HasHeader && r == 0;

            foreach (var value in source.Rows[r])
            {
                var cell = new TableCell(new Paragraph(new Run(value)) { Margin = new Thickness(0) })
                {
                    Padding = new Thickness(10, 6, 10, 6),
                    BorderBrush = border,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                };

                if (header)
                {
                    cell.FontWeight = Tokens.Get<FontWeight>("TextHeadingWeight");
                }

                row.Cells.Add(cell);
            }

            group.Rows.Add(row);
        }

        return table;
    }

    private static Section Quote(MdQuote quote, int depth)
    {
        var section = new Section
        {
            BorderBrush = Tokens.Get<Brush>("BrushBorder"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 10),
        };

        foreach (var block in quote.Blocks)
        {
            foreach (var rendered in Render(block, depth))
            {
                section.Blocks.Add(rendered);
            }
        }

        return section;
    }

    private static BlockUIContainer Rule() =>
        new(new Rectangle
        {
            Height = Tokens.Number("BorderWidth"),
            Fill = Tokens.Get<Brush>("BrushBorderSoft"),
        })
        {
            Margin = new Thickness(0, 10, 0, 14),
        };
}
