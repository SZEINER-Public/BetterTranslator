using BetterTranslator.Engine.Corpus;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Did the extraction actually produce text?
///
/// Nothing downstream can answer this: a scanned PDF returns a string, and glyph
/// residue indexed as prose produces chunks that match nothing and embeddings
/// that pull real queries towards noise.
/// </summary>
public sealed class ExtractedTextTests
{
    [Fact]
    public void OrdinaryProseIsText()
    {
        ExtractedText.LooksLikeText(
            "The store resolves a model by name and falls back to the pinned revision.")
            .Should().BeTrue();
    }

    [Fact]
    public void ProseInAnyScriptIsText()
    {
        // The gate counts \p{L}, not [a-z], so it must not quietly reject the
        // languages this app exists to translate into.
        ExtractedText.LooksLikeText("Sestavení selhalo, protože úložiště chybělo.").Should().BeTrue();
        ExtractedText.LooksLikeText("Сборка не удалась, потому что хранилище отсутствовало.").Should().BeTrue();
        ExtractedText.LooksLikeText("ビルドはストレージが見つからなかったため失敗しました。").Should().BeTrue();
    }

    [Fact]
    public void StreamResidueIsNot()
    {
        ExtractedText.LooksLikeText(@"\x00\xFF<</Type/Page>>endobj\x01\x02%%EOF\xDE\xAD\xBE\xEF")
            .Should().BeFalse();
    }

    [Fact]
    public void TooShortToJudgeIsRejected()
    {
        // A two-word heading is 100% letters and still is not a document.
        ExtractedText.LooksLikeText("Chapter one").Should().BeFalse();
        ExtractedText.LooksLikeText("").Should().BeFalse();
        ExtractedText.LooksLikeText("   \n  ").Should().BeFalse();
        ExtractedText.LooksLikeText(null).Should().BeFalse();
    }

    [Fact]
    public void TheReasonSaysWhichFailureItWas()
    {
        // "0 chunks" with no reason reads as a bug in the indexer; the count is
        // what tells someone their PDF is scanned rather than empty.
        ExtractedText.RejectionReason(null).Should().Be("extraction produced nothing");
        ExtractedText.RejectionReason("Chapter one").Should().Contain("characters extracted");
        ExtractedText.RejectionReason(@"\x00\xFF<</Type/Page>>endobj\x01%%EOF\xDE\xAD\xBE\xEF")
            .Should().Contain("no text layer");

        ExtractedText.RejectionReason("The store resolves a model by name and falls back.")
            .Should().BeNull("a document that reads fine has no reason to report");
    }
}
