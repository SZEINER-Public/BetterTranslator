using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using BetterTranslator.App.Controls;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BetterTranslator.Tests;

[Collection(EngineConfigCollection.Name)]
public sealed class FileMessageRowTests(ITestOutputHelper output) : IDisposable
{
    private const string PackRoot = "pack://application:,,,/BetterTranslator;component/Themes/";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-filerow", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        MotionService.Override = null;

        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void BothMessageKindsRenderThroughTheOneRowControl()
    {
        var kinds = new[] { FileEntry("compass.md", 4096, "# Heading"), TextEntry("The build is green.") };

        var built = StaRunner.Run(() =>
        {
            var style = (Style)Themes()[typeof(BilingualRow)];

            return kinds
                .Select(entry => new EntryViewModel(entry))
                .Select(view =>
                {
                    var row = new BilingualRow
                    {
                        Style = style,
                        SourceLabel = view.SourceHeading,
                        TargetLabel = view.TargetHeading,
                        SourceContent = new TextBlock(),
                        TargetContent = new TextBlock(),
                    };

                    row.ApplyTemplate();
                    row.Measure(new Size(800, 800));

                    output.WriteLine($"template: {row.Template is not null}, presenters: {Presenters(row)}");

                    return row.Template is not null && Presenters(row) == 3;
                })
                .ToArray();
        });

        built.Should().OnlyContain(ok => ok, "one control owns the grid, the labels, the arrow and the error surface");

        typeof(EntryViewModel).GetProperty("ShowsFileChip")
            .Should().BeNull("the standalone file card is gone rather than left as a second path");
        typeof(EntryViewModel).GetProperty("ShowsColumns")
            .Should().BeNull();
    }

    [Fact]
    public void TheTargetLabelComesFromTheRegistryEntryForTheSelectedTarget()
    {
        var view = new EntryViewModel(FileEntry("compass.md", 4096, "# Heading"));

        view.TargetHeading.Should().Be("CZECH");

        var unrecorded = FileEntry("compass.md", 4096, "# Heading", target: string.Empty);

        new EntryViewModel(unrecorded).TargetHeading
            .Should().Be(App.Resources.Strings.EntryResultLabel, "no language was recorded, so none is claimed");
    }

    [Fact]
    public void TheSourceLabelSwitchesBetweenTheChosenLanguageAndTheGenericLabel()
    {
        var workspace = Workspace();

        workspace.EntrySourceHeading.Should().Be(App.Resources.Strings.EntrySourceLabel, "nothing was chosen yet");

        workspace.ChooseSourceLanguageCommand.Execute(
            workspace.Languages.Single(l => l.Code == "cs"));

        workspace.EntrySourceHeading.Should().Be("CZECH");
        workspace.IsSourceAuto.Should().BeFalse();
    }

    [Fact]
    public void TheRightSlotRendersEachOfItsFourStates()
    {
        var queued = new EntryViewModel(FileEntry("a.md", 900, "x")) { IsQueued = true };
        queued.FileState.Should().Be(FileSlotState.Queued);
        queued.ShowsFileQueued.Should().BeTrue();
        queued.HasResultFileName.Should().BeFalse();

        var running = new EntryViewModel(FileEntry("b.md", 900, "x"));
        running.BeginRun();
        running.FileState.Should().Be(FileSlotState.Translating);
        running.ShowsFileSkeleton.Should().BeTrue("the engine reports no figure, so the skeleton stands in");
        running.ShowsFileProgressBar.Should().BeFalse();

        running.FileProgress = 0.4;
        running.ShowsFileProgressBar.Should().BeTrue("a reported figure is rendered rather than a skeleton");
        running.ShowsFileSkeleton.Should().BeFalse();

        var done = new EntryViewModel(FileEntry("c.md", 900, "x"));
        done.BeginRun();
        done.Complete("prelozeno");
        done.FileState.Should().Be(FileSlotState.Done);
        done.ResultFileName.Should().Be("c.md");

        var failed = new EntryViewModel(FileEntry("d.md", 900, "x"));
        failed.BeginRun();
        failed.Fail("the guard declined it");
        failed.FileState.Should().Be(FileSlotState.Failed);
        failed.HasFailed.Should().BeTrue("the row shows the same error line and Retry a text row shows");
        failed.Note.Should().Be("the guard declined it");
        failed.HasResultFileName.Should().BeFalse();
    }

    [Fact]
    public void RetryRequeuesOnlyItsOwnJob()
    {
        var workspace = Workspace();
        var first = new EntryViewModel(FileEntry("first.md", 900, "one"));
        var second = new EntryViewModel(FileEntry("second.md", 900, "two"));

        workspace.Entries.Add(first);
        workspace.Entries.Add(second);

        var asked = new List<string>();
        workspace.Translate = (ask, _) =>
        {
            asked.Add(ask.Text);
            return Task.FromResult(new Runtime.Inference.TranslationOutcome("ok", 1, TimeSpan.Zero));
        };

        workspace.RetryEntryCommand.Execute(second);

        output.WriteLine(string.Join(", ", asked));

        asked.Should().OnlyContain(text => text == "two", "the other entry was not re queued");
        first.Phase.Should().NotBe(TranslationPhase.Pending);
    }

    [Fact]
    public void ReducedMotionSetsTheFinalStateWithNoTimeline()
    {
        var plan = ExpandPlan.For(current: 0, target: 120, motionEnabled: false);

        plan.Animates.Should().BeFalse("reduced motion is a path, not a shorter animation");
        plan.To.Should().Be(120);

        var height = StaRunner.Run(() =>
        {
            MotionService.Override = false;

            var area = new ExpandArea { Content = new Border { Height = 120 } };
            area.Measure(new Size(400, 400));
            area.IsOpen = true;

            var settled = area.Height;
            MotionService.Override = null;

            return settled;
        });

        double.IsNaN(height).Should().BeTrue("the area settles to an automatic height at once");
    }

    [Fact]
    public void AnInterruptedExpandReversesFromWhereItIsRatherThanSnapping()
    {
        var opening = ExpandPlan.For(current: 0, target: 200, motionEnabled: true);
        opening.From.Should().Be(0);
        opening.To.Should().Be(200);

        var interrupted = ExpandPlan.For(current: 74, target: 0, motionEnabled: true);

        interrupted.Animates.Should().BeTrue();
        interrupted.From.Should().Be(74, "the reverse starts from the height on screen");
        interrupted.To.Should().Be(0);

        ExpandPlan.For(current: 200, target: 200, motionEnabled: true).Animates
            .Should().BeFalse("nothing is animated to where it already is");
    }

    /// <summary>
    /// A shut file clips to the peek and scrolls within it; an open one shows
    /// everything and hands the wheel back to the history.
    ///
    /// The peek had its scroller taken away, and this test pinned that. What it
    /// produced was a document cut off mid sentence with nothing to take hold
    /// of: no bar, no wheel, and the only way on was Show more at the top of the
    /// entry, nowhere near the cut. The bar is back for the capped state only,
    /// where there is something to scroll.
    /// </summary>
    [Theory]
    [InlineData(false, 360d, ScrollBarVisibility.Auto)]
    [InlineData(true, double.PositiveInfinity, ScrollBarVisibility.Disabled)]
    public void TheFilePreviewClipsShutAndOpensWhole(bool expanded, double cap, ScrollBarVisibility bar)
    {
        var measured = StaRunner.Run(() =>
        {
            var style = (Style)Themes()["FilePreviewScroll"];
            var entry = new EntryViewModel(FileEntry("a.md", 900, "x"));

            entry.ToggleSourcePreviewCommand.Execute(null);

            if (expanded)
            {
                entry.ToggleExpandCommand.Execute(null);
            }

            var scroll = new ScrollViewer { DataContext = entry, Style = style };

            // Two conditions on one trigger settle over more than a single pass,
            // and reading after one made this pick up the base cap about half the
            // time. Pumped until the queue is empty rather than a fixed number of
            // times, so the assertion is about the style and not about timing.
            foreach (var priority in new[]
                     {
                         System.Windows.Threading.DispatcherPriority.DataBind,
                         System.Windows.Threading.DispatcherPriority.Render,
                         System.Windows.Threading.DispatcherPriority.Loaded,
                         System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                     })
            {
                scroll.Dispatcher.Invoke(() => { }, priority);
            }

            scroll.Measure(new Size(400, double.PositiveInfinity));

            return (scroll.MaxHeight, scroll.VerticalScrollBarVisibility);
        });

        measured.MaxHeight.Should().Be(cap);
        measured.VerticalScrollBarVisibility.Should().Be(
            bar,
            "a capped peek is read through its own bar, and an uncapped one has nothing left to scroll");
    }

    /// <summary>
    /// One cap per column, and on a file row it belongs to the preview.
    ///
    /// Both caps are 360, and the column's counts from higher up: the label and
    /// the chip row sit above the preview inside it. So the fold cut the preview
    /// 49 pixels short of its own bottom, and its last lines and the lower end of
    /// its scrollbar sat under the clip with the row's white behind them. It read
    /// as the bar disappearing into a white band.
    /// </summary>
    [Theory]
    [InlineData(true, double.PositiveInfinity)]
    [InlineData(false, 360d)]
    public void TheColumnFoldLeavesAFileRowToItsPreview(bool file, double cap)
    {
        var measured = StaRunner.Run(() =>
        {
            var style = (Style)Themes()["EntryColumnFold"];

            var entry = file
                ? new EntryViewModel(FileEntry("a.md", 900, "x"))
                : new EntryViewModel(TextEntry(new string('x', 1200)));

            if (file)
            {
                entry.ToggleSourcePreviewCommand.Execute(null);
            }

            entry.IsCollapsed.Should().BeTrue("both rows are folded until Show more");

            var fold = new Border { DataContext = entry, Style = style };

            // Two conditions on one trigger settle over more than a single pass.
            foreach (var priority in new[]
                     {
                         System.Windows.Threading.DispatcherPriority.DataBind,
                         System.Windows.Threading.DispatcherPriority.Render,
                         System.Windows.Threading.DispatcherPriority.Loaded,
                         System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                     })
            {
                fold.Dispatcher.Invoke(() => { }, priority);
            }

            fold.Measure(new Size(400, double.PositiveInfinity));

            return fold.MaxHeight;
        });

        measured.Should().Be(cap);
    }

    [Fact]
    public void ExportingTheTranslationWritesItWhereTheReaderChose()
    {
        var workspace = Workspace();
        var view = new EntryViewModel(FileEntry("compass.md", 900, "# Heading"));

        view.BeginRun();
        view.Complete("# Nadpis");

        var target = Path.Combine(_root, "saved.md");
        workspace.PickSavePath = _ => target;

        view.CanExportResult.Should().BeTrue();
        view.ExportFileName.Should().Be("compass.cs.md", "the code between stem and extension is the registry's");

        workspace.ExportResultCommand.Execute(view);

        File.ReadAllText(target).Should().Be("# Nadpis");
    }

    [Fact]
    public void ExportIsNotOfferedBeforeThereIsSomethingToSave()
    {
        var workspace = Workspace();
        var view = new EntryViewModel(FileEntry("compass.md", 900, "# Heading"));

        view.BeginRun();

        view.CanExportResult.Should().BeFalse("nothing has come back yet");

        var asked = false;
        workspace.PickSavePath = _ =>
        {
            asked = true;
            return Path.Combine(_root, "never.md");
        };

        workspace.ExportResultCommand.Execute(view);

        asked.Should().BeFalse("an empty translation is never written out");
        view.CanExportSource.Should().BeTrue("the source is there from the start");
    }

    private static int Presenters(DependencyObject root)
    {
        var found = 0;

        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);

            if (child is ContentPresenter)
            {
                found++;
            }

            found += Presenters(child);
        }

        return found;
    }

    private static ResourceDictionary Themes()
    {
        var themes = new ResourceDictionary();

        foreach (var name in new[] { "Colors", "Typography", "Metrics", "Icons", "Controls" })
        {
            themes.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(PackRoot + name + ".xaml") });
        }

        return themes;
    }

    private ChatWorkspaceViewModel Workspace()
    {
        Directory.CreateDirectory(_root);

        var database = new Database(new AppPaths(_root));
        database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        return new ChatWorkspaceViewModel(new ChatStore(database), new ClockService(), _ => null);
    }

    private static Entry FileEntry(string name, long bytes, string source, string target = "Czech") => new()
    {
        Id = Guid.NewGuid(),
        ChatId = Guid.NewGuid(),
        Kind = EntryKind.File,
        Source = source,
        Result = string.Empty,
        CreatedAt = DateTimeOffset.Now,
        State = EntryState.Pending,
        TargetLanguage = target,
        FileName = name,
        FilePath = $@"C:\docs\{name}",
        FileSizeBytes = bytes,
    };

    private static Entry TextEntry(string source) => new()
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
