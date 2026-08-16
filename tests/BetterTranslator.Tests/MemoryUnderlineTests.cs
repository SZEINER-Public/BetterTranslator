using BetterTranslator.Indexing.Retrieval;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Which words render dotted and which solid. The threshold is a user setting,
/// so moving it has to change the marking without anything being re-translated.
/// </summary>
public sealed class MemoryUnderlineTests
{
    private static MemoryMatch Match(int confidence) => new()
    {
        Term = "nastaveni",
        Start = 0,
        Confidence = confidence,
        Origin = "From term pairs",
    };

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(79)]
    public void BelowTheThresholdIsDotted(int confidence) =>
        Match(confidence).UnderlineFor(80).Should().Be(MemoryUnderline.Dotted);

    [Theory]
    [InlineData(80)]
    [InlineData(88)]
    [InlineData(100)]
    public void AtOrAboveTheThresholdIsSolid(int confidence) =>
        Match(confidence).UnderlineFor(80).Should().Be(MemoryUnderline.Solid);

    [Fact]
    public void TheBoundaryItselfCountsAsSure()
    {
        // Exactly at the threshold is not unsure: the setting reads "mark a
        // word unsure below" this figure.
        Match(80).UnderlineFor(80).Should().Be(MemoryUnderline.Solid);
        Match(80).IsUnsure(80).Should().BeFalse();
        Match(79).IsUnsure(80).Should().BeTrue();
    }

    [Fact]
    public void MovingTheThresholdChangesWhichWordsAreDotted()
    {
        var match = Match(85);

        match.UnderlineFor(80).Should().Be(MemoryUnderline.Solid);
        match.UnderlineFor(90).Should().Be(MemoryUnderline.Dotted, "raising the bar makes more words unsure");
    }

    [Fact]
    public void TheConfidenceFigureIsPlainText() =>
        Match(88).ConfidenceLabel.Should().Be("88% confident");

    [Fact]
    public void TheSummaryCountsTermsAndUnsureOnes()
    {
        var result = new MemoryAnnotatedResult("a b c d", [
            Match(96) with { Start = 0 },
            Match(58) with { Start = 2 },
            Match(99) with { Start = 4 },
            Match(88) with { Start = 6 },
        ]);

        result.Summary(80).Should().Be("4 terms came from memory, 1 is unsure");
        result.UnsureCount(80).Should().Be(1);
    }

    [Fact]
    public void TheSummaryDropsTheUnsureClauseWhenThereAreNone()
    {
        var result = new MemoryAnnotatedResult("a b", [Match(96) with { Start = 0 }]);

        result.Summary(80).Should().Be("1 term came from memory");
    }

    [Fact]
    public void NothingFromMemorySaysSo() =>
        MemoryAnnotatedResult.None("pracovni prostor").Summary(80).Should().Be("Nothing came from memory");

    [Fact]
    public void SegmentsSplitTheTextAroundEachMatch()
    {
        var text = "Otevrete nastaveni a pripnete panel.";

        var result = new MemoryAnnotatedResult(text, [
            new MemoryMatch { Term = "nastaveni", Start = 9, Confidence = 96, Origin = "From term pairs" },
            new MemoryMatch { Term = "pripnete", Start = 21, Confidence = 58, Origin = "From my corrections" },
        ]);

        var segments = result.Segments();

        // Reassembling the segments must give back exactly the original text.
        string.Concat(segments.Select(s => s.Text)).Should().Be(text);

        segments.Where(s => s.Match is not null).Select(s => s.Text)
            .Should().Equal("nastaveni", "pripnete");
    }

    [Fact]
    public void TextWithNoMatchesIsOneUnmarkedSegment()
    {
        var segments = MemoryAnnotatedResult.None("pracovni prostor").Segments();

        segments.Should().ContainSingle();
        segments[0].Match.Should().BeNull();
    }

    [Fact]
    public void AnOutOfRangeMatchIsSkippedRatherThanCorruptingTheText()
    {
        var text = "short";

        var result = new MemoryAnnotatedResult(text, [
            new MemoryMatch { Term = "way past the end", Start = 3, Confidence = 90, Origin = "From term pairs" },
        ]);

        string.Concat(result.Segments().Select(s => s.Text)).Should().Be(text);
    }

    [Fact]
    public void OverlappingMatchesDoNotDuplicateText()
    {
        var text = "nastaveni panel";

        var result = new MemoryAnnotatedResult(text, [
            new MemoryMatch { Term = "nastaveni", Start = 0, Confidence = 90, Origin = "a" },
            new MemoryMatch { Term = "aveni", Start = 4, Confidence = 70, Origin = "b" },
        ]);

        string.Concat(result.Segments().Select(s => s.Text)).Should().Be(text);
    }
}
