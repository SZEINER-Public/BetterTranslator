using BetterTranslator.App.ViewModels;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// S11. The action button names what it will actually do, and the two
/// chat-derived rows say why they are unavailable rather than just greying out.
/// </summary>
public sealed class AddSourcesTests
{
    private static AddSourcesViewModel Create(int chats = 0, int words = 0, bool indexed = false, string? project = null) =>
        new(indexed, project, chats, words, _ => Task.CompletedTask, () => { });

    [Theory]
    [InlineData(false, false, 0, false, false, "Start indexing")]
    [InlineData(true, false, 0, false, false, "Index folder")]
    [InlineData(false, false, 3, false, false, "Index 3 files")]
    [InlineData(false, false, 1, false, false, "Index 1 file")]
    [InlineData(true, false, 3, false, false, "Index folder and 3 files")]
    [InlineData(false, true, 0, false, false, "Index repository")]
    [InlineData(true, true, 2, false, false, "Index folder, repository and 2 files")]
    [InlineData(false, false, 0, true, false, "Index chats")]
    [InlineData(true, false, 0, false, true, "Index folder and chosen terms")]
    public void TheActionNamesTheRealSelection(
        bool folder, bool repository, int files, bool chats, bool terms, string expected) =>
        AddSourcesViewModel.BuildActionLabel(folder, repository, files, chats, terms).Should().Be(expected);

    [Fact]
    public void NothingPickedBlocksTheActionAndSaysSo()
    {
        var vm = Create();

        vm.CanStart.Should().BeFalse();
        vm.BlockedReason.Should().Be("Pick at least one");
        vm.ActionLabel.Should().Be("Start indexing");
    }

    [Fact]
    public void TickingFolderWithoutChoosingOneDoesNotEnableTheAction()
    {
        var vm = Create();

        vm.FolderSelected = true;

        // The row is ticked but no folder was chosen, so there is nothing to do.
        vm.CanStart.Should().BeFalse();
    }

    [Fact]
    public void BothChatRowsStateWhyTheyAreUnavailableWhenNoChatExists()
    {
        var vm = Create(chats: 0);

        vm.HasChats.Should().BeFalse();
        vm.AllChatsDetail.Should().Be("No chats yet - this needs at least one translation");
        vm.ChosenTermsDetail.Should().Be("No chats yet - this needs at least one translation");
    }

    [Fact]
    public void SelectingAChatRowWithNoChatsCannotStart()
    {
        var vm = Create(chats: 0);

        vm.AllChatsSelected = true;
        vm.ChosenTermsSelected = true;

        vm.CanStart.Should().BeFalse();
    }

    [Fact]
    public void TheChatRowShowsALiveCount()
    {
        Create(chats: 2, words: 412).AllChatsDetail.Should().Be("2 chats - 412 translated words");
        Create(chats: 1, words: 15).AllChatsDetail.Should().Be("1 chat - 15 translated words");
        Create(chats: 4, words: 15).AllChatsDetail.Should().Be("4 chats - 15 translated words");
    }

    [Fact]
    public void TheSubheadDiffersBeforeAndAfterAnythingIsIndexed()
    {
        Create(indexed: false).Subhead
            .Should().Be("Nothing is indexed yet. Pick what memory should learn from - it all stays on this machine.");

        Create(indexed: true, project: "bubble-desktop").Subhead
            .Should().Be("Already indexing bubble-desktop. Anything you add is indexed alongside it.");
    }

    [Fact]
    public void TheSubheadUsesAsciiPunctuationOnly()
    {
        var subhead = Create().Subhead;

        subhead.Should().NotContain("\u2014", "no em dash");
        subhead.Should().NotContain("\u2013", "no en dash");
        subhead.Should().Contain(" - ", "a spaced hyphen is the separator");
    }

    [Fact]
    public void TheFileDetailReadsAsTheRealCount()
    {
        var vm = Create();

        vm.FilesDetail.Should().Be("Pick single PDF, DOCX, MD, JSON or TXT files");

        vm.ChosenFiles.Add("a.md");
        vm.ChosenFiles.Add("b.txt");
        vm.ChosenFiles.Add("c.pdf");

        // Recomputed from the list, not from a counter kept alongside it.
        vm.FilesDetail.Should().Be("3 files chosen");
    }

    [Fact]
    public void ChoosingAFolderEnablesTheActionAndNamesIt()
    {
        var vm = Create();

        vm.FolderPath = @"C:\dev\bubble-desktop";
        vm.FolderSelected = true;

        vm.CanStart.Should().BeTrue();
        vm.BlockedReason.Should().BeNull();
        vm.ActionLabel.Should().Be("Index folder");
        vm.FolderDetail.Should().Be(@"C:\dev\bubble-desktop");
    }
}
