using System.IO;
using BetterTranslator.Indexing.Markdown;
using BetterTranslator.Indexing.Readers;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Each reader against a real file of its format. The preview renders from
/// whatever these return, so a reader that quietly produces nothing would show
/// an empty pane rather than fail.
/// </summary>
public sealed class DocumentReaderTests : IDisposable
{
    private readonly Fixtures _files = new();
    private readonly DocumentReaders _readers = new();

    public void Dispose() => _files.Dispose();

    [Fact]
    public async Task TextKeepsItsBlankLinesAndOffersNoRenderingSwitch()
    {
        var content = await _readers.ReadAsync(_files.TextFile, CancellationToken.None);

        content.Format.Should().Be(DocumentKind.PlainText);
        content.Text.Should().Contain("Release notes 2.4");
        content.Text.Should().Contain("\n\n\n", "blank lines are preserved");
        content.HasRenderingSwitch.Should().BeFalse("only Markdown shows Formatted and Source");
        content.IsMonospace.Should().BeTrue();
    }

    /// <summary>
    /// A resource file is the i18n case the JSON path exists for, and it reaches
    /// that path as the bytes it was written as: nothing reindented, nothing
    /// reordered, no formatted view offered over the top of it.
    /// </summary>
    [Fact]
    public async Task JsonArrivesAsWrittenAndOffersNoRenderingSwitch()
    {
        var content = await _readers.ReadAsync(_files.JsonFile, CancellationToken.None);

        content.Format.Should().Be(DocumentKind.Json);
        content.Text.Should().Be(Fixtures.JsonBody, "the reader reads, it does not reformat");
        content.Text.Should().Contain("\"sync\": \"Sync now\"");
        content.HasRenderingSwitch.Should().BeFalse("only Markdown shows Formatted and Source");
        content.IsMonospace.Should().BeTrue("indentation and key alignment are the structure");
    }

    [Fact]
    public async Task MarkdownCarriesBothTheLiteralSourceAndTheParsedBlocks()
    {
        var content = await _readers.ReadAsync(_files.MarkdownFile, CancellationToken.None);

        content.Format.Should().Be(DocumentKind.Markdown);
        content.HasRenderingSwitch.Should().BeTrue();

        // Source view shows the literal asterisks.
        content.Text.Should().Contain("**in sync**");

        // Formatted view gets a run actually marked bold.
        content.Blocks.Should().NotBeNull();
        AllInlines(content.Blocks!)
            .Should().Contain(i => i.Bold && i.Text.Contains("in sync"), "bold renders as bold");
    }

    [Fact]
    public async Task WordExtractsParagraphsAndFlattensTables()
    {
        var content = await _readers.ReadAsync(_files.WordFile, CancellationToken.None);

        content.Format.Should().Be(DocumentKind.Word);
        content.Text.Should().Contain("Open Settings and pin the sidebar to your workspace.");
        content.Text.Should().Contain("settings\tnastaveni", "table rows flatten to tab-separated terms");
        content.HasRenderingSwitch.Should().BeFalse();
    }

    [Fact]
    public async Task PdfExtractsItsText()
    {
        var content = await _readers.ReadAsync(_files.PdfFile, CancellationToken.None);

        content.Format.Should().Be(DocumentKind.Pdf);
        content.Text.Should().Contain("Bubble");
        content.Text.Should().Contain("workspace");
        content.HasRenderingSwitch.Should().BeFalse();
    }

    [Fact]
    public void EveryOfferedExtensionHasAReader()
    {
        foreach (var extension in DocumentReaders.SupportedExtensions)
        {
            _readers.IsSupported("sample" + extension).Should().BeTrue($"{extension} is offered by the file dialog");
        }
    }

    [Fact]
    public async Task AnUnsupportedExtensionIsRefusedByName()
    {
        var act = () => _readers.ReadAsync(Path.Combine(_files.Root, "archive.zip"), CancellationToken.None);

        (await act.Should().ThrowAsync<NotSupportedException>()).Which.Message.Should().Contain(".zip");
    }

    private static IEnumerable<MdInline> AllInlines(IReadOnlyList<MdBlock> blocks) =>
        blocks.SelectMany(block => block switch
        {
            MdHeading heading => heading.Inlines,
            MdParagraph paragraph => paragraph.Inlines,
            MdBulletList list => list.Items.SelectMany(item => item.Inlines.Concat(AllInlines(item.Children))),
            MdQuote quote => AllInlines(quote.Blocks),
            _ => [],
        });
}
