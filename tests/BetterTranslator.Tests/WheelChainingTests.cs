using BetterTranslator.App.Controls;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// When the wheel belongs to the scroller outside rather than the one under the
/// pointer. A WPF ScrollViewer marks the event handled whether or not it could
/// use it, so without this the history stopped moving whenever the pointer sat
/// over a file preview.
/// </summary>
public sealed class WheelChainingTests
{
    private const int Down = -120;

    private const int Up = 120;

    [Theory]
    [InlineData(0, 0, Down)]
    [InlineData(0, 0, Up)]
    public void AScrollerWithNothingToScrollAlwaysPassesTheWheelOut(
        double scrollableHeight,
        double verticalOffset,
        int delta) =>
        WheelChaining.PassesOutward(scrollableHeight, verticalOffset, delta).Should().BeTrue();

    [Theory]
    [InlineData(500, 0, Down)]
    [InlineData(500, 250, Down)]
    [InlineData(500, 250, Up)]
    [InlineData(500, 500, Up)]
    public void AScrollerThatCanStillMoveKeepsTheWheel(
        double scrollableHeight,
        double verticalOffset,
        int delta) =>
        WheelChaining.PassesOutward(scrollableHeight, verticalOffset, delta).Should().BeFalse();

    [Theory]
    [InlineData(500, 500, Down)]
    [InlineData(500, 0, Up)]
    public void AScrollerAtItsEndPassesTheWheelOutInThatDirection(
        double scrollableHeight,
        double verticalOffset,
        int delta) =>
        WheelChaining.PassesOutward(scrollableHeight, verticalOffset, delta).Should().BeTrue();
}
