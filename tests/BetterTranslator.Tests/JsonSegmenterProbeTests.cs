using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Engine.Json;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class JsonSegmenterProbeTests
{
    [Theory]
    [InlineData("The price just covers the pre-tax cost.")]
    [InlineData("Cena zahrnuje pouze náklady.")]
    [InlineData("# A heading\n\nAnd a paragraph.")]
    [InlineData("- one\n- two")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("true")]
    [InlineData("42")]
    [InlineData("\"a bare string\"")]
    public void ProseIsAnsweredWithoutParsingIt(string text) =>
        JsonSegmenter.Segment(text).Should().BeNull();

    [Fact]
    public void ARealDocumentStillSegments()
    {
        var scalars = JsonSegmenter.Segment(SpecCorpus.ResourceJson);

        scalars.Should().NotBeNull();
        scalars!.Should().NotBeEmpty();
        scalars.Should().Contain(s => s.IsString && s.Text.Contains("Welcome back"));
    }

    [Fact]
    public void SomethingThatOpensLikeADocumentAndIsNotStillFallsThrough()
    {
        JsonSegmenter.Segment("{ this is not json at all").Should().BeNull();
        JsonSegmenter.Segment("[1, 2,").Should().BeNull();
    }

    [Fact]
    public void AProseEntryComputesNoJsonRows()
    {
        var view = new EntryViewModel(Entry("The committee approved the budget yesterday."));

        view.IsJson.Should().BeFalse();
        view.SourceRows.Should().BeEmpty();
        view.ResultRows.Should().BeEmpty();
    }

    [Fact]
    public void AResourceEntryStillGetsItsColumns()
    {
        var view = new EntryViewModel(Entry(SpecCorpus.ResourceJson));

        view.IsJson.Should().BeTrue();
        view.SourceRows.Should().NotBeEmpty();
    }

    private static Entry Entry(string source) => new()
    {
        Id = Guid.NewGuid(),
        ChatId = Guid.NewGuid(),
        Kind = EntryKind.Sentence,
        Source = source,
        Result = string.Empty,
        CreatedAt = DateTimeOffset.Now,
        State = EntryState.Pending,
        TargetLanguage = "Czech",
    };
}
