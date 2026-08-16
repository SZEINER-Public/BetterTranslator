using System.Globalization;
using BetterTranslator.Core.Services;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// One formatter is behind every timestamp on screen, so its boundaries are
/// worth pinning: seconds, minutes, hours, and the day boundary past which the
/// relative part is dropped entirely.
/// </summary>
public sealed class RelativeTimeTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly DateTimeOffset Now = new(2026, 7, 28, 20, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, "just now")]
    [InlineData(1, "just now")]
    [InlineData(59, "just now")]
    public void SecondsReadAsTheMoment(int seconds, string expected) =>
        RelativeTime.Relative(Now.AddSeconds(-seconds), Now).Should().Be(expected);

    [Theory]
    [InlineData(60, "1 min ago")]
    [InlineData(150, "2 min ago")]
    [InlineData(2520, "42 min ago")]
    [InlineData(3599, "59 min ago")]
    public void MinutesCountDownToTheMinute(int seconds, string expected) =>
        RelativeTime.Relative(Now.AddSeconds(-seconds), Now).Should().Be(expected);

    [Theory]
    [InlineData(3600, "1 hour ago")]
    [InlineData(7200, "2 hours ago")]
    [InlineData(82800, "23 hours ago")]
    public void HoursAreSingularAtOne(int seconds, string expected) =>
        RelativeTime.Relative(Now.AddSeconds(-seconds), Now).Should().Be(expected);

    [Theory]
    [InlineData(86400)]
    [InlineData(172800)]
    [InlineData(2592000)]
    public void PastADayTheRelativePartIsDropped(int seconds) =>
        RelativeTime.Relative(Now.AddSeconds(-seconds), Now).Should().BeNull();

    [Fact]
    public void AMomentInTheFutureReadsAsThePresentRatherThanANegativeAge() =>
        RelativeTime.Relative(Now.AddMinutes(5), Now).Should().Be("just now");

    [Fact]
    public void RowJoinsTheRelativeAndAbsolutePartsWithASpacedHyphen()
    {
        var stamp = RelativeTime.Row(Now.AddMinutes(-42), Now, Invariant);

        stamp.Should().Be("42 min ago - Jul 28, 19:48");
        stamp.Should().NotContain("–").And.NotContain("—").And.NotContain("·",
            "constraint 6 allows ASCII punctuation only");
    }

    [Fact]
    public void RowCarriesTheDateAloneOnceItIsOlderThanADay() =>
        RelativeTime.Row(Now.AddDays(-3), Now, Invariant).Should().Be("Jul 25, 20:30");

    [Fact]
    public void HeaderSpellsTheMonthOut() =>
        RelativeTime.Header(Now.AddMinutes(-42), Now, Invariant).Should().Be("42 min ago - 28 July 2026, 19:48");

    [Fact]
    public void TooltipIsAlwaysTheFullAbsoluteMoment() =>
        RelativeTime.Tooltip(new DateTimeOffset(2026, 7, 28, 20, 29, 0, TimeSpan.Zero), Invariant)
            .Should().Be("Tuesday, 28 July 2026 at 20:29");
}
