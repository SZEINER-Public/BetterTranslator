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
using BetterTranslator.Runtime.Agents;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

public sealed class StopAndPauseTests : IAsyncLifetime
{
    private const string ThreeLines = "One line.\nTwo line.\nThree line.";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-stop", Guid.NewGuid().ToString("N"));
    private readonly List<string> _sent = [];

    private ChatWorkspaceViewModel _workspace = null!;
    private Func<string, CancellationToken, Task>? _onUnit;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        var database = new Database(new AppPaths(_root));
        await database.MigrateAsync(CancellationToken.None);

        _workspace = new ChatWorkspaceViewModel(new ChatStore(database), new ClockService(), _ => null)
        {
            Language = TargetLanguage.All.Single(l => l.Name == "Czech"),
        };

        _workspace.Translate = async (ask, token) =>
        {
            _sent.Add(ask.Text);

            if (_onUnit is { } hook)
            {
                await hook(ask.Text, token).ConfigureAwait(false);
            }

            return new TranslationOutcome("cz " + ask.Text, 1, TimeSpan.FromMilliseconds(1));
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

    private Task SendAsync(string text)
    {
        _workspace.Draft = text;
        return _workspace.SendCommand.ExecuteAsync(null);
    }

    private EntryViewModel Latest => _workspace.Entries.Last();

    [Fact]
    public async Task StoppingCancelsTheRuntimeCallAndKeepsWhatCameBack()
    {
        var reached = new TaskCompletionSource();

        _onUnit = async (text, token) =>
        {
            if (text != "Two line.")
            {
                return;
            }

            reached.TrySetResult();

            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        };

        var sending = SendAsync(ThreeLines);

        await reached.Task;

        var entry = Latest;
        entry.IsRunning.Should().BeTrue();
        entry.CanStop.Should().BeTrue();

        _workspace.StopEntryCommand.Execute(entry);

        await sending;

        _sent.Should().Equal(["One line.", "Two line."]);

        entry.WasStopped.Should().BeTrue();
        entry.Result.Should().Contain("cz One line.", "what came back is kept");
        entry.Result.Should().Contain("Three line.", "and the units never sent keep their source");
        entry.Note.Should().Be(entry.StoppedLabel);
        entry.HasResultText.Should().BeTrue("partial output is never discarded");
    }

    [Fact]
    public async Task AFinishedTranslationOffersNoStopAndIsToldTheRunEnded()
    {
        string? id = null;
        var announced = 0;

        _onUnit = (text, _) =>
        {
            if (text == "One line.")
            {
                id = Latest.JobId;

                Latest.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(EntryViewModel.CanStop))
                    {
                        announced++;
                    }
                };
            }

            return Task.CompletedTask;
        };

        await SendAsync(ThreeLines);

        var entry = Latest;

        entry.Phase.Should().Be(TranslationPhase.Complete);
        entry.IsRunning.Should().BeFalse("the run is over");
        entry.CanStop.Should().BeFalse("a finished translation has nothing left to stop");
        entry.CanPause.Should().BeFalse();

        announced.Should().BeGreaterThan(0, "the row is told the run ended rather than left to poll for it");
        JobRegistry.Shared.Status(id!).Should().BeNull();
    }

    [Fact]
    public async Task AStoppedEntryIsStoredAsStoppedRatherThanDone()
    {
        _onUnit = async (text, token) =>
        {
            if (text == "Two line.")
            {
                _workspace.StopGenerationCommand.Execute(null);
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
            }
        };

        await SendAsync(ThreeLines);

        Latest.Entry.State.Should().Be(EntryState.Stopped);
        Latest.Entry.Result.Should().Contain("cz One line.");
    }

    [Fact]
    public async Task StopReachesTheJobThroughTheSamePathTheAgentToolUses()
    {
        string? id = null;
        var reached = new TaskCompletionSource();

        _onUnit = async (text, token) =>
        {
            if (text != "Two line.")
            {
                return;
            }

            id = Latest.JobId;
            reached.TrySetResult();

            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        };

        var sending = SendAsync(ThreeLines);

        await reached.Task;

        id.Should().NotBeNull("a chat generation is a job like any other");
        JobRegistry.Shared.Status(id!).Should().NotBeNull();

        JobRegistry.Shared.Cancel(id!).Should().BeTrue("this is what job_cancel calls");

        await sending;

        Latest.WasStopped.Should().BeTrue();
    }

    [Fact]
    public async Task PauseHoldsAtTheChunkBoundaryAndResumeContinuesFromTheFirstUnfinishedChunk()
    {
        var paused = new TaskCompletionSource();

        _onUnit = (text, _) =>
        {
            if (text == "One line.")
            {
                _workspace.PauseEntryCommand.Execute(Latest);
                paused.TrySetResult();
            }

            return Task.CompletedTask;
        };

        var sending = SendAsync(ThreeLines);

        await paused.Task;

        for (var i = 0; i < 8; i++)
        {
            await Task.Yield();
        }

        _sent.Should().ContainSingle().Which.Should().Be("One line.");

        var entry = Latest;
        entry.IsPaused.Should().BeTrue();
        entry.CanResume.Should().BeTrue();
        entry.CanPause.Should().BeFalse();
        sending.IsCompleted.Should().BeFalse("the run is held, not abandoned");

        _workspace.ResumeEntryCommand.Execute(entry);

        await sending;

        _sent.Should().Equal(["One line.", "Two line.", "Three line."]);
        entry.WasStopped.Should().BeFalse();
        entry.Result.Should().Be("cz One line.\ncz Two line.\ncz Three line.");
    }

    [Fact]
    public async Task APausedRunIsStillPausedAfterLeavingTheConversationAndComingBack()
    {
        var paused = new TaskCompletionSource();

        _onUnit = (text, _) =>
        {
            if (text == "One line.")
            {
                _workspace.PauseEntryCommand.Execute(Latest);
                paused.TrySetResult();
            }

            return Task.CompletedTask;
        };

        var sending = SendAsync(ThreeLines);

        await paused.Task;

        var opened = _workspace.SelectedRow;

        _workspace.NewChatCommand.Execute(null);
        await _workspace.EntriesLoaded;

        _workspace.Entries.Should().BeEmpty();

        _workspace.SelectedRow = opened;
        await _workspace.EntriesLoaded;

        var entry = _workspace.Entries.Last();

        entry.IsPaused.Should().BeTrue("the job outlives the row that shows it");
        entry.CanResume.Should().BeTrue();

        _workspace.ResumeEntryCommand.Execute(entry);

        await sending;

        _sent.Should().Equal(["One line.", "Two line.", "Three line."]);
    }

    [Fact]
    public async Task PauseIsOfferedOnlyWhereTheWorkIsCutIntoChunks()
    {
        var reached = new TaskCompletionSource();

        _onUnit = async (_, token) =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        };

        var sending = SendAsync("A single short line.");

        await reached.Task;

        var entry = Latest;

        entry.IsChunked.Should().BeFalse();
        entry.CanPause.Should().BeFalse("there is no next chunk to stop before");
        entry.CanStop.Should().BeTrue("stop is offered on every generation");

        _workspace.StopEntryCommand.Execute(entry);
        await sending;
    }

    [Fact]
    public async Task AStopLeavesNoLiveJobNoTemporaryFileAndAComposerThatSendsAgainAtOnce()
    {
        string? id = null;
        var reached = new TaskCompletionSource();

        _onUnit = async (text, token) =>
        {
            if (text != "Two line.")
            {
                return;
            }

            id = Latest.JobId;
            reached.TrySetResult();

            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        };

        var sending = SendAsync(ThreeLines);

        await reached.Task;

        _workspace.IsGenerating.Should().BeTrue();
        _workspace.CanSend.Should().BeFalse("the send control is a stop control while a run is out");

        _workspace.StopGenerationCommand.Execute(null);

        await sending;

        _workspace.IsGenerating.Should().BeFalse();
        _workspace.LiveJobIds.Should().BeEmpty("the slot the run held is released");
        JobRegistry.Shared.Status(id!).Should().BeNull("a finished job is not left in the registry");

        Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
        Directory.EnumerateFiles(_root, "*.part", SearchOption.AllDirectories).Should().BeEmpty();

        _onUnit = null;
        _sent.Clear();

        _workspace.Draft = "After the stop.";
        _workspace.CanSend.Should().BeTrue("a new send is accepted immediately");

        await _workspace.SendCommand.ExecuteAsync(null);

        _sent.Should().ContainSingle().Which.Should().Be("After the stop.");
        Latest.Result.Should().Be("cz After the stop.");
        Latest.WasStopped.Should().BeFalse();
    }

    [Fact]
    public async Task StoppingBeforeAnythingCameBackSaysSoAndKeepsNoHalfAnswer()
    {
        var reached = new TaskCompletionSource();

        _onUnit = async (_, token) =>
        {
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        };

        var sending = SendAsync(ThreeLines);

        await reached.Task;

        _workspace.StopEntryCommand.Execute(Latest);

        await sending;

        var entry = Latest;

        entry.HasResultText.Should().BeFalse("nothing came back, so nothing is shown as a translation");
        entry.HasFailed.Should().BeTrue();
        entry.Note.Should().Be("Stopped before anything came back.");
        entry.IsRunning.Should().BeFalse();
        entry.CanStop.Should().BeFalse("a row that is no longer running has nothing left to stop");
    }
}
