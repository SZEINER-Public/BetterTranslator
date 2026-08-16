using System.Linq;
using BetterTranslator.Engine.Documents;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Putting a translated paragraph back into the line count it came from.
///
/// This is what makes paragraph translation safe: a document whose line count
/// changed cannot be written over the original.
/// </summary>
public sealed class LineSplitterTests
{
    private const string Paragraph =
        "Úložiště vyhledá model podle jména a v případě nejednoznačnosti "
        + "se vrátí k připnuté revizi, což je to, co udržuje běh opakovatelný "
        + "napříč stroji a mezi jednotlivými spuštěními.";

    [Fact]
    public void TheLineCountComesBackExactly()
    {
        LineSplitter.Split(Paragraph, 3).Should().HaveCount(3);
        LineSplitter.Split(Paragraph, 1).Should().HaveCount(1);
        LineSplitter.Split(Paragraph, 7).Should().HaveCount(7);
    }

    [Fact]
    public void NoWordIsEverLost()
    {
        // Losing words is the one outcome worse than an uneven wrap.
        foreach (var count in new[] { 1, 2, 3, 5, 9 })
        {
            var lines = LineSplitter.Split(Paragraph, count)!;

            string.Join(' ', lines).Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Should().BeEquivalentTo(
                    Paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries),
                    o => o.WithStrictOrdering(),
                    $"splitting into {count} lines must not drop or reorder a word");
        }
    }

    [Fact]
    public void EveryLineGetsAtLeastOneWord()
    {
        var words = "one two three four five";

        LineSplitter.Split(words, 5).Should().OnlyContain(l => l.Length > 0);
        LineSplitter.Split(words, 5).Should().HaveCount(5);
    }

    [Fact]
    public void TheLinesComeOutRoughlyEven()
    {
        // They came from a wrapped paragraph, so equal length is what puts them
        // back looking like the original.
        var lines = LineSplitter.Split(Paragraph, 4)!;

        var longest = lines.Max(l => l.Length);
        var shortest = lines.Min(l => l.Length);

        longest.Should().BeLessThan(shortest * 3, "a balanced wrap, not one long line and three short");
    }

    [Fact]
    public void FewerWordsThanLinesIsRefusedRatherThanInvented()
    {
        // Null so the caller falls back to translating line by line rather than
        // writing something it made up.
        LineSplitter.Split("two words", 5).Should().BeNull();
        LineSplitter.Split("", 2).Should().BeNull();
        LineSplitter.Split(Paragraph, 0).Should().BeNull();
    }

    [Fact]
    public void APrefixIsReAppliedToEveryLine()
    {
        // A blockquote's marker belongs to each line, not to the paragraph: a
        // paragraph that lost it on lines two onward stops being a blockquote
        // halfway through.
        LineSplitter.Split(Paragraph, 3, "> ")!.Should().OnlyContain(l => l.StartsWith("> "));
    }
}
