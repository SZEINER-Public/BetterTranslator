using System.Linq;
using BetterTranslator.Engine.Slop;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

/// <summary>
/// Proposing a glossary from a document and its translation.
///
/// The build decides nothing. It measures which terms the translation treats two
/// ways at once and proposes the majority; a person settles those. That is the
/// whole design, and it exists because asking the model to classify was built
/// and measured first: EuroLLM answered KEEP for ten words out of ten, including
/// "the".
/// </summary>
public sealed class GlossaryBuilderTests(ITestOutputHelper output)
{
    private const string Source = """
        # The store

        The `store` holds every model. Point the `store` at a folder and the
        store is created there. A second store is never made.
        Run `bootstrap` to prepare it; bootstrap writes a log.
        The bootstrap step is quick.
        """;

    /// <summary>
    /// "store" is translated on some lines and kept on others, which is the
    /// defect. "bootstrap" is translated every time, which is fine.
    /// </summary>
    private const string Translated = """
        # Úložiště

        The `store` holds every model. Point the `store` at a folder and the
        úložiště is created there. A second store is never made.
        Run `bootstrap` to prepare it; zavaděč writes a log.
        The zavaděč step is quick.
        """;

    [Fact]
    public void TheVocabularyComesFromTheDocumentsOwnCodeSpans()
    {
        var proposal = GlossaryBuilder.Build(Source);

        output.WriteLine(string.Join(", ", proposal.Vocabulary.Select(t => $"{t.Term}({t.Count})")));

        proposal.Vocabulary.Should().Contain(t => t.Term == "store");
        proposal.Vocabulary.Should().Contain(t => t.Term == "bootstrap");

        // Prose-only words never appear, however often they occur: the author
        // did not mark them technical.
        proposal.Vocabulary.Should().NotContain(t => t.Term == "folder");
        proposal.Vocabulary.Should().NotContain(t => t.Term == "the", "closed-class English is removed");
    }

    [Fact]
    public void OnlyTermsTreatedBothWaysNeedADecision()
    {
        var proposal = GlossaryBuilder.Build(Source, Translated, minOccurrences: 2);

        output.WriteLine(string.Join("; ", proposal.Inconsistent.Select(
            i => $"{i.Term} kept={i.Kept} translated={i.Translated} -> {i.Majority}")));

        proposal.Inconsistent.Should().Contain(i => i.Term == "store",
            "it is kept on one line and translated on another, which is the whole defect");

        proposal.Inconsistent.Should().NotContain(i => i.Term == "bootstrap",
            "a term translated every time is settled already");
    }

    [Fact]
    public void ATranslationWithADifferentLineCountIsNotComparedAnyway()
    {
        // The measurement pairs lines by index; a shifted pairing would report
        // every term as inconsistent.
        var proposal = GlossaryBuilder.Build(Source, Translated + "\nan extra line");

        proposal.Inconsistent.Should().BeEmpty();
        proposal.Vocabulary.Should().NotBeEmpty("the vocabulary is still derived");
    }

    [Fact]
    public void WithNoTranslationNothingIsMeasured()
    {
        var proposal = GlossaryBuilder.Build(Source);

        proposal.Inconsistent.Should().BeEmpty();
        proposal.HasAnything.Should().BeTrue();
    }

    [Fact]
    public void TheProposedMarkdownIsAGlossaryTheParserCanReadBack()
    {
        // The output is meant to be edited and then used, so the format it
        // proposes has to be the format the glossary loader accepts.
        var proposal = GlossaryBuilder.Build(Source, Translated, minOccurrences: 2);

        output.WriteLine(proposal.Markdown);

        proposal.Markdown.Should().Contain("## Required terms").And.Contain("## Full derived vocabulary");

        var parsed = Glossary.Parse(proposal.Markdown);

        // The Required terms rows are deliberately blank -- a person fills them
        // in -- so nothing is parsed out of them, and that is correct rather
        // than a failure.
        parsed.Terms.Should().BeEmpty("the stem and word columns are left for a person to complete");
    }

    [Fact]
    public void ADocumentWithNoCodeSpansProposesNothing()
    {
        var proposal = GlossaryBuilder.Build("Just ordinary prose with nothing marked as technical at all.");

        proposal.Vocabulary.Should().BeEmpty();
        proposal.HasAnything.Should().BeFalse();
    }
}
