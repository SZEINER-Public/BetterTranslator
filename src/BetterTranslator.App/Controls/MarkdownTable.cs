using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterTranslator.App.Services;
using BetterTranslator.Indexing.Markdown;

namespace BetterTranslator.App.Controls;

public static class MarkdownTable
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source",
        typeof(MdTable),
        typeof(MarkdownTable),
        new PropertyMetadata(null, OnSourceChanged));

    public static void SetSource(DependencyObject element, MdTable? value) =>
        element.SetValue(SourceProperty, value);

    public static MdTable? GetSource(DependencyObject element) =>
        (MdTable?)element.GetValue(SourceProperty);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Grid grid)
        {
            return;
        }

        grid.Children.Clear();
        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();

        if (e.NewValue is not MdTable table || table.Rows.Count == 0)
        {
            return;
        }

        Build(grid, table);
    }

    private static void Build(Grid grid, MdTable table)
    {
        var columns = table.Rows.Max(r => r.Count);
        var cellPadding = Tokens.Get<Thickness>("Space8Thickness");
        var minWidth = Tokens.Number("TableCellMinWidth");
        var maxWidth = Tokens.Number("TableCellMaxWidth");
        var hairline = Tokens.Get<Brush>("BrushBorderHairline");
        var headerFill = Tokens.Get<Brush>("BrushSurfaceMuted");

        for (var column = 0; column < columns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var row = 0; row < table.Rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var isHeader = table.HasHeader && row == 0;

            if (isHeader || row < table.Rows.Count - 1)
            {
                var rule = new Border
                {
                    Background = isHeader ? headerFill : Brushes.Transparent,
                    BorderBrush = hairline,
                    BorderThickness = new Thickness(0, 0, 0, Tokens.Number("BorderWidth")),
                };

                Grid.SetRow(rule, row);
                Grid.SetColumn(rule, 0);
                Grid.SetColumnSpan(rule, columns);
                grid.Children.Add(rule);
            }

            for (var column = 0; column < table.Rows[row].Count; column++)
            {
                var cell = new TextBlock
                {
                    Text = table.Rows[row][column],
                    TextWrapping = TextWrapping.Wrap,
                    Margin = cellPadding,
                    MinWidth = minWidth,
                    MaxWidth = maxWidth,
                    Style = Tokens.Get<Style>(isHeader ? "TextTableHeader" : "TextSecondary"),
                };

                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }
    }
}
