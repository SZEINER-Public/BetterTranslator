using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// A scrollbar is the width the token says.
///
/// It was not, and the style could not show it. A control keeps its theme style
/// underneath whatever style is applied to it, and the theme's sets MinWidth to
/// the system scrollbar width. A minimum beats a width, so every bar in the
/// window rendered at seventeen while this style asked for eight, for as long as
/// the style existed. Read the rendered bar, never the setter: the setter was
/// right the whole time.
/// </summary>
public sealed class ScrollBarWidthTests
{
    [Fact]
    public void The_vertical_bar_renders_at_the_token_width()
    {
        var measured = Render(horizontal: false);

        measured.Token.Should().BeGreaterThan(0);
        measured.Vertical.Should().Be(
            measured.Token,
            "a bar wider than its token is the theme's minimum winning, which no setter in the style can show");
    }

    [Fact]
    public void The_horizontal_bar_renders_at_the_token_height()
    {
        var measured = Render(horizontal: true);

        measured.Horizontal.Should().Be(measured.Token);
    }

    private sealed record Measured(double Token, double Vertical, double Horizontal);

    // Every element is built inside the STA thread that will lay it out: a
    // WPF object cannot be constructed anywhere else.
    private static Measured Render(bool horizontal) => StaRunner.Run(() =>
    {
        var themes = new ResourceDictionary();

        foreach (var name in new[] { "Colors", "Typography", "Metrics", "Icons", "Controls" })
        {
            themes.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/BetterTranslator;component/Themes/{name}.xaml"),
            });
        }

        var content = horizontal
            ? new Border { Width = 4000, Height = 40, Background = Brushes.White }
            : new Border { Height = 4000, Background = Brushes.White };

        var scroll = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        };

        var host = new Border { Resources = themes, Width = 300, Height = 200, Child = scroll };

        host.Measure(new Size(300, 200));
        host.Arrange(new Rect(0, 0, 300, 200));
        host.UpdateLayout();

        var bars = Descendants(scroll).OfType<ScrollBar>().ToList();

        double Size(Orientation orientation) => bars
            .Where(bar => bar.Orientation == orientation && bar.Visibility == Visibility.Visible)
            .Select(bar => orientation == Orientation.Vertical ? bar.ActualWidth : bar.ActualHeight)
            .DefaultIfEmpty(0)
            .Single();

        return new Measured((double)themes["ScrollThumbWidth"], Size(Orientation.Vertical), Size(Orientation.Horizontal));
    });

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);

            yield return child;

            foreach (var nested in Descendants(child))
            {
                yield return nested;
            }
        }
    }
}
