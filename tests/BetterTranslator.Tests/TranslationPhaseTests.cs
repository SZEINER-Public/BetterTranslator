using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Indexing.Retrieval;
using BetterTranslator.Runtime.Inference;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The result region renders the translation, or it renders a placeholder, or
/// it says it failed. It never renders the source.
///
/// It used to. A memory lookup wrote the source back as the entry's result so
/// its marks had something to sit on, and the region showed that for the whole
/// of the wait -- untranslated text under the target-language heading, stored
/// there too, so a reload showed it again. These hold that shut.
/// </summary>
public sealed class TranslationPhaseTests : IAsyncLifetime
{
    private const string Source = "the build failed on the second stage";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-phase", Guid.NewGuid().ToString("N"));
    private readonly List<CancellationToken> _tokens = [];

    private Database _database = null!;
    private ChatWorkspaceViewModel _workspace = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);

        _database = new Database(new AppPaths(_root));
        await _database.MigrateAsync(CancellationToken.None);

        _workspace = new ChatWorkspaceViewModel(new ChatStore(_database), new ClockService(), _ => null)
        {
            Language = TargetLanguage.All.Single(l => l.Name == "German"),

            // The lexical path needs no model, so a lookup answers on every send
            // in this fixture -- which is the condition the defect needed.
            LookupMemory = (phrase, _) => new MemoryLookup(
                [],
                [
                    new MemoryMatch
                    {
                        Term = phrase.Split(' ')[0],
                        Start = 0,
                        Confidence = 90,
                        Origin = "From term pairs",
                    }
                ],
                []),
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
            // The database file may still be held; the temp folder is disposable.
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task The_source_never_stands_in_for_the_translation_while_one_is_in_flight()
    {
        var released = new TaskCompletionSource<TranslationOutcome>();
        EntryViewModel? entry = null;

        _workspace.Translate = (_, _) => released.Task;

        var send = SendAsync();

        // Mid-flight: the send is out and nothing has come back.
        entry = _workspace.Entries.Single();
        entry.Phase.Should().Be(TranslationPhase.Pending);
        entry.Result.Should().BeEmpty();
        entry.HasResultText.Should().BeFalse("there is no translation to show yet");
        entry.ShowsPlainResult.Should().BeFalse();
        entry.ShowsMarkedResult.Should().BeFalse();

        released.SetResult(new TranslationOutcome("der Build ist fehlgeschlagen", 9, TimeSpan.FromSeconds(2)));
        await send;

        entry.Phase.Should().Be(TranslationPhase.Complete);
        entry.Result.Should().Be("der Build ist fehlgeschlagen");
    }

    [Fact]
    public async Task A_refusal_fails_the_entry_and_says_why_rather_than_keeping_the_source()
    {
        _workspace.Translate = (_, _) => Task.FromResult(TranslationOutcome.None(TimeSpan.FromSeconds(1)));
        _workspace.LastVerdict = () => "unchanged";

        await SendAsync();

        var entry = _workspace.Entries.Single();

        entry.Phase.Should().Be(TranslationPhase.Failed);
        entry.HasFailed.Should().BeTrue("a failed entry offers Retry rather than a toast");
        entry.Note.Should().Be("unchanged");
        entry.Result.Should().BeEmpty();
        entry.HasResultText.Should().BeFalse();

        // And the row agrees, so reopening the chat does not resurrect it as an
        // answer.
        var stored = await new ChatStore(_database).GetEntriesAsync(_workspace.Rows.Single().Id, CancellationToken.None);
        stored.Single().Result.Should().BeEmpty();
        stored.Single().State.Should().Be(EntryState.Failed);
    }

    [Fact]
    public async Task A_superseded_run_cannot_overwrite_a_newer_result()
    {
        var slow = new TaskCompletionSource<TranslationOutcome>();
        var fast = new TaskCompletionSource<TranslationOutcome>();
        var answers = new Queue<TaskCompletionSource<TranslationOutcome>>([slow, fast]);

        _workspace.Translate = (_, token) =>
        {
            _tokens.Add(token);
            return answers.Dequeue().Task;
        };

        var first = SendAsync();
        var entry = _workspace.Entries.Single();

        // Retry supersedes the send that is still out.
        var second = _workspace.RetryEntryCommand.ExecuteAsync(entry);

        _tokens.Should().HaveCount(2);
        _tokens[0].IsCancellationRequested.Should().BeTrue("the run it replaced has to be cancelled");

        fast.SetResult(new TranslationOutcome("the newer answer", 4, TimeSpan.FromMilliseconds(80)));
        await second;

        // The earlier request finishes anyway, as a slow one over HTTP would.
        slow.SetResult(new TranslationOutcome("the stale answer", 4, TimeSpan.FromSeconds(30)));
        await first;

        entry.Result.Should().Be("the newer answer");
        entry.Phase.Should().Be(TranslationPhase.Complete);
    }

    [Fact]
    public async Task A_row_stored_with_its_source_as_its_result_loads_as_no_translation()
    {
        // Exactly what the old send path wrote, and what is sitting in databases
        // it wrote to. Loading it must not put the source back on screen.
        var store = new ChatStore(_database);
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Name = "Poisoned",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        await store.AddChatAsync(chat, CancellationToken.None);
        await store.AddEntryAsync(
            new Entry
            {
                Id = Guid.NewGuid(),
                ChatId = chat.Id,
                Kind = EntryKind.Sentence,
                Source = Source,
                Result = Source,
                CreatedAt = DateTimeOffset.Now,
                State = EntryState.Done,
                TargetLanguage = "German",
            },
            CancellationToken.None);

        await _workspace.LoadAsync(CancellationToken.None);

        var entry = _workspace.Entries.Single();

        entry.Source.Should().Be(Source);
        entry.Result.Should().BeEmpty("a result identical to its source was never translated");
        entry.Phase.Should().Be(TranslationPhase.Idle);
        entry.HasResultText.Should().BeFalse();
    }

    [Fact]
    public async Task Marks_measured_against_the_source_are_not_carried_onto_the_translation()
    {
        // A lookup measures its offsets against the phrase it was handed. Once
        // the region holds a translation instead, those offsets point at
        // whatever characters happen to sit there.
        _workspace.Translate = (_, _) =>
            Task.FromResult(new TranslationOutcome("der Build ist fehlgeschlagen", 9, TimeSpan.FromSeconds(1)));

        await SendAsync();

        var entry = _workspace.Entries.Single();

        entry.Matches.Should().NotBeEmpty("the lookup still ran and the panel still logs it");
        entry.HasMatches.Should().BeFalse("none of them line up with the text that came back");
        entry.ShowsPlainResult.Should().BeTrue();
        entry.Segments.Should().ContainSingle().Which.Text.Should().Be("der Build ist fehlgeschlagen");
    }

    private async Task SendAsync()
    {
        _workspace.Draft = Source;
        await _workspace.SendCommand.ExecuteAsync(null);
    }
}
