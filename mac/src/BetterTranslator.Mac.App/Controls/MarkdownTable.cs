using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using BetterTranslator.Indexing.Markdown;

namespace BetterTranslator.Mac.App.Controls;

public static class MarkdownTable
{
    public static readonly AttachedProperty<MdTable?> SourceProperty =
        AvaloniaProperty.RegisterAttached<Grid, MdTable?>("Source", typeof(MarkdownTable));

    static MarkdownTable()
    {
        SourceProperty.Changed.AddClassHandler<Grid>(OnSourceChanged);
    }

    public static void SetSource(Grid element, MdTable? value) =>
        element.SetValue(SourceProperty, value);

    public static MdTable? GetSource(Grid element) =>
        element.GetValue(SourceProperty);

    private static void OnSourceChanged(Grid grid, AvaloniaPropertyChangedEventArgs e)
    {
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
        var cellPadding = Get<Thickness>("Space8Thickness");
        var minWidth = Number("TableCellMinWidth");
        var maxWidth = Number("TableCellMaxWidth");
        var hairline = Get<IBrush>("BrushBorderHairline");
        var headerFill = Get<IBrush>("BrushSurfaceMuted");

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
                    BorderThickness = new Thickness(0, 0, 0, Number("BorderWidth")),
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
                    Theme = Get<ControlTheme>(isHeader ? "TextTableHeader" : "TextSecondary"),
                };

                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }
    }

    private static T Get<T>(string key) =>
        Application.Current is { } app && app.TryFindResource(key, out var value) && value is T typed
            ? typed
            : throw new KeyNotFoundException($"Resource '{key}' was not found.");

    private static double Number(string key) => Get<double>(key);
}
