using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Engine.Markdown;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Whether a pasted message is offered the View/Source pair at all.
///
/// The toggle is only as good as the test that decides a message is Markdown,
/// and that test is invisible: a message it says no to renders as prose with no
/// control on screen and nothing explaining why.
/// </summary>
public sealed class MarkdownMessageDetectionTests
{
    [Theory]
    [InlineData("# Title\n\nSome prose under it.")]
    [InlineData("## Build and publish\n\nRun the build from the repository root.")]
    [InlineData("- one\n- two\n- three")]
    [InlineData("1. first\n2. second")]
    [InlineData("Use **bold** in the middle of a sentence.")]
    [InlineData("Call `dotnet build` before shipping.")]
    [InlineData("> A quoted passage worth keeping.")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |")]
    [InlineData("```bash\ndotnet build\n```")]
    [InlineData("- [x] done\n- [ ] not done")]
    [InlineData("---\ntitle: x\n---\n\nBody.")]
    [InlineData("A line with a [link](https://example.com) in it.")]
    public void RealMarkdownIsDetected(string markdown) =>
        MarkdownSyntax.HasStructure(markdown).Should().BeTrue();

    [Theory]
    [InlineData("Just an ordinary sentence.")]
    [InlineData("Two sentences. Neither is Markdown.")]
    [InlineData("<context>\nPROJECT: something\n</context>")]
    [InlineData("")]
    public void ProseAndRawHtmlAreNot(string text) =>
        MarkdownSyntax.HasStructure(text).Should().BeFalse();

    [Theory]
    [InlineData("# Title\n\nSome prose under it.")]
    [InlineData("## Build\n\nRun it.")]
    [InlineData("- one\n- two")]
    [InlineData("Use **bold** here.")]
    public void ADetectedMessageIsOfferedTheSwitch(string markdown)
    {
        var view = new EntryViewModel(Entry(markdown));

        view.IsMarkdown.Should().BeTrue();
        view.HasFormatToggle.Should().BeTrue("the View/Source pair binds to this");
        view.ShowsRendered.Should().BeTrue("a Markdown message opens rendered, as the file preview does");
        view.SourceBlocks.Should().NotBeEmpty("the rendered view reads from these");
    }

    [Fact]
    public void TheSwitchFlipsBothColumns()
    {
        var view = new EntryViewModel(Entry("# Title\n\nProse."));

        view.ShowsSourceMarkdown.Should().BeTrue();
        view.ShowsSourceSyntax.Should().BeFalse();

        view.ToggleFormatCommand.Execute(null);

        view.IsSourceMode.Should().BeTrue();
        view.ShowsRendered.Should().BeFalse();
        view.ShowsSourceMarkdown.Should().BeFalse();
        view.ShowsSourceSyntax.Should().BeTrue("Source mode shows the literal Markdown");
    }

    [Fact]
    public void AnOrdinaryMessageIsOfferedNothing()
    {
        var view = new EntryViewModel(Entry("Just an ordinary sentence."));

        view.HasFormatToggle.Should().BeFalse();
        view.ShowsSourceProse.Should().BeTrue();
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
