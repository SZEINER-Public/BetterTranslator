using Avalonia.Controls;
using Avalonia.Media;
using BetterTranslator.Mac.App.Controls;

namespace BetterTranslator.Mac.Parity;

public static class DividerBoard
{
    public static Control Build()
    {
        var grid = new Grid
        {
            Background = Brush("BrushWindow"),
            ColumnDefinitions = new ColumnDefinitions("246,Auto,*,Auto,300"),
        };

        Add(grid, new Border { Background = Brush("BrushSurfaceMuted") }, 0);
        Add(grid, new ResizeSeparator { Value = 246, Minimum = 200, Maximum = 420 }, 1);
        Add(grid, new Border { Background = Brush("BrushSurface") }, 2);
        Add(grid, new ResizeSeparator { Value = 300, Minimum = 240, Maximum = 460, Inverted = true }, 3);
        Add(grid, new Border { Background = Brush("BrushSurfaceMuted") }, 4);

        return grid;
    }

    private static void Add(Grid grid, Control child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static IBrush? Brush(string key) =>
        Avalonia.Application.Current is { } app && app.TryFindResource(key, out var value)
            ? value as IBrush
            : null;
}
