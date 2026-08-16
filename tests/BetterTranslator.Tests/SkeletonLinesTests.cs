using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BetterTranslator.App.Controls;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The loading placeholder's geometry, and the template it is built into.
///
/// Both fail quietly. A bar count that drifts is a layout shift at the moment
/// the translation lands, which is exactly when nobody is watching the
/// placeholder; a template part that stops resolving leaves the region empty,
/// which is indistinguishable from the state the placeholder replaced.
/// </summary>
public sealed class SkeletonLinesTests
{
    // The values Metrics.xaml carries. Asserted there by DesignTokenTests; used
    // here so the arithmetic is exercised at the sizes it actually runs at.
    private const double CharactersPerLine = 68;
    private const int MaxLines = 8;
    private const double MinFraction = 0.35;
    private const double MaxFraction = 0.85;

    /// <summary>
    /// The bars stand on the prose line box, so a placeholder for a three-line
    /// send occupies three lines of the block the answer will.
    /// </summary>
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(68, 1)]
    [InlineData(69, 2)]
    [InlineData(200, 3)]
    // Capped: past a point the placeholder is describing a wall of text, and a
    // longer one says nothing a long one does not.
    [InlineData(4000, MaxLines)]
    public void The_bar_count_follows_the_source_length(int sourceLength, int expected) =>
        Shape(sourceLength).Lines.Should().Be(expected);

    /// <summary>
    /// The closing bar is short, because the closing line of a paragraph is.
    /// How short is what is left over after the full lines.
    /// </summary>
    [Fact]
    public void The_closing_bar_is_a_fraction_of_the_width_and_never_a_full_one()
    {
        Enumerable.Range(0, 500)
            .Select(length => Shape(length).LastFraction)
            .Should().OnlyContain(f => f >= MinFraction && f <= MaxFraction);

        // A send that fills its lines exactly still closes short rather than
        // squaring off into a block.
        Shape(136).LastFraction.Should().BeLessThan(1);
    }

    [Fact]
    public void A_send_of_one_word_is_one_short_bar()
    {
        var (lines, fraction) = Shape("Rebuild".Length);

        lines.Should().Be(1);
        fraction.Should().Be(MinFraction, "a word is shorter than the floor, so the floor is what it gets");
    }

    /// <summary>
    /// Loaded standalone rather than through Application.Current: the dictionary
    /// is shared process-wide and this suite runs its classes in parallel, so a
    /// test that merged into it would race whichever other test read it next.
    /// </summary>
    [Fact]
    public void The_template_carries_the_part_the_bars_are_built_into()
    {
        var problems = StaRunner.Run(() =>
        {
            var controls = new ResourceDictionary
            {
                Source = new Uri(
                    "pack://application:,,,/BetterTranslator;component/Themes/Controls.xaml",
                    UriKind.Absolute),
            };

            var found = new List<string>();

            if (controls[typeof(SkeletonLines)] is not Style style)
            {
                return ["no implicit style for SkeletonLines"];
            }

            if (style.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == Control.TemplateProperty)?.Value
                is not ControlTemplate template)
            {
                return ["the style sets no Template"];
            }

            if (template.LoadContent() is not Panel root)
            {
                found.Add("the template's root is not a Panel for the bars to stack in");
            }
            else if (root.Name != "PART_Lines")
            {
                found.Add($"the template's root is named {root.Name}, not PART_Lines");
            }

            // Nothing inside the placeholder may take a tab stop: it stands
            // where the translation will be, and focus must not land in it.
            foreach (var property in new[] { UIElement.FocusableProperty, Control.IsTabStopProperty })
            {
                if (style.Setters.OfType<Setter>().FirstOrDefault(s => s.Property == property)?.Value is not false)
                {
                    found.Add($"{property.Name} is not set false");
                }
            }

            return found;
        });

        problems.Should().BeEmpty();
    }

    private static (int Lines, double LastFraction) Shape(int sourceLength) =>
        SkeletonLines.Shape(sourceLength, CharactersPerLine, MaxLines, MinFraction, MaxFraction);
}
