using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// One message out of a conversation.
///
/// There was no way to take a single row back: the only removal anywhere was
/// Delete on a whole chat, so a send that should not have happened could only be
/// undone by destroying everything around it or by editing the database.
/// </summary>
public sealed class EntryDeleteTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-entry-delete", Guid.NewGuid().ToString("N"));

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

    [Fact]
    public async Task The_store_removes_one_row_and_leaves_the_rest()
    {
        var store = Store();
        var chat = await ChatAsync(store).ConfigureAwait(true);

        var kept = Entry(chat.Id, "The build is green.");
        var doomed = Entry(chat.Id, "The cache was cold.");

        await store.AddEntryAsync(kept, CancellationToken.None).ConfigureAwait(true);
        await store.AddEntryAsync(doomed, CancellationToken.None).ConfigureAwait(true);

        await store.DeleteEntryAsync(doomed.Id, CancellationToken.None).ConfigureAwait(true);

        var left = await store.GetEntriesAsync(chat.Id, CancellationToken.None).ConfigureAwait(true);

        left.Select(entry => entry.Source).Should().Equal("The build is green.");

        var chats = await store.GetChatsAsync(CancellationToken.None).ConfigureAwait(true);

        chats.Should().ContainSingle(row => row.Id == chat.Id, "a message is not its conversation");
    }

    [Fact]
    public void Deleting_a_message_asks_first()
    {
        ConfirmDialogViewModel? shown = null;

        var workspace = new ChatWorkspaceViewModel(
            Store(),
            new ClockService(),
            dialog =>
            {
                shown = dialog;
                return dialog;
            });

        var entry = new EntryViewModel(Entry(Guid.NewGuid(), "The cache was cold."));

        workspace.Entries.Add(entry);
        workspace.ConfirmDeleteEntryCommand.Execute(entry);

        shown.Should().NotBeNull("nothing that cannot be undone happens on one click");
        shown!.Title.Should().Be("Delete this message?");
        shown.Body.Should().Contain("cannot be undone");
        shown.ConfirmLabel.Should().Be("Delete");

        workspace.Entries.Should().Contain(entry, "the prompt is still open");
    }

    [Fact]
    public async Task Confirming_takes_the_row_out_of_the_chat_and_out_of_storage()
    {
        var store = Store();
        var chat = await ChatAsync(store).ConfigureAwait(true);

        var kept = Entry(chat.Id, "The build is green.");
        var doomed = Entry(chat.Id, "The cache was cold.");

        doomed.GeneratedTokens = 40;
        doomed.DurationMs = 1000;

        await store.AddEntryAsync(kept, CancellationToken.None).ConfigureAwait(true);
        await store.AddEntryAsync(doomed, CancellationToken.None).ConfigureAwait(true);

        ConfirmDialogViewModel? shown = null;

        var workspace = new ChatWorkspaceViewModel(
            store,
            new ClockService(),
            dialog =>
            {
                shown = dialog;
                return dialog;
            });

        var view = new EntryViewModel(doomed) { GeneratedTokens = 40, DurationMs = 1000 };

        workspace.Entries.Add(new EntryViewModel(kept));
        workspace.Entries.Add(view);

        workspace.ChatTokenTotal.Should().Be(40);

        workspace.ConfirmDeleteEntryCommand.Execute(view);
        shown!.ConfirmCommand.Execute(null);

        // The command hands off to a task; the store call is the only await in it.
        await WaitUntil(() => workspace.Entries.Count == 1).ConfigureAwait(true);

        workspace.Entries.Should().NotContain(view);
        workspace.ChatTokenTotal.Should().Be(0, "the conversation's total is the sum of its rows");

        var left = await store.GetEntriesAsync(chat.Id, CancellationToken.None).ConfigureAwait(true);

        left.Select(entry => entry.Source).Should().Equal("The build is green.");
    }

    [Fact]
    public async Task A_run_still_out_for_the_row_cannot_land_on_it_afterwards()
    {
        var store = Store();
        var chat = await ChatAsync(store).ConfigureAwait(true);
        var doomed = Entry(chat.Id, "The cache was cold.");

        await store.AddEntryAsync(doomed, CancellationToken.None).ConfigureAwait(true);

        ConfirmDialogViewModel? shown = null;

        var workspace = new ChatWorkspaceViewModel(
            store,
            new ClockService(),
            dialog =>
            {
                shown = dialog;
                return dialog;
            });

        var view = new EntryViewModel(doomed);

        workspace.Entries.Add(view);

        var (sequence, token) = view.BeginRun();

        workspace.ConfirmDeleteEntryCommand.Execute(view);
        shown!.ConfirmCommand.Execute(null);

        await WaitUntil(() => workspace.Entries.Count == 0).ConfigureAwait(true);

        token.IsCancellationRequested.Should().BeTrue("the work for a row that has gone is not wanted");
        view.Owns(sequence).Should().BeFalse("an answer already on its way back cannot claim it");
    }

    [Fact]
    public void Copy_translation_is_offered_only_once_something_came_back()
    {
        var view = new EntryViewModel(Entry(Guid.NewGuid(), "The cache was cold."));

        view.CopySourceCommand.CanExecute(null).Should().BeTrue("the source is what was sent");
        view.CopyResultCommand.CanExecute(null).Should().BeFalse("nothing has come back yet");

        var told = 0;

        view.CopyResultCommand.CanExecuteChanged += (_, _) => told++;

        view.Complete("Mezipaměť byla chladná.");

        view.CopyResultCommand.CanExecute(null).Should().BeTrue();
        told.Should().BeGreaterThan(
            0,
            "a menu item greys itself off CanExecuteChanged, so the answer landing has to say so");
    }

    private ChatStore Store()
    {
        Directory.CreateDirectory(_root);

        var database = new Database(new AppPaths(_root));

        database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        return new ChatStore(database);
    }

    private static async Task<Chat> ChatAsync(ChatStore store)
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Name = "A chat",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        await store.AddChatAsync(chat, CancellationToken.None).ConfigureAwait(true);

        return chat;
    }

    private static Entry Entry(Guid chatId, string source) => new()
    {
        Id = Guid.NewGuid(),
        ChatId = chatId,
        Kind = EntryKind.Sentence,
        Source = source,
        Result = string.Empty,
        CreatedAt = DateTimeOffset.Now,
        State = EntryState.Pending,
        TargetLanguage = "Czech",
    };

    private static async Task WaitUntil(Func<bool> settled)
    {
        for (var attempt = 0; attempt < 200 && !settled(); attempt++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }

        settled().Should().BeTrue("the delete should have completed");
    }
}
