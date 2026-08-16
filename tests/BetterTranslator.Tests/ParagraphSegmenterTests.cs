using System.Linq;
using BetterTranslator.Engine.Documents;
using BetterTranslator.Engine.Slop;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Which lines of a document may be translated, and which belong together.
///
/// Sending a line at a time gives the model no sentence to work with, so it
/// renders each fragment on its own and the result reads as fragments. Sending
/// the whole document loses the line count, and a document whose line count
/// changed cannot be written back over the original.
/// </summary>
public sealed class ParagraphSegmenterTests
{
    private static readonly DoNotTranslateLists Terms = DoNotTranslate.Load();

    private static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    [Fact]
    public void ConsecutiveProseLinesBecomeOneParagraph()
    {
        var lines = Lines("""
            The store resolves a model by name and falls back to
            the pinned revision when the name is ambiguous, which
            is what keeps a run repeatable across machines.
            """);

        var groups = ParagraphSegmenter.Paragraphs(lines, ParagraphSegmenter.Candidates(lines));

        groups.Should().ContainSingle();
        groups[0].Should().BeEquivalentTo([0, 1, 2]);
    }

    [Fact]
    public void AListingRowIsARowNotASentenceThatWraps()
    {
        // Joining them destroyed the block this was written to translate: seven
        // aligned rows became one welded paragraph, re-wrapped, with every path
        // buried mid-line and the column gone.
        ParagraphSegmenter.SameParagraph(
            "src/store/resolve.cs    resolves a model by name",
            "src/store/prune.cs      removes weights nothing references")
            .Should().BeFalse();
    }

    [Fact]
    public void AnythingWithItsOwnLineMarkerStandsAlone()
    {
        ParagraphSegmenter.SameParagraph("# Heading", "ordinary prose here").Should().BeFalse();
        ParagraphSegmenter.SameParagraph("- first item", "- second item").Should().BeFalse();
        ParagraphSegmenter.SameParagraph("1. first", "2. second").Should().BeFalse();
        ParagraphSegmenter.SameParagraph("| a | b |", "| c | d |").Should().BeFalse();
        ParagraphSegmenter.SameParagraph("<div>", "some prose").Should().BeFalse();
    }

    [Fact]
    public void AFinishedSentenceDoesNotContinueIntoTheNextLine()
    {
        // A wrapped line breaks MID-SENTENCE -- that is what wrapping is. Joining
        // ten finished sentences gains nothing, because each was already a
        // sentence, and costs the correspondence between line and source.
        ParagraphSegmenter.SameParagraph("The fire alarm was broken, but nobody knew.", "It's likely to rain.")
            .Should().BeFalse();

        ParagraphSegmenter.SameParagraph("What is that huge building in front of us?", "A big fire broke out.")
            .Should().BeFalse();

        ParagraphSegmenter.SameParagraph("He shouted \"stop!\"", "Then he ran.").Should().BeFalse();

        // Still joined where the break really is mid-sentence.
        ParagraphSegmenter.SameParagraph("The store resolves a model by name and", "falls back to the pinned revision.")
            .Should().BeTrue();
    }

    [Fact]
    public void TenIndependentSentencesStayTenUnits()
    {
        var lines = Lines("""
            The fire alarm was broken, but nobody knew.
            It's likely to rain.
            What is that huge building in front of us?
            A big fire broke out after the earthquake.
            Mr. White went to the store last night.
            """);

        var candidates = ParagraphSegmenter.Candidates(lines, Terms.Strict, minLetters: 1);

        candidates.Should().HaveCount(5);
        ParagraphSegmenter.Paragraphs(lines, candidates)
            .Should().BeEmpty("each line is its own sentence and its own unit");
    }

    [Fact]
    public void ABlockquoteContinuesOnlyIntoAnotherBlockquote()
    {
        ParagraphSegmenter.SameParagraph("> quoted prose here", "> and its continuation").Should().BeTrue();
        ParagraphSegmenter.SameParagraph("> quoted prose here", "ordinary prose here").Should().BeFalse();
    }

    [Fact]
    public void ATaggedFenceIsSkippedWholeAndAnUntaggedOneIsNot()
    {
        // A tagged fence is code and says so. An untagged one is where a document
        // puts an aligned listing, whose right-hand column is prose a reader of
        // the translation needs.
        var lines = Lines("""
            Ordinary prose before the fence.
            ```csharp
            var resolved = store.Resolve(name);
            ```
            ```
            src/store/resolve.cs    resolves a model by name
            ```
            Ordinary prose after the fence.
            """);

        var candidates = ParagraphSegmenter.Candidates(lines);

        candidates.Should().Contain(0).And.Contain(7);
        candidates.Should().NotContain(2, "a tagged fence is code");
        candidates.Should().Contain(5, "the right-hand column of a listing is prose");
    }

    [Fact]
    public void AShortHeadingIsStillTranslated()
    {
        // The prose floor applied to headings left a document with its body
        // translated and its section titles in English, which reads worse than
        // either extreme.
        TranslationCandidate.IsWorthSending("## Layout").Should().BeTrue();
        TranslationCandidate.IsWorthSending("## Run locally").Should().BeTrue();
    }

    [Fact]
    public void AHeadingThatIsOnlyAProductNameIsStillNeverSent()
    {
        // At four letters, "# biotank" came back as "# biologicky reaktor" -- a
        // product name destroyed. It is safe now only because do-not-translate
        // terms are stripped before the count.
        TranslationCandidate.IsWorthSending("# JSON", Terms.Strict).Should().BeFalse();
        TranslationCandidate.IsWorthSending("# ", Terms.Strict).Should().BeFalse();
    }

    [Fact]
    public void ASingleLineIsNotAParagraph()
    {
        // Nothing to gain from segmenting one line; the caller translates it
        // directly.
        var lines = Lines("""
            The store resolves a model by name.

            - a list item that is long enough
            """);

        ParagraphSegmenter.Paragraphs(lines, ParagraphSegmenter.Candidates(lines)).Should().BeEmpty();
    }
}
