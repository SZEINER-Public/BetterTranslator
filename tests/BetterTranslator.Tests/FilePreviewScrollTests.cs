using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// A file preview capped to a peek can be read to its end.
///
/// It could not. The style clipped the document at the cap with the bar
/// switched off, so opening a preview produced a page cut mid sentence with
/// nothing to take hold of: no bar, no wheel, and the only way on was Show more
/// at the top of the entry, nowhere near the cut. The two states are asserted
/// here because they are opposites and only one of them can be right at a time.
///
/// Everything is read inside the STA thread that owns the dictionary and comes
/// back as plain values: a Style belongs to the thread that built it.
/// </summary>
public sealed class FilePreviewScrollTests
{
    private sealed record Preview(
        ScrollBarVisibility Capped,
        bool HasCap,
        bool Chains,
        bool UncapsWhenExpanded,
        ScrollBarVisibility Expanded,
        string? BarBackground);

    [Fact]
    public void A_capped_preview_scrolls_inside_itself()
    {
        var preview = Read();

        preview.Capped.Should().Be(
            ScrollBarVisibility.Auto,
            "a peek that clips has to offer a way through what it clipped");

        preview.HasCap.Should().BeTrue("the peek is the height the bar scrolls within");
    }

    [Fact]
    public void An_expanded_preview_gives_the_wheel_back_to_the_history()
    {
        var preview = Read();

        preview.UncapsWhenExpanded.Should().BeTrue();
        preview.Expanded.Should().Be(
            ScrollBarVisibility.Disabled,
            "with the cap off there is nothing left to scroll, so a bar would be a control over nothing");
    }

    [Fact]
    public void The_wheel_is_handed_outward_at_the_ends()
    {
        // Without this a bar inside the history is a trap: WPF marks the wheel
        // handled whether or not the inner scroller could use it.
        Read().Chains.Should().BeTrue();
    }

    [Fact]
    public void The_bar_runs_in_a_track_of_its_own()
    {
        // The window's bar is a bare thumb. Beside a paragraph, with no panel
        // edge to sit against, it reads as something left behind rather than as
        // a control, which is one of the reasons it was taken away.
        var background = Read().BarBackground;

        background.Should().NotBeNull();
        background.Should().NotBe(Brushes.Transparent.ToString());
    }

    private static Preview Read() => StaRunner.Run(() =>
    {
        var dictionary = new ResourceDictionary
        {
            Source = new Uri(
                "pack://application:,,,/BetterTranslator;component/Themes/Controls.xaml",
                UriKind.Absolute),
        };

        var style = (Style)dictionary["FilePreviewScroll"];

        var capped = style.Setters.OfType<Setter>()
            .Single(setter => setter.Property == ScrollViewer.VerticalScrollBarVisibilityProperty)
            .Value;

        var chains = style.Setters.OfType<Setter>()
            .Single(setter => setter.Property == App.Controls.WheelChaining.ChainsProperty)
            .Value;

        var cap = style.Triggers.OfType<DataTrigger>()
            .SelectMany(trigger => trigger.Setters.OfType<Setter>())
            .Any(setter => setter.Property == FrameworkElement.MaxHeightProperty);

        var expanded = style.Triggers.OfType<MultiDataTrigger>()
            .Single(trigger => trigger.Conditions
                .Any(condition => condition.Binding is System.Windows.Data.Binding { Path.Path: "IsExpanded" }))
            .Setters.OfType<Setter>()
            .ToList();

        var uncaps = expanded.Any(setter =>
            setter.Property == FrameworkElement.MaxHeightProperty
            && setter.Value is double.PositiveInfinity);

        var bar = style.Resources.Values.OfType<Style>()
            .SingleOrDefault(inner => inner.TargetType == typeof(ScrollBar))
            ?.Setters.OfType<Setter>()
            .SingleOrDefault(setter => setter.Property == Control.BackgroundProperty)
            ?.Value;

        return new Preview(
            (ScrollBarVisibility)capped,
            cap,
            (bool)chains,
            uncaps,
            (ScrollBarVisibility)expanded
                .Single(setter => setter.Property == ScrollViewer.VerticalScrollBarVisibilityProperty)
                .Value,
            bar?.ToString());
    });
}
