using BetterTranslator.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// What a drag over the chat is allowed to carry. This decides the cursor while
/// the pointer is still moving, so it has to agree with the formats a reader
/// exists for and with the file picker's own filter.
/// </summary>
public sealed class AttachmentDropTests
{
    [Theory]
    [InlineData("notes.md")]
    [InlineData("terms.pdf")]
    [InlineData("handbook.docx")]
    [InlineData("release-notes.txt")]
    [InlineData("strings.en.json")]
    public void AReadableFormatIsAccepted(string name) =>
        ChatWorkspaceViewModel.Readable([name]).Should().ContainSingle().Which.Should().Be(name);

    [Theory]
    [InlineData("archive.zip")]
    [InlineData("photo.png")]
    [InlineData("sheet.xlsx")]
    [InlineData("no-extension")]
    public void AFormatWithNoReaderIsRefused(string name) =>
        ChatWorkspaceViewModel.Readable([name]).Should().BeEmpty();

    /// <summary>
    /// Windows hands paths over however they were cased on disk, so the check
    /// cannot be case sensitive.
    /// </summary>
    [Fact]
    public void ExtensionCaseDoesNotMatter() =>
        ChatWorkspaceViewModel.Readable(["TERMS.PDF", "Notes.Md"]).Should().HaveCount(2);

    /// <summary>
    /// A mixed drag is not refused outright: it takes what it can read and
    /// leaves the rest, which is what the accepting cursor promised.
    /// </summary>
    [Fact]
    public void AMixedDragKeepsOnlyWhatItCanRead() =>
        ChatWorkspaceViewModel.Readable(["a.pdf", "b.zip", "c.txt"])
            .Should().BeEquivalentTo(["a.pdf", "c.txt"]);

    /// <summary>
    /// Dragged text and browser selections arrive with no file list at all.
    /// </summary>
    [Fact]
    public void NothingDraggedReadsAsNothing() =>
        ChatWorkspaceViewModel.Readable(null).Should().BeEmpty();
}
