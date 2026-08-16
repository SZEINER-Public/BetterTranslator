using System.Globalization;
using BetterTranslator.Runtime.Downloads;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The footer states a remaining time computed from what is actually left, not
/// from an average over the whole run.
/// </summary>
public sealed class TransferEstimateTests
{
    [Fact]
    public void RemainingTimeComesFromWhatIsLeftOverTheRecentRate()
    {
        var estimate = new TransferEstimate();
        estimate.Reset(totalBytes: 1000);

        // 100 bytes a second, 200 done, so 800 left is 8 seconds.
        estimate.Report(TimeSpan.FromSeconds(0), 0);
        estimate.Report(TimeSpan.FromSeconds(2), 200);

        estimate.BytesPerSecond.Should().BeApproximately(100, 0.01);
        estimate.BytesRemaining.Should().Be(800);
        estimate.Remaining!.Value.TotalSeconds.Should().BeApproximately(8, 0.01);
    }

    [Fact]
    public void ASlowStartDoesNotHoldTheEstimateDownForever()
    {
        var estimate = new TransferEstimate();
        estimate.Reset(totalBytes: 10_000);

        // Crawls for the first second, then runs at 1000 a second.
        estimate.Report(TimeSpan.FromSeconds(0), 0);
        estimate.Report(TimeSpan.FromSeconds(1), 10);

        var early = estimate.BytesPerSecond;

        for (var second = 2; second <= 8; second++)
        {
            estimate.Report(TimeSpan.FromSeconds(second), 10 + ((second - 1) * 1000));
        }

        // The window has rolled past the slow start, so the rate reflects now.
        estimate.BytesPerSecond.Should().BeGreaterThan(early * 10);
        estimate.BytesPerSecond.Should().BeApproximately(1000, 1);
    }

    [Fact]
    public void TheRateIsUnknownUntilThereAreTwoSamples()
    {
        var estimate = new TransferEstimate();
        estimate.Reset(totalBytes: 1000);
        estimate.Report(TimeSpan.Zero, 0);

        estimate.BytesPerSecond.Should().Be(0);
        estimate.Remaining.Should().BeNull("the footer stays quiet rather than printing a wild figure");
    }

    [Fact]
    public void AnUnknownTotalYieldsNoRemainingTime()
    {
        var estimate = new TransferEstimate();
        estimate.Reset(totalBytes: 0);
        estimate.Report(TimeSpan.FromSeconds(0), 0);
        estimate.Report(TimeSpan.FromSeconds(1), 500);

        estimate.Remaining.Should().BeNull();
        estimate.Fraction.Should().Be(0);
    }

    [Fact]
    public void PercentIsFlooredSoARowNeverReadsDoneEarly()
    {
        var estimate = new TransferEstimate();
        estimate.Reset(totalBytes: 1000);
        estimate.Report(TimeSpan.FromSeconds(0), 0);
        estimate.Report(TimeSpan.FromSeconds(1), 999);

        estimate.Percent.Should().Be(99);
    }

    [Fact]
    public void ProgressBeyondTheStatedTotalClampsRatherThanOverrunning()
    {
        var estimate = new TransferEstimate();
        estimate.Reset(totalBytes: 1000);
        estimate.Report(TimeSpan.FromSeconds(0), 0);
        estimate.Report(TimeSpan.FromSeconds(1), 1200);

        estimate.Fraction.Should().Be(1);
        estimate.Percent.Should().Be(100);
        estimate.BytesRemaining.Should().Be(0);
    }

    [Theory]
    [InlineData(52, "52 s left")]
    [InlineData(59.4, "60 s left")]
    [InlineData(150, "3 min left")]
    [InlineData(3600, "1 h left")]
    [InlineData(4800, "1 h 20 min left")]
    public void RemainingTimeReadsInWholeUnits(double seconds, string expected) =>
        TransferEstimate.FormatRemaining(TimeSpan.FromSeconds(seconds), CultureInfo.InvariantCulture)
            .Should().Be(expected);

    [Fact]
    public void AnUnknownRemainingTimePrintsNothing() =>
        TransferEstimate.FormatRemaining(null).Should().BeEmpty();
}
