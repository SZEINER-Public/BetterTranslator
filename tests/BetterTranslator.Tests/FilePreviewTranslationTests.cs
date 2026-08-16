using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// Translating the previewed file.
///
/// The version dropdown selects what to show and starts nothing; running the
/// document is its own action, because it costs a model call per paragraph and a
/// picker that silently began one would be indistinguishable from a frozen
/// window.
/// </summary>
public sealed class FilePreviewTranslationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-preview-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private async Task<FilePreviewViewModel> OpenedAsync(string body = "# A heading long enough to be sent")
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "notes.md");
        await File.WriteAllTextAsync(path, body);

        var preview = new FilePreviewViewModel();
        await preview.OpenAsync(path, CancellationToken.None);

        return preview;
    }

    private static DocumentTranslation Result(string text, bool cancelled = false) => new()
    {
        Text = text,
        LinesTotal = 1,
        LinesTranslated = cancelled ? 0 : 1,
        WasCancelled = cancelled,
    };

    [Fact]
    public async Task ChoosingTheTranslatedVersionStartsNothing()
    {
        var asked = false;
        var preview = await OpenedAsync();

        preview.TranslateDocument = (_, _, _) =>
        {
            asked = true;
            return Task.FromResult(Result("cz"));
        };

        preview.ShowTranslatedVersionCommand.Execute(null);

        asked.Should().BeFalse();
        preview.ShowTranslated.Should().BeTrue();
        preview.HasTranslation.Should().BeFalse();
        preview.VersionNotice.Should().Be("Source shown until you run it");
    }

    [Fact]
    public async Task UntilARunFinishesTheSourceStaysOnScreen()
    {
        var preview = await OpenedAsync("# A heading long enough to be sent");

        preview.ShowTranslatedVersionCommand.Execute(null);

        preview.SourceText.Should().Be("# A heading long enough to be sent");
    }

    [Fact]
    public async Task AFinishedRunShowsTheTranslationAndSaysWhatItDid()
    {
        var preview = await OpenedAsync();

        preview.TranslateDocument = (_, _, _) => Task.FromResult(new DocumentTranslation
        {
            Text = "# Nadpis",
            LinesTotal = 3,
            LinesTranslated = 2,
            LinesKept = 1,
            ParagraphsJoined = 1,
        });

        await preview.TranslateAsync();

        preview.HasTranslation.Should().BeTrue();
        preview.ShowTranslated.Should().BeTrue();
        preview.SourceText.Should().Be("# Nadpis");
        preview.ShowVersionNotice.Should().BeFalse();

        // A document that came back with a third of its lines in English and no
        // explanation is the failure this reports against.
        preview.Summary.Should().Contain("2 of 3 lines translated")
            .And.Contain("1 kept their source")
            .And.Contain("1 wrapped paragraph joined");
    }

    [Fact]
    public async Task AProgressReportIsOfferedAndTheSummaryReplacesItAtTheEnd()
    {
        // The report is POSTED to the captured context -- under WPF the
        // dispatcher -- so when it lands is not observable from here. What is
        // observable, and what matters, is that a sink is handed over and that
        // the header ends up describing the finished run rather than the last
        // thing it was doing.
        var preview = await OpenedAsync();
        IProgress<DocumentProgress>? offered = null;

        preview.TranslateDocument = (_, progress, _) =>
        {
            offered = progress;
            return Task.FromResult(Result("cz"));
        };

        await preview.TranslateAsync();

        offered.Should().NotBeNull();
        preview.Activity.Should().BeNull();
        preview.RunNote.Should().StartWith("1 of 1 lines translated");
    }

    [Fact]
    public void ThePercentOnAReportIsTheRealShareOfTheWork()
    {
        new DocumentProgress { Activity = "x", UnitsDone = 1, UnitsTotal = 4 }.Percent.Should().Be(25);
        new DocumentProgress { Activity = "x", UnitsDone = 0, UnitsTotal = 0 }.Percent.Should().Be(0);
        new DocumentProgress { Activity = "x", UnitsDone = 9, UnitsTotal = 4 }.Percent.Should().Be(100);
    }

    [Fact]
    public async Task ALateReportDoesNotOverwriteTheSummary()
    {
        // Reports are posted, so one can still be waiting when the run ends.
        // Applied after that, it would leave the header describing work that has
        // already finished.
        var preview = await OpenedAsync();
        IProgress<DocumentProgress>? captured = null;

        preview.TranslateDocument = (_, progress, _) =>
        {
            captured = progress;
            return Task.FromResult(Result("cz"));
        };

        await preview.TranslateAsync();

        var summary = preview.Summary;
        captured!.Report(new DocumentProgress { Activity = "Pass 1 - stale", UnitsDone = 1, UnitsTotal = 4 });

        preview.Summary.Should().Be(summary);
        preview.RunNote.Should().Be(summary);
    }

    [Fact]
    public async Task ACancelledRunIsNotPresentedAsTheFilesTranslation()
    {
        // A stopped run still returns a whole document, but it is a partial
        // translation.
        var preview = await OpenedAsync();

        preview.TranslateDocument = (_, _, _) => Task.FromResult(Result("half done", cancelled: true));

        await preview.TranslateAsync();

        preview.HasTranslation.Should().BeFalse();
        preview.ShowTranslated.Should().BeFalse();
        preview.TranslatedText.Should().BeNull();
        preview.Summary.Should().Be("Stopped. Nothing was changed.");
    }

    [Fact]
    public async Task AThrownCancelIsTheSameOutcomeAsAReportedOne()
    {
        var preview = await OpenedAsync();

        preview.TranslateDocument = (_, _, token) => Task.FromCanceled<DocumentTranslation>(
            new CancellationToken(canceled: true));

        await preview.TranslateAsync();

        preview.HasTranslation.Should().BeFalse();
        preview.IsTranslating.Should().BeFalse("the run is over either way");
        preview.Summary.Should().Be("Stopped. Nothing was changed.");
    }

    [Fact]
    public async Task OpeningAnotherFileDropsTheOneBefore()
    {
        // A run belonging to the file being closed must not write its answer over
        // the one being opened.
        var preview = await OpenedAsync();

        preview.TranslateDocument = (_, _, _) => Task.FromResult(Result("# Nadpis"));
        await preview.TranslateAsync();

        preview.HasTranslation.Should().BeTrue();

        var second = Path.Combine(_root, "other.md");
        await File.WriteAllTextAsync(second, "# Another heading long enough to send");
        await preview.OpenAsync(second, CancellationToken.None);

        preview.HasTranslation.Should().BeFalse();
        preview.TranslatedText.Should().BeNull();
        preview.Summary.Should().BeNull();
        preview.SourceText.Should().Be("# Another heading long enough to send");
    }

    [Fact]
    public async Task WithNoRuntimeTheActionIsSimplyUnavailable()
    {
        var preview = await OpenedAsync();

        preview.CanTranslate.Should().BeFalse("no runtime is wired");

        await preview.TranslateAsync();

        preview.IsTranslating.Should().BeFalse();
        preview.HasTranslation.Should().BeFalse();
    }
}
