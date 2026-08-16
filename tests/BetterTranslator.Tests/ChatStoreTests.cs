using System.IO;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using BetterTranslator.Indexing.Retrieval;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The entry round trip, and the language the result is headed with. The
/// heading names a language, so the entry has to carry the one chosen when it
/// was sent rather than whatever the composer is set to when it is read.
/// </summary>
public sealed class ChatStoreTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-chats", Guid.NewGuid().ToString("N"));
    private Database _database = null!;
    private ChatStore _store = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = new Database(new AppPaths(_root));
        await _database.MigrateAsync(CancellationToken.None);
        _store = new ChatStore(_database);
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Entry_keeps_the_language_it_was_sent_with()
    {
        var chat = await AddChatAsync();

        await _store.AddEntryAsync(
            NewEntry(chat, "settings", "nastaveni", "Czech"),
            CancellationToken.None);

        var read = await _store.GetEntriesAsync(chat, CancellationToken.None);

        read.Should().ContainSingle();
        read[0].TargetLanguage.Should().Be("Czech");
        read[0].Source.Should().Be("settings");
        read[0].Result.Should().Be("nastaveni");
    }

    /// <summary>
    /// Two entries in one chat can hold two languages, which is the whole point
    /// of storing it per entry: changing the composer must not relabel what has
    /// already been translated.
    /// </summary>
    [Fact]
    public async Task Entries_in_one_chat_can_hold_different_languages()
    {
        var chat = await AddChatAsync();

        await _store.AddEntryAsync(NewEntry(chat, "one", "jedna", "Czech"), CancellationToken.None);
        await _store.AddEntryAsync(NewEntry(chat, "two", "zwei", "German"), CancellationToken.None);

        var read = await _store.GetEntriesAsync(chat, CancellationToken.None);

        read.Select(e => e.TargetLanguage).Should().BeEquivalentTo(["Czech", "German"]);
    }

    /// <summary>
    /// The column arrived in migration 2, so anything written before it has no
    /// language. It reads back empty rather than throwing, which is what lets
    /// the view head it neutrally instead of naming a language nothing recorded.
    /// </summary>
    [Fact]
    public async Task Entry_written_without_a_language_reads_back_empty()
    {
        var chat = await AddChatAsync();

        await _store.AddEntryAsync(NewEntry(chat, "legacy", "legacy", target: ""), CancellationToken.None);

        var read = await _store.GetEntriesAsync(chat, CancellationToken.None);

        read[0].TargetLanguage.Should().BeEmpty();
    }

    /// <summary>
    /// What a sent entry shows has to be what the database keeps. The result
    /// used to be written onto the view model after the row was already
    /// inserted, so the entry read back blank on the next launch and its
    /// translation column was empty under a heading naming a language.
    /// </summary>
    [Fact]
    public async Task Sending_stores_the_result_that_is_shown()
    {
        var workspace = new ChatWorkspaceViewModel(_store, new ClockService(), _ => null)
        {
            // Stands in for the lexical lookup, which needs no model. It used to
            // be what echoed the source into the result: a lookup marks terms
            // and translates nothing, so it has to leave the result alone.
            LookupMemory = (_, _) => new MemoryLookup([], [], []),
            Language = TargetLanguage.All.Single(l => l.Name == "German"),
            Draft = "workspace sidebar",
        };

        await workspace.SendCommand.ExecuteAsync(null);

        var shown = workspace.Entries.Single();
        var chatId = workspace.Rows.Single().Id;

        var stored = await _store.GetEntriesAsync(chatId, CancellationToken.None);

        stored.Should().ContainSingle();
        stored[0].Result.Should().Be(shown.Result);

        // No runtime is wired up, so nothing translated it. The row carries no
        // result at all rather than its own source -- in the database as on
        // screen, because a stored source reappears as an answer on reload.
        shown.Source.Should().Be("workspace sidebar");
        shown.Result.Should().BeEmpty();
        stored[0].TargetLanguage.Should().Be("German");
    }

    private async Task<Guid> AddChatAsync()
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Name = "Chat",
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        await _store.AddChatAsync(chat, CancellationToken.None);
        return chat.Id;
    }

    private static Entry NewEntry(Guid chatId, string source, string result, string target) =>
        new()
        {
            Id = Guid.NewGuid(),
            ChatId = chatId,
            Kind = EntryKind.Sentence,
            Source = source,
            Result = result,
            CreatedAt = DateTimeOffset.Now,
            State = EntryState.Done,
            TargetLanguage = target,
        };
}
