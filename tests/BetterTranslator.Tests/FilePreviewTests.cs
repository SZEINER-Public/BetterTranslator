using System.IO;
using BetterTranslator.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// S9 behaviour that is decided in the view model rather than in markup: which
/// view is showing, whether the rendering switch exists at all, and that
/// choosing a version never starts a translation.
/// </summary>
public sealed class FilePreviewTests : IDisposable
{
    private readonly Fixtures _files = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task MarkdownOpensFormattedAndOffersTheSwitch()
    {
        var preview = new FilePreviewViewModel();
        await preview.OpenAsync(_files.MarkdownFile, CancellationToken.None);

        preview.IsOpen.Should().BeTrue();
        preview.FileName.Should().Be("README.md");
        preview.HasRenderingSwitch.Should().BeTrue();
        preview.ShowFormatted.Should().BeTrue("Markdown opens formatted");
        preview.Blocks.Should().NotBeEmpty();
    }

    [Fact]
    public async Task TheSwitchTogglesBetweenFormattedAndTheLiteralSource()
    {
        var preview = new FilePreviewViewModel();
        await preview.OpenAsync(_files.MarkdownFile, CancellationToken.None);

        preview.ShowSourceViewCommand.Execute(null);

        preview.ShowFormatted.Should().BeFalse();
        preview.ShowSourceText.Should().BeTrue();
        preview.SourceText.Should().Contain("**in sync**", "Source shows the asterisks");

        preview.ShowFormattedViewCommand.Execute(null);
        preview.ShowFormatted.Should().BeTrue();
    }

    [Theory]
    [InlineData("txt")]
    [InlineData("docx")]
    [InlineData("pdf")]
    public async Task EveryOtherFormatShowsOneViewAndNoSwitch(string kind)
    {
        var path = kind switch
        {
            "txt" => _files.TextFile,
            "docx" => _files.WordFile,
            _ => _files.PdfFile,
        };

        var preview = new FilePreviewViewModel();
        await preview.OpenAsync(path, CancellationToken.None);

        preview.HasRenderingSwitch.Should().BeFalse();
        preview.ShowFormatted.Should().BeFalse();
        preview.ShowSourceText.Should().BeTrue("the single view is always shown");
    }

    [Fact]
    public async Task ChoosingTranslatedSelectsTheVersionAndStartsNothing()
    {
        var preview = new FilePreviewViewModel();
        await preview.OpenAsync(_files.MarkdownFile, CancellationToken.None);

        preview.ShowTranslatedVersionCommand.Execute(null);

        preview.ShowTranslated.Should().BeTrue();
        preview.VersionLabel.Should().Be("Translate");

        // Which version is shown and how it is rendered are two questions. A
        // markdown file has a formatted view whichever version is on screen, so
        // choosing Translate must not throw the reader back to raw text.
        preview.ShowFormatted.Should().BeTrue("the rendering choice is untouched by the version");
        preview.ShowSourceText.Should().BeFalse();

        // Nothing has produced a translation, so the notice stands and the
        // source is still what is on screen.
        preview.HasTranslation.Should().BeFalse();
        preview.VersionNotice.Should().Be("Source shown until you run it");
        preview.ShowVersionNotice.Should().BeTrue();
        preview.SourceText.Should().Contain("Bubble Desktop");
    }

    [Fact]
    public async Task ReopeningAFileResetsBackToTranslateAndFormatted()
    {
        var preview = new FilePreviewViewModel();
        await preview.OpenAsync(_files.MarkdownFile, CancellationToken.None);
        preview.ShowSourceVersionCommand.Execute(null);
        preview.ShowSourceViewCommand.Execute(null);

        await preview.OpenAsync(_files.MarkdownFile, CancellationToken.None);

        preview.ShowTranslated.Should().BeTrue();
        preview.IsFormatted.Should().BeTrue();
    }

    [Fact]
    public async Task ClosingLeavesTheContentLoadedButHidden()
    {
        var preview = new FilePreviewViewModel();
        await preview.OpenAsync(_files.TextFile, CancellationToken.None);

        preview.CloseCommand.Execute(null);

        preview.IsOpen.Should().BeFalse();
    }

    [Fact]
    public async Task ThePathShownIsTheRealOne()
    {
        var preview = new FilePreviewViewModel();
        await preview.OpenAsync(_files.TextFile, CancellationToken.None);

        preview.FilePath.Should().Be(_files.TextFile);
        File.Exists(preview.FilePath).Should().BeTrue();
    }
}
