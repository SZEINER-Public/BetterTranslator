using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using BetterTranslator.Indexing.Markdown;

namespace BetterTranslator.Mac.App.Controls;

public static class MarkdownFlow
{
    public static readonly AttachedProperty<IEnumerable<MdBlock>?> BlocksProperty =
        AvaloniaProperty.RegisterAttached<Control, IEnumerable<MdBlock>?>("Blocks", typeof(MarkdownFlow));

    static MarkdownFlow()
    {
        BlocksProperty.Changed.AddClassHandler<Control>(OnBlocksChanged);
    }

    public static void SetBlocks(Control target, IEnumerable<MdBlock>? value) =>
        target.SetValue(BlocksProperty, value);

    public static IEnumerable<MdBlock>? GetBlocks(Control target) =>
        target.GetValue(BlocksProperty);

    private static void OnBlocksChanged(Control target, AvaloniaPropertyChangedEventArgs e)
    {
        var blocks = e.NewValue as IEnumerable<MdBlock>;

        switch (target)
        {
            case Panel panel:
                Fill(panel, blocks);
                break;

            case ContentControl host:
                host.Content = Build(blocks);
                break;

            case Decorator decorator:
                decorator.Child = Build(blocks);
                break;
        }
    }

    public static Panel Build(IEnumerable<MdBlock>? blocks)
    {
        var panel = new StackPanel();

        Fill(panel, blocks);

        return panel;
    }

    private static void Fill(Panel panel, IEnumerable<MdBlock>? blocks)
    {
        panel.Children.Clear();

        TextElement.SetFontFamily(panel, Get<FontFamily>("FontFamilyUi"));
        TextElement.SetFontSize(panel, Number("TextBodySize"));
        TextElement.SetForeground(panel, Get<IBrush>("BrushText"));
        TextBlock.SetLineHeight(panel, Number("ProseLineHeight"));

        foreach (var block in blocks ?? [])
        {
            foreach (var rendered in Render(block, depth: 0))
            {
                panel.Children.Add(rendered);
            }
        }
    }

    private static IEnumerable<Control> Render(MdBlock block, int depth)
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

    private static SelectableTextBlock Heading(MdHeading heading)
    {
        var paragraph = new SelectableTextBlock
        {
            FontWeight = Get<FontWeight>("TextHeadingWeight"),
            FontSize = heading.Level switch
            {
                1 => Number("TextTitleSize"),
                3 => Number("TextSubheadingSize"),
                _ => Number("TextHeadingSize"),
            },
            Margin = new Thickness(0, 14, 0, 6),
            TextWrapping = TextWrapping.Wrap,
        };

        MarkdownInlines.Fill(paragraph.Inlines, heading.Inlines);

        return paragraph;
    }

    private static SelectableTextBlock Prose(IEnumerable<MdInline> inlines, Thickness margin)
    {
        var paragraph = new SelectableTextBlock { Margin = margin, TextWrapping = TextWrapping.Wrap };

        MarkdownInlines.Fill(paragraph.Inlines, inlines);

        return paragraph;
    }

    private static IEnumerable<Control> Item(MdListItem item, int depth)
    {
        var indent = Gutter * depth;
        var paragraph = new SelectableTextBlock
        {
            Margin = new Thickness(indent, 0, 0, 3),
            TextWrapping = TextWrapping.Wrap,
        };

        var marker = new Run(Marker(item))
        {
            Foreground = item.Done is null
                ? Get<IBrush>("BrushTextTertiary")
                : Get<IBrush>(item.Done is true ? "BrushSuccessDeep" : "BrushBorderStrong"),
        };

        paragraph.Inlines?.Add(marker);

        foreach (var inline in item.Inlines)
        {
            paragraph.Inlines?.Add(MarkdownInlines.Build(inline));
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

    private static Border Code(MdCodeBlock code)
    {
        var text = new SelectableTextBlock
        {
            Text = code.Text,
            FontFamily = Get<FontFamily>("FontFamilyMono"),
            FontSize = Number("TextMonoPathSize"),
            TextWrapping = TextWrapping.Wrap,
        };

        return new Border
        {
            Background = Get<IBrush>("BrushSurfaceSunken"),
            BorderBrush = Get<IBrush>("BrushBorderSoft"),
            BorderThickness = Get<Thickness>("BorderThickness"),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 2, 0, 12),
            Child = text,
        };
    }

    private static Control Grid(MdTable source)
    {
        var table = new Avalonia.Controls.Grid { Margin = new Thickness(0, 2, 0, 12) };
        var columns = source.Rows.Count == 0 ? 0 : source.Rows.Max(r => r.Count);

        for (var i = 0; i < columns; i++)
        {
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var border = Get<IBrush>("BrushBorderSoft");

        for (var r = 0; r < source.Rows.Count; r++)
        {
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = source.HasHeader && r == 0;

            for (var c = 0; c < source.Rows[r].Count; c++)
            {
                var text = new SelectableTextBlock
                {
                    Text = source.Rows[r][c],
                    TextWrapping = TextWrapping.Wrap,
                };

                if (header)
                {
                    text.FontWeight = Get<FontWeight>("TextHeadingWeight");
                }

                var cell = new Border
                {
                    Padding = new Thickness(10, 6, 10, 6),
                    BorderBrush = border,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = text,
                };

                Avalonia.Controls.Grid.SetRow(cell, r);
                Avalonia.Controls.Grid.SetColumn(cell, c);
                table.Children.Add(cell);
            }
        }

        return table;
    }

    private static Border Quote(MdQuote quote, int depth)
    {
        var stack = new StackPanel();

        foreach (var block in quote.Blocks)
        {
            foreach (var rendered in Render(block, depth))
            {
                stack.Children.Add(rendered);
            }
        }

        return new Border
        {
            BorderBrush = Get<IBrush>("BrushBorder"),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(12, 0, 0, 0),
            Margin = new Thickness(0, 0, 0, 10),
            Child = stack,
        };
    }

    private static Rectangle Rule() =>
        new()
        {
            Height = Number("BorderWidth"),
            Fill = Get<IBrush>("BrushBorderSoft"),
            Margin = new Thickness(0, 10, 0, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

    private static T Get<T>(string key) =>
        Application.Current is { } app && app.TryFindResource(key, out var value) && value is T typed
            ? typed
            : throw new KeyNotFoundException($"Resource '{key}' was not found.");

    private static double Number(string key) => Get<double>(key);
}
