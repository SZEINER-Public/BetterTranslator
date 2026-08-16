using System;
using System.IO;
using System.Linq;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Indexing.Markdown;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class EntryDisclosureTests
{
    [Fact]
    public void AShortEntryIsNotWorthFolding()
    {
        var view = new EntryViewModel(Entry("The build is green.", "Sestavení je zelené."));

        view.CanCollapse.Should().BeFalse();
        view.IsCollapsed.Should().BeFalse("a control that reveals one more line costs more than it gives");
    }

    [Fact]
    public void ALongEntryStartsFolded()
    {
        var view = new EntryViewModel(Entry(Long(), Long()));

        view.CanCollapse.Should().BeTrue();
        view.IsExpanded.Should().BeFalse();
        view.IsCollapsed.Should().BeTrue();
        view.DisclosureLabel.Should().Be("Show more");
    }

    [Fact]
    public void TheControlOpensAndClosesIt()
    {
        var view = new EntryViewModel(Entry(Long(), Long()));

        view.ToggleExpandCommand.Execute(null);

        view.IsExpanded.Should().BeTrue();
        view.IsCollapsed.Should().BeFalse();
        view.DisclosureLabel.Should().Be("Show less");

        view.ToggleExpandCommand.Execute(null);

        view.IsCollapsed.Should().BeTrue();
        view.DisclosureLabel.Should().Be("Show more");
    }

    [Fact]
    public void ALongAnswerToAShortQuestionFoldsToo()
    {
        var view = new EntryViewModel(Entry("Explain the routing table.", string.Empty));

        view.CanCollapse.Should().BeFalse();

        view.Result = Long();

        view.CanCollapse.Should().BeTrue("the result grew past the fold");
        view.IsCollapsed.Should().BeTrue();
    }

    [Fact]
    public void FoldingRaisesWhatTheViewBindsTo()
    {
        var view = new EntryViewModel(Entry(Long(), Long()));
        var raised = 0;
        view.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(EntryViewModel.IsCollapsed) or nameof(EntryViewModel.DisclosureLabel))
            {
                raised++;
            }
        };

        view.ToggleExpandCommand.Execute(null);

        raised.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public void ATableKeepsEveryColumnItWasGiven()
    {
        var blocks = MarkdownBlockParser.Parse(
            "| Task type | Digest(s) | Binding section |\n"
            + "|---|---|---|\n"
            + "| Web UI build or review | distinctive-design-system-research-digest.md | Section 2, Distilled Rules |\n");

        var table = blocks.OfType<MdTable>().Should().ContainSingle().Subject;

        table.HasHeader.Should().BeTrue();
        table.Rows.Should().HaveCount(2);
        table.Rows.Should().OnlyContain(r => r.Count == 3);
        table.Rows[1][1].Should().Be("distinctive-design-system-research-digest.md");
    }

    [Fact]
    public void OnlyOneEntryStandsOpenAtATime()
    {
        var workspace = Workspace();
        var first = new EntryViewModel(Entry(Long(), Long()));
        var second = new EntryViewModel(Entry(Long(), Long()));
        workspace.Entries.Add(first);
        workspace.Entries.Add(second);

        first.ToggleExpandCommand.Execute(null);
        workspace.ExpandedEntry.Should().BeSameAs(first);
        workspace.HasExpandedEntry.Should().BeTrue();

        second.ToggleExpandCommand.Execute(null);

        first.IsExpanded.Should().BeFalse("opening the second is also closing the first");
        workspace.ExpandedEntry.Should().BeSameAs(second);
    }

    [Fact]
    public void TheBarClosesWhicheverIsOpen()
    {
        var workspace = Workspace();
        var entry = new EntryViewModel(Entry(Long(), Long()));
        workspace.Entries.Add(entry);
        entry.ToggleExpandCommand.Execute(null);

        workspace.CollapseExpandedEntryCommand.Execute(null);

        entry.IsExpanded.Should().BeFalse();
        workspace.HasExpandedEntry.Should().BeFalse("and the bar stands down with it");
    }

    [Fact]
    public void TheBarIsAbsentUntilSomethingIsOpen()
    {
        var workspace = Workspace();
        workspace.Entries.Add(new EntryViewModel(Entry(Long(), Long())));

        workspace.HasExpandedEntry.Should().BeFalse();
    }

    [Fact]
    public void ATranslatedFileArrivesAsTheFileItCameFrom()
    {
        var view = new EntryViewModel(File("design-digests.md", 4096, Long()));

        view.IsFile.Should().BeTrue();
        view.FileName.Should().Be("design-digests.md");
        view.FileSizeLabel.Should().NotBeEmpty();
        view.HasFileSize.Should().BeTrue();
    }

    [Fact]
    public void AFilePreviewStartsClosedOnBothSides()
    {
        var view = new EntryViewModel(File("design-digests.md", 4096, Long()));

        view.IsSourcePreviewOpen.Should().BeFalse();
        view.IsResultPreviewOpen.Should().BeFalse();
        view.IsSourceBodyOpen.Should().BeFalse();
        view.IsResultBodyOpen.Should().BeFalse();
    }

    [Fact]
    public void AFileIsFoldableOnlyOnceAPreviewIsOpen()
    {
        var view = new EntryViewModel(File("note.txt", 12, "Short."));

        view.CanCollapse.Should().BeFalse("a shut file row is two chips, with no body for Show more to lengthen");

        view.ToggleSourcePreviewCommand.Execute(null);
        view.CanCollapse.Should().BeTrue();

        view.ToggleSourcePreviewCommand.Execute(null);
        view.CanCollapse.Should().BeFalse();
    }

    [Fact]
    public void ShuttingTheLastPreviewAlsoFoldsTheRow()
    {
        var view = new EntryViewModel(File("note.txt", 12, "Short."));

        view.ToggleSourcePreviewCommand.Execute(null);
        view.ToggleExpandCommand.Execute(null);
        view.IsExpanded.Should().BeTrue();

        view.ToggleSourcePreviewCommand.Execute(null);

        view.IsExpanded.Should().BeFalse("reopening the eye must not show Show less over a preview nobody expanded");
    }

    [Fact]
    public void AFileStillTranslatingShowsTheSkeletonRatherThanAnArtifact()
    {
        var view = new EntryViewModel(File("compass.md", 19_000, Long()));

        view.BeginRun();

        view.FileState.Should().Be(FileSlotState.Translating);
        view.ShowsFileSkeleton.Should().BeTrue("a document takes minutes and the row has to say so");
        view.ShowsFileProgressBar.Should().BeFalse("the engine reported no figure");
        view.ShowsFileDone.Should().BeFalse("there is nothing behind it yet");
        view.HasResultFileName.Should().BeFalse("the artifact has no name until the job finishes");
    }

    [Fact]
    public void TheArtifactArrivesWhenTheTranslationDoes()
    {
        var view = new EntryViewModel(File("compass.md", 19_000, Long()));
        view.BeginRun();

        view.Complete(Long());

        view.FileState.Should().Be(FileSlotState.Done);
        view.ShowsFileDone.Should().BeTrue();
        view.ShowsFileSkeleton.Should().BeFalse();
        view.ResultFileName.Should().Be("compass.md");
    }

    [Fact]
    public void AFileOffersTheViewSourceSwitchOnBothSides()
    {
        var markdown = File("compass.md", 19_000, "# Heading\n\nProse under it.");
        var view = new EntryViewModel(markdown);
        view.Complete("# Nadpis\n\nText pod ním.");

        view.IsMarkdown.Should().BeTrue();
        view.HasFormatToggle.Should().BeTrue("the two readings are both there to switch between");
    }

    [Fact]
    public void AQueuedFileWaitsRatherThanLookingFinished()
    {
        var view = new EntryViewModel(File("second.md", 900, Long())) { IsQueued = true };

        view.FileState.Should().Be(FileSlotState.Queued);
        view.ShowsFileQueued.Should().BeTrue();
        view.ShowsFileSkeleton.Should().BeFalse();
        view.ShowsFileDone.Should().BeFalse();
        view.ShowsFileFailed.Should().BeFalse();
    }

    [Fact]
    public void AFileThatCameBackEmptyReadsAsFailedRatherThanDone()
    {
        var view = new EntryViewModel(File("keys.txt", 1400, "KEY_ONE=1\nKEY_TWO=2"));

        view.BeginRun();
        view.Complete(string.Empty);

        view.FileState.Should().Be(FileSlotState.Failed);
        view.ShowsFileDone.Should().BeFalse("there is nothing behind it to open");
        view.ShowsFileFailed.Should().BeTrue();
        view.HasResultFileName.Should().BeFalse();
    }

    [Fact]
    public void EachFileSlotHasExactlyOneState()
    {
        var view = new EntryViewModel(File("one.md", 900, Long())) { IsQueued = true };
        Lit(view).Should().Be(1);

        view.IsQueued = false;
        view.BeginRun();
        Lit(view).Should().Be(1);

        view.Complete(Long());
        Lit(view).Should().Be(1);
    }

    private static int Lit(EntryViewModel view) =>
        new[] { view.ShowsFileQueued, view.ShowsFileTranslating, view.ShowsFileDone, view.ShowsFileFailed }
            .Count(x => x);

    [Fact]
    public void AnOrdinarySentenceHasNoFileSlot()
    {
        var view = new EntryViewModel(Entry(Long(), Long()));

        view.IsFile.Should().BeFalse();
        view.ShowsFileQueued.Should().BeFalse();
        view.ShowsFileDone.Should().BeFalse();
        view.IsSourceBodyOpen.Should().BeTrue("a message that is not a file has no preview to open");
        view.IsResultBodyOpen.Should().BeTrue();
    }

    private static Entry File(string name, long bytes, string source) => new()
    {
        Id = Guid.NewGuid(),
        ChatId = Guid.NewGuid(),
        Kind = EntryKind.File,
        Source = source,
        Result = source,
        CreatedAt = DateTimeOffset.Now,
        State = EntryState.Done,
        TargetLanguage = "Czech",
        FileName = name,
        FilePath = $@"C:\docs\{name}",
        FileSizeBytes = bytes,
    };

    private static ChatWorkspaceViewModel Workspace()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bt-fold-{Guid.NewGuid():N}.db");

        return new ChatWorkspaceViewModel(
            new ChatStore(new Database(new AppPaths(path))),
            new App.Services.ClockService(),
            _ => null);
    }

    private static string Long() =>
        string.Join("\n", Enumerable.Range(0, 40)
            .Select(i => $"Line {i} of a translated document that runs well past the fold."));

    private static Entry Entry(string source, string result) => new()
    {
        Id = Guid.NewGuid(),
        ChatId = Guid.NewGuid(),
        Kind = EntryKind.Sentence,
        Source = source,
        Result = result,
        CreatedAt = DateTimeOffset.Now,
        State = EntryState.Done,
        TargetLanguage = "Czech",
    };
}
