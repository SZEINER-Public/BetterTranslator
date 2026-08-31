using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class RegenerationTests : IAsyncLifetime
{
    private const string Message = "The build finished.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-regen", Guid.NewGuid().ToString("N"));
    private readonly List<string> _sent = [];

    private ChatWorkspaceViewModel _workspace = null!;
    private ConfirmDialogViewModel? _dialog;

    private Func<string, CancellationToken, Task<TranslationOutcome>> _model =
        (text, _) => Task.FromResult(new TranslationOutcome("v1 " + text, 3, TimeSpan.FromMilliseconds(10)));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var database = new Database(new AppPaths(_root));
        await database.MigrateAsync(CancellationToken.None);

        _workspace = new ChatWorkspaceViewModel(
            new ChatStore(database),
            new ClockService(),
            dialog => _dialog = dialog)
        {
            Language = TargetLanguage.All.Single(l => l.Name == "Czech"),
        };

        _workspace.Translate = (ask, token) =>
        {
            _sent.Add(ask.Text);
            return _model(ask.Text, token);
        };
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    private async Task<EntryViewModel> SendAsync(string text)
    {
        _workspace.Draft = text;
        await _workspace.SendCommand.ExecuteAsync(null);

        return _workspace.Entries.Last();
    }

    private void UseModel(string marker) =>
        _model = (text, _) => Task.FromResult(new TranslationOutcome(marker + " " + text, 3, TimeSpan.FromMilliseconds(10)));

    private static Entry FileEntry(string name, string source) => new()
    {
        Id = Guid.NewGuid(),
        ChatId = Guid.NewGuid(),
        Kind = EntryKind.File,
        Source = source,
        Result = string.Empty,
        CreatedAt = DateTimeOffset.Now,
        State = EntryState.Pending,
        TargetLanguage = "Czech",
        FileName = name,
        FilePath = Path.Combine(@"C:\docs", name),
        FileSizeBytes = 900,
    };

    [Fact]
    public async Task ARegenerationSendsTheOriginalSourceAgainAndNeverItsTranslation()
    {
        var entry = await SendAsync(Message);

        entry.Result.Should().Be("v1 " + Message);

        _sent.Clear();
        UseModel("v2");

        await _workspace.RegenerateEntryCommand.ExecuteAsync(entry);

        _sent.Should().ContainSingle().Which.Should().Be(Message, "the source is re-submitted byte for byte");
        entry.Source.Should().Be(Message, "a regeneration never rewrites the source");
        entry.Entry.Source.Should().Be(Message);
    }

    [Fact]
    public async Task ARegenerationRunsOnTheModelSelectedNowAndKeepsThePlaceInTheConversation()
    {
        var first = await SendAsync("One.");
        var target = await SendAsync(Message);
        var last = await SendAsync("Three.");

        UseModel("v2");

        await _workspace.RegenerateEntryCommand.ExecuteAsync(target);

        target.Result.Should().Be("v2 " + Message, "the run used whatever model is selected now");
        first.Result.Should().Be("v1 One.", "nothing else was touched");
        last.Result.Should().Be("v1 Three.");

        _workspace.Entries.Should().HaveCount(3);
        _workspace.Entries[1].Should().BeSameAs(target, "the message keeps its position");
    }

    [Fact]
    public async Task TheReplacedTranslationStaysRetrievable()
    {
        var entry = await SendAsync(Message);

        UseModel("v2");
        await _workspace.RegenerateEntryCommand.ExecuteAsync(entry);

        entry.Result.Should().Be("v2 " + Message);
        entry.PreviousResult.Should().Be("v1 " + Message);
        entry.HasPreviousResult.Should().BeTrue();
        entry.CopyPreviousResultCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task AFailedRegenerationLeavesThePreviousTranslationVisible()
    {
        var entry = await SendAsync(Message);
        var kept = entry.Result;

        _model = (_, _) => Task.FromResult(TranslationOutcome.None(TimeSpan.Zero));

        await _workspace.RegenerateEntryCommand.ExecuteAsync(entry);

        entry.Result.Should().Be(kept);
        entry.Phase.Should().Be(TranslationPhase.Complete);
        entry.HasResultText.Should().BeTrue();
        entry.HasFailed.Should().BeFalse();
        entry.Note.Should().Be(EntryViewModel.RegenerationFailedNote);
        entry.IsRegenerating.Should().BeFalse();
        entry.ShowsSkeleton.Should().BeFalse();
    }

    [Fact]
    public async Task AFailedRegenerationLeavesTheStoredRowAlone()
    {
        var entry = await SendAsync(Message);
        var kept = entry.Entry.Result;

        _model = (_, _) => Task.FromResult(TranslationOutcome.None(TimeSpan.Zero));

        await _workspace.RegenerateEntryCommand.ExecuteAsync(entry);

        entry.Entry.Result.Should().Be(kept);
        entry.Entry.State.Should().Be(EntryState.Done);
    }

    [Fact]
    public async Task ACancelledRegenerationLeavesThePreviousTranslationVisible()
    {
        var entry = await SendAsync(Message);
        var kept = entry.Result;

        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        _model = async (text, token) =>
        {
            reached.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);

            return new TranslationOutcome("v2 " + text, 3, TimeSpan.Zero);
        };

        var running = _workspace.RegenerateEntryCommand.ExecuteAsync(entry);

        await reached.Task;

        entry.IsRegenerating.Should().BeTrue();
        entry.Result.Should().BeEmpty("the result region is cleared for the placeholder while the run is out");

        _workspace.CancelRegenerationCommand.Execute(entry);

        entry.Result.Should().Be(kept);
        entry.Phase.Should().Be(TranslationPhase.Complete);
        entry.Note.Should().Be(EntryViewModel.RegenerationCancelledNote);
        entry.IsRegenerating.Should().BeFalse();

        release.TrySetResult();
        await running;

        entry.Result.Should().Be(kept, "the answer the cancelled run was carrying never lands");
        entry.Entry.Result.Should().Be(kept);
    }

    [Fact]
    public async Task OneMessageCannotRegenerateTwiceAtOnce()
    {
        var entry = await SendAsync(Message);

        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var runs = 0;

        _model = async (text, token) =>
        {
            Interlocked.Increment(ref runs);
            reached.TrySetResult();
            await release.Task.WaitAsync(token).ConfigureAwait(false);

            return new TranslationOutcome("v2 " + text, 3, TimeSpan.Zero);
        };

        var first = _workspace.RegenerateEntryCommand.ExecuteAsync(entry);

        await reached.Task;

        entry.CanRegenerate.Should().BeFalse("the action is unavailable while its own run is out");

        await _workspace.RegenerateEntryCommand.ExecuteAsync(entry);

        runs.Should().Be(1, "the second press did not start a run");

        release.TrySetResult();
        await first;

        runs.Should().Be(1);
        entry.Result.Should().Be("v2 " + Message);
    }

    [Fact]
    public async Task TheNormalSendPathStaysUsableWhileARegenerationIsOut()
    {
        var entry = await SendAsync(Message);

        var reached = new TaskCompletionSource();
        var release = new TaskCompletionSource();

        _model = async (text, token) =>
        {
            if (text == Message)
            {
                reached.TrySetResult();
                await release.Task.WaitAsync(token).ConfigureAwait(false);
            }

            return new TranslationOutcome("v2 " + text, 3, TimeSpan.Zero);
        };

        var running = _workspace.RegenerateEntryCommand.ExecuteAsync(entry);
        await reached.Task;

        var sentMeanwhile = await SendAsync("Something else.");

        sentMeanwhile.Result.Should().Be("v2 Something else.");

        release.TrySetResult();
        await running;
    }

    [Fact]
    public async Task AFileMessageRegeneratesThroughTheFileTranslationPath()
    {
        var view = new EntryViewModel(FileEntry("notes.md", "one"));
        view.BeginRun();
        view.Complete("v1 one");

        _workspace.Entries.Add(view);

        UseModel("v2");
        _sent.Clear();

        await _workspace.RegenerateEntryCommand.ExecuteAsync(view);

        _sent.Should().ContainSingle().Which.Should().Be("one");
        view.Result.Should().Be("v2 one");
        view.PreviousResult.Should().Be("v1 one");
        view.FileState.Should().Be(FileSlotState.Done);
        view.ResultFileName.Should().Be("notes.md");
        view.CanExportResult.Should().BeTrue("the new translated file is exported through the same route");
    }

    [Fact]
    public async Task ATranslatedSelectionRegeneratesLikeAnyOtherResult()
    {
        var entry = await SendAsync("build");

        entry.Kind.Should().Be(EntryKind.Words, "a short selection is a words row");

        UseModel("v2");
        await _workspace.RegenerateEntryCommand.ExecuteAsync(entry);

        entry.Result.Should().Be("v2 build");
    }

    [Fact]
    public async Task AModelSwitchOffersRegenerationOnlyWhenSomethingWasTranslated()
    {
        _workspace.OfferRegeneration("TranslateGemma");
        _workspace.HasRegenerateOffer.Should().BeFalse("there is nothing to re-run yet");

        await SendAsync(Message);

        _workspace.OfferRegeneration("TranslateGemma");

        _workspace.HasRegenerateOffer.Should().BeTrue();
        _workspace.RegenerateOfferText.Should().Contain("TranslateGemma");

        _workspace.DismissRegenerateOfferCommand.Execute(null);

        _workspace.HasRegenerateOffer.Should().BeFalse();
    }

    [Fact]
    public async Task ABulkRegenerationNamesTheCountAndWaitsToBeConfirmed()
    {
        await SendAsync("One.");
        await SendAsync("Two.");
        await SendAsync("Three.");

        _workspace.OfferRegeneration("TranslateGemma");

        UseModel("v2");
        _sent.Clear();

        _workspace.ConfirmRegenerateAllCommand.Execute(null);

        _dialog.Should().NotBeNull();
        _dialog!.Title.Should().Be("Regenerate 3 messages?");
        _dialog.Body.Should().Contain("TranslateGemma");
        _sent.Should().BeEmpty("nothing runs before the confirmation is taken");

        _dialog.ConfirmCommand.Execute(null);
        await _workspace.RegenerationsCompleted;

        _sent.Should().Equal("One.", "Two.", "Three.");
        _workspace.Entries.Select(e => e.Result).Should().Equal("v2 One.", "v2 Two.", "v2 Three.");
        _workspace.HasRegenerateOffer.Should().BeFalse();
    }

    [Fact]
    public async Task ABulkRegenerationThatIsNotConfirmedRunsNothing()
    {
        await SendAsync("One.");
        await SendAsync("Two.");

        _workspace.OfferRegeneration("TranslateGemma");

        UseModel("v2");
        _sent.Clear();

        _workspace.ConfirmRegenerateAllCommand.Execute(null);
        _dialog!.CancelCommand.Execute(null);

        _sent.Should().BeEmpty();
        _workspace.Entries.Select(e => e.Result).Should().Equal("v1 One.", "v1 Two.");
    }

    [Fact]
    public async Task ABulkRegenerationRunsOneMessageAtATime()
    {
        await SendAsync("One.");
        await SendAsync("Two.");

        _workspace.OfferRegeneration("TranslateGemma");

        var inFlight = 0;
        var highest = 0;

        _model = async (text, _) =>
        {
            var now = Interlocked.Increment(ref inFlight);
            highest = Math.Max(highest, now);

            await Task.Yield();

            Interlocked.Decrement(ref inFlight);

            return new TranslationOutcome("v2 " + text, 3, TimeSpan.Zero);
        };

        _workspace.ConfirmRegenerateAllCommand.Execute(null);
        _dialog!.ConfirmCommand.Execute(null);

        await _workspace.RegenerationsCompleted;

        highest.Should().Be(1, "a bulk run is sequential");
    }
}
