using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using BetterTranslator.App.Controls;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

public sealed class SeparatorHitRegionTests(ITestOutputHelper output)
{
    private static readonly double[] Scales = [1.0, 1.5, 2.0];

    private const double Width = 600;
    private const double Height = 400;

    private sealed record Bands(Rect Rail, Rect Band, double RailTrackHeight, double ContentWidth);

    private static double Overlap(Rect rail, Rect band) =>
        Math.Max(0, Math.Min(rail.Right, band.Right) - Math.Max(rail.Left, band.Left));

    private static double DeviceOverlap(Rect rail, Rect band, double scale)
    {
        var railRight = Math.Round(rail.Right * scale, MidpointRounding.AwayFromZero);
        var bandLeft = Math.Round(band.Left * scale, MidpointRounding.AwayFromZero);
        var bandRight = Math.Round(band.Right * scale, MidpointRounding.AwayFromZero);
        var railLeft = Math.Round(rail.Left * scale, MidpointRounding.AwayFromZero);

        return Math.Max(0, Math.Min(railRight, bandRight) - Math.Max(railLeft, bandLeft));
    }

    [Fact]
    public void TheSplitterBandAndTheScrollRailNeverOverlap()
    {
        foreach (var open in new[] { true, false })
        {
            var bands = Measure(open);

            Report(open, bands);

            Overlap(bands.Rail, bands.Band).Should().Be(
                0,
                "the splitter band and the scroll rail may touch and must never share a pixel");

            bands.Band.Left.Should().BeGreaterThanOrEqualTo(
                bands.Rail.Right,
                "the rail lives entirely inside the content column");

            foreach (var scale in Scales)
            {
                DeviceOverlap(bands.Rail, bands.Band, scale).Should().Be(
                    0,
                    "rounding to device pixels at " + scale.ToString("0.0", CultureInfo.InvariantCulture) + " must not reintroduce the overlap");
            }
        }
    }

    [Fact]
    public void TheSplitterBandReachesThePointerTarget()
    {
        var bands = Measure(panelOpen: true);

        bands.Band.Width.Should().BeGreaterThanOrEqualTo(
            8,
            "a divider thinner than this is a target nobody can hit on the first try");
    }

    [Fact]
    public void TheScrollRailKeepsItsWidthAndItsWholeTrack()
    {
        var bands = Measure(panelOpen: true);

        bands.Rail.Width.Should().Be(8, "the fix must not take pixels from the rail");
        bands.RailTrackHeight.Should().Be(Height, "the track runs the whole height of the content");
    }

    [Fact]
    public void APressInsideTheRailReachesTheScrollbarAndAPressInsideTheBandReachesTheSplitter()
    {
        StaRunner.Run(() =>
        {
            var surface = Build(panelOpen: true);

            var rail = Rectangle(surface.Root, surface.Bar);
            var band = HitBand(surface.Root, surface.Separator);

            var inRail = HitAt(surface.Root, rail.Left + (rail.Width / 2), Height / 2);
            var inBand = HitAt(surface.Root, band.Left + (band.Width / 2), Height / 2);

            Owner(inRail).Should().Be(nameof(ScrollBar), "a press on the rail scrolls and never resizes");
            Owner(inBand).Should().Be(nameof(ResizeSeparator), "a press on the band resizes and never grabs the rail");

            for (var x = rail.Left; x < rail.Right; x += 0.5)
            {
                Owner(HitAt(surface.Root, x, Height / 2))
                    .Should().NotBe(nameof(ResizeSeparator), "no point of the rail belongs to the splitter");
            }

            for (var x = band.Left; x < band.Right; x += 0.5)
            {
                Owner(HitAt(surface.Root, x, Height / 2))
                    .Should().NotBe(nameof(ScrollBar), "no point of the band belongs to the rail");
            }

            return 0;
        });
    }

    [Fact]
    public void TheResizeCursorAndTheTooltipStopAtTheEdgeOfTheBand()
    {
        StaRunner.Run(() =>
        {
            var surface = Build(panelOpen: true);

            var rail = Rectangle(surface.Root, surface.Bar);

            surface.Separator.Cursor.Should().Be(Cursors.SizeWE);
            surface.Separator.ToolTip.Should().NotBeNull();

            for (var x = rail.Left; x < rail.Right; x += 0.5)
            {
                var hit = HitAt(surface.Root, x, Height / 2);

                Ancestors(hit).Should().NotContain(
                    element => ReferenceEquals(element, surface.Separator),
                    "the resize cursor and the tooltip belong to the separator, so neither may reach the rail");
            }

            return 0;
        });
    }

    [Fact]
    public void ADragThatCrossesTheBoundaryStaysWithTheControlThatStartedIt()
    {
        StaRunner.Run(() =>
        {
            var surface = Build(panelOpen: true);

            var separator = surface.Separator;
            var started = 0;
            var completed = 0;
            var crossed = 0d;

            separator.DragStarted += (_, _) => started++;
            separator.DragCompleted += (_, _) => completed++;
            separator.DragDelta += (_, e) => crossed += e.HorizontalChange;

            separator.RaiseEvent(new DragStartedEventArgs(0, 0) { RoutedEvent = Thumb.DragStartedEvent });

            separator.IsDragging.Should().BeFalse("IsDragging is set by the real press, not by the replayed event");

            separator.RaiseEvent(new DragDeltaEventArgs(-40, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            separator.RaiseEvent(new DragCompletedEventArgs(-40, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });

            started.Should().Be(1);
            completed.Should().Be(1);
            crossed.Should().Be(-40, "every delta of the drag went to the control the press started on");

            var rail = Rectangle(surface.Root, surface.Bar);
            var owner = HitAt(surface.Root, rail.Left + (rail.Width / 2), Height / 2);

            Owner(owner).Should().Be(
                nameof(ScrollBar),
                "the neighbour is reachable again once the drag is over, and never during it");

            return 0;
        });
    }

    private void Report(bool open, Bands bands)
    {
        output.WriteLine("panel " + (open ? "open" : "closed"));
        output.WriteLine("  scroll rail   : " + Format(bands.Rail));
        output.WriteLine("  splitter band : " + Format(bands.Band));
        output.WriteLine("  overlap       : " + Overlap(bands.Rail, bands.Band).ToString("0.###", CultureInfo.InvariantCulture) + " dip");
        output.WriteLine("  content width : " + bands.ContentWidth.ToString("0.###", CultureInfo.InvariantCulture) + " dip");

        foreach (var scale in Scales)
        {
            output.WriteLine(
                "  at " + scale.ToString("0.0", CultureInfo.InvariantCulture) + "x device pixels: rail right "
                + Math.Round(bands.Rail.Right * scale, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
                + ", band left "
                + Math.Round(bands.Band.Left * scale, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)
                + ", overlap "
                + DeviceOverlap(bands.Rail, bands.Band, scale).ToString("0.###", CultureInfo.InvariantCulture));
        }
    }

    private static string Format(Rect rect) =>
        "x " + rect.Left.ToString("0.###", CultureInfo.InvariantCulture)
        + " to " + rect.Right.ToString("0.###", CultureInfo.InvariantCulture)
        + ", width " + rect.Width.ToString("0.###", CultureInfo.InvariantCulture);

    private static DependencyObject? HitAt(Visual root, double x, double y) =>
        VisualTreeHelper.HitTest(root, new Point(x, y))?.VisualHit;

    private static string Owner(DependencyObject? hit)
    {
        foreach (var element in Ancestors(hit))
        {
            switch (element)
            {
                case ResizeSeparator:
                    return nameof(ResizeSeparator);
                case ScrollBar:
                    return nameof(ScrollBar);
            }
        }

        return hit is null ? "nothing" : "content:" + hit.GetType().Name;
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? from)
    {
        while (from is not null)
        {
            yield return from;
            from = VisualTreeHelper.GetParent(from);
        }
    }

    private static Rect Rectangle(Visual root, FrameworkElement element)
    {
        var origin = element.TransformToAncestor(root).Transform(new Point(0, 0));

        return new Rect(origin, new Size(element.ActualWidth, element.ActualHeight));
    }

    private static Rect HitBand(Visual root, ResizeSeparator separator)
    {
        var band = Rectangle(root, separator);

        foreach (var child in Descendants(separator).OfType<FrameworkElement>())
        {
            if (child.ActualWidth <= 0 || !ReferenceEquals(child.GetValue(Panel.BackgroundProperty), Brushes.Transparent))
            {
                continue;
            }

            band.Union(Rectangle(root, child));
        }

        return band;
    }

    private sealed record Surface(FrameworkElement Root, ScrollBar Bar, ResizeSeparator Separator, Track Track, ScrollViewer Scroll);

    private static Bands Measure(bool panelOpen) => StaRunner.Run(() =>
    {
        var surface = Build(panelOpen);

        return new Bands(
            Rectangle(surface.Root, surface.Bar),
            HitBand(surface.Root, surface.Separator),
            surface.Track.ActualHeight,
            surface.Scroll.ActualWidth);
    });

    private static Surface Build(bool panelOpen)
    {
        var themes = new ResourceDictionary();

        foreach (var name in new[] { "Colors", "Typography", "Metrics", "Icons", "Controls" })
        {
            themes.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/BetterTranslator;component/Themes/{name}.xaml"),
            });
        }

        var scroll = new ScrollViewer
        {
            Content = new Border { Height = 4000, Background = Brushes.White },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var separator = new ResizeSeparator
        {
            Value = 200,
            Minimum = 120,
            Maximum = 400,
            Inverted = true,
            ToolTip = "Resize advanced panel",
        };
        var panel = new Border { Background = Brushes.White };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(panelOpen ? 200 : 0) });

        Grid.SetColumn(scroll, 0);
        Grid.SetColumn(separator, 1);
        Grid.SetColumn(panel, 2);

        grid.Children.Add(scroll);
        grid.Children.Add(separator);
        grid.Children.Add(panel);

        var host = new Border { Resources = themes, Width = Width, Height = Height, Child = grid };

        host.Measure(new Size(Width, Height));
        host.Arrange(new Rect(0, 0, Width, Height));
        host.UpdateLayout();

        var bar = Descendants(scroll)
            .OfType<ScrollBar>()
            .Single(b => b.Orientation == Orientation.Vertical && b.Visibility == Visibility.Visible);

        var track = Descendants(bar).OfType<Track>().Single();

        return new Surface(host, bar, separator, track, scroll);
    }

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
