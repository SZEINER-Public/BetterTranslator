using BetterTranslator.Indexing.Chunking;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Chunk boundaries decide what a retrieval can find, and a chunk cut through a
/// word embeds as nonsense, so the boundary rules are pinned here.
/// </summary>
public sealed class TextChunkerTests
{
    private static readonly ChunkOptions Small = new()
    {
        TargetCharacters = 100,
        MaxCharacters = 140,
        MinimumCharacters = 40,
        OverlapCharacters = 20,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n  ")]
    public void EmptyInputProducesNoChunks(string? text) =>
        TextChunker.Chunk(text).Should().BeEmpty();

    [Fact]
    public void TextShorterThanTheCeilingIsOneChunk()
    {
        var chunks = TextChunker.Chunk("Open Settings and pin the sidebar.", Small);

        chunks.Should().ContainSingle();
        chunks[0].Text.Should().Be("Open Settings and pin the sidebar.");
        chunks[0].Ordinal.Should().Be(0);
    }

    [Fact]
    public void NoChunkExceedsTheCeiling()
    {
        var text = string.Join(" ", Enumerable.Repeat("workspace", 400));

        var chunks = TextChunker.Chunk(text, Small);

        chunks.Should().NotBeEmpty();
        chunks.Should().OnlyContain(c => c.Text.Length <= Small.MaxCharacters);
    }

    [Fact]
    public void AWordIsNeverSplitInHalf()
    {
        var text = string.Join(" ", Enumerable.Repeat("nastaveni", 400));

        var chunks = TextChunker.Chunk(text, Small);

        // Every chunk is whole copies of the word, so no fragment survived.
        foreach (var chunk in chunks)
        {
            foreach (var word in chunk.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                word.Should().Be("nastaveni");
            }
        }
    }

    [Fact]
    public void AParagraphBreakIsPreferredOverASentenceEnd()
    {
        var first = new string('a', 60) + ".";
        var second = new string('b', 60) + ".";
        var text = first + "\n\n" + second + " " + new string('c', 200);

        var chunks = TextChunker.Chunk(text, Small);

        chunks[0].Text.Should().Be(first, "the blank line is the cleanest cut");
    }

    [Fact]
    public void ASentenceEndIsPreferredOverBareWhitespace()
    {
        var sentence = new string('a', 80) + ". ";
        var text = sentence + string.Join(" ", Enumerable.Repeat("tail", 60));

        var chunks = TextChunker.Chunk(text, Small);

        chunks[0].Text.Should().EndWith(".");
    }

    [Fact]
    public void ChunksOverlapSoATermOnABoundaryStaysFindable()
    {
        var text = string.Join(" ", Enumerable.Range(0, 200).Select(i => "w" + i));

        var chunks = TextChunker.Chunk(text, Small);

        chunks.Count.Should().BeGreaterThan(1);

        // The start of the second chunk appears at the end of the first.
        var firstWordOfSecond = chunks[1].Text.Split(' ')[0];
        chunks[0].Text.Should().Contain(firstWordOfSecond);
    }

    [Fact]
    public void OrdinalsRunFromZeroWithoutGaps()
    {
        var text = string.Join(" ", Enumerable.Repeat("workspace", 300));

        var chunks = TextChunker.Chunk(text, Small);

        chunks.Select(c => c.Ordinal).Should().Equal(Enumerable.Range(0, chunks.Count));
    }

    [Fact]
    public void ASingleUnbrokenRunLongerThanTheCeilingIsStillCut()
    {
        // No whitespace at all: the chunker must not grow without bound.
        var text = new string('x', 1000);

        var chunks = TextChunker.Chunk(text, Small);

        chunks.Should().NotBeEmpty();
        chunks.Should().OnlyContain(c => c.Text.Length <= Small.MaxCharacters);
        string.Concat(chunks.Select(c => c.Text)).Length.Should().BeGreaterThanOrEqualTo(1000 - Small.MaxCharacters);
    }

    [Fact]
    public void ChunksNeverOpenOnWhitespace()
    {
        var text = string.Join("\n\n", Enumerable.Repeat(new string('a', 90) + ".", 12));

        var chunks = TextChunker.Chunk(text, Small);

        chunks.Should().OnlyContain(c => c.Text.Length > 0 && !char.IsWhiteSpace(c.Text[0]));
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("one", 1)]
    [InlineData("one two three", 3)]
    [InlineData("  spaced   out  words  ", 3)]
    [InlineData("line\nbreak\tseparated", 3)]
    public void WordsAreCountedOnceForEverySurface(string text, int expected) =>
        TextChunker.CountWords(text).Should().Be(expected);

    [Fact]
    public void EveryChunkCarriesItsOwnWordCount()
    {
        var chunks = TextChunker.Chunk(string.Join(" ", Enumerable.Repeat("term", 300)), Small);

        chunks.Should().OnlyContain(c => c.WordCount == TextChunker.CountWords(c.Text));
        chunks.Should().OnlyContain(c => c.WordCount > 0);
    }
}
