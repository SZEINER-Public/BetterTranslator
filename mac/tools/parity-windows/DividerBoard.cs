using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterTranslator.App.Controls;

namespace BetterTranslator.Mac.Parity.Windows;

public static class DividerBoard
{
    public static FrameworkElement Build()
    {
        var grid = new Grid { Background = Brush("BrushWindow") };

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(246) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300) });

        Add(grid, new Border { Background = Brush("BrushSurfaceMuted") }, 0);
        Add(grid, new ResizeSeparator { Value = 246, Minimum = 200, Maximum = 420 }, 1);
        Add(grid, new Border { Background = Brush("BrushSurface") }, 2);
        Add(grid, new ResizeSeparator { Value = 300, Minimum = 240, Maximum = 460, Inverted = true }, 3);
        Add(grid, new Border { Background = Brush("BrushSurfaceMuted") }, 4);

        return grid;
    }

    private static void Add(Grid grid, UIElement child, int column)
    {
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }

    private static Brush? Brush(string key) => Application.Current.TryFindResource(key) as Brush;
}
