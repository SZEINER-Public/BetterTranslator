using System.Globalization;
using BetterTranslator.Core.Services;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// One formatter is behind every size figure, so a model's size reads the same
/// in the download manager, the cache table and every confirmation.
/// </summary>
public sealed class ByteSizeTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(4301, "4.2 KB")]
    [InlineData(44040192, "42 MB")]
    [InlineData(192937984, "184 MB")]
    [InlineData(2576980378, "2.4 GB")]
    [InlineData(1825361100, "1.7 GB")]
    public void SizesFormatOnOneLadder(long bytes, string expected) =>
        ByteSize.Format(bytes, Invariant).Should().Be(expected);

    [Fact]
    public void OneDecimalBelowTenAndNoneAtOrAboveIt()
    {
        // 9.5 MB keeps its decimal; 10 MB does not.
        ByteSize.Format((long)(9.5 * 1024 * 1024), Invariant).Should().Be("9.5 MB");
        ByteSize.Format(10 * 1024 * 1024, Invariant).Should().Be("10 MB");
    }

    [Fact]
    public void BytesAreAlwaysWhole() =>
        ByteSize.Format(3, Invariant).Should().Be("3 B");

    [Fact]
    public void ANegativeCountReadsAsZeroRatherThanThrowing() =>
        ByteSize.Format(-1, Invariant).Should().Be("0 B");

    [Fact]
    public void RatesUseTheSameLadder() =>
        ByteSize.FormatRate(29675110, Invariant).Should().Be("28.3 MB/s");

    [Fact]
    public void AStalledRateIsNotNegative() =>
        ByteSize.FormatRate(0, Invariant).Should().Be("0 B/s");
}
