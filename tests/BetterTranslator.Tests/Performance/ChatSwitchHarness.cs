using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using BetterTranslator.App.Services;
using BetterTranslator.App.ViewModels;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;

namespace BetterTranslator.Tests.Performance;

public sealed record ScenarioResult(
    string Scenario,
    double FirstContentMs,
    double FullyLoadedMs,
    int ViewModelsMaterialized,
    long AllocatedBytes,
    int Gen0Collections)
{
    public override string ToString() => string.Join(
        "  ",
        $"{Scenario,-28}",
        $"first {FirstContentMs,7:F1} ms",
        $"loaded {FullyLoadedMs,7:F1} ms",
        $"viewmodels {ViewModelsMaterialized,5}",
        $"alloc {AllocatedBytes / 1024,7} KB",
        $"gen0 {Gen0Collections,3}");
}

/// <summary>
/// Drives the chat switch path with no window at all. The surface under test is
/// the workspace view model and the store beneath it, which is where the load,
/// the cancellation and the view model construction live; nothing here needs a
/// visual tree, so nothing here can take the interactive desktop.
/// </summary>
public sealed class ChatSwitchHarness : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "bt-perf",
        Guid.NewGuid().ToString("N"));

    private readonly Database _database;

    private readonly ChatStore _store;

    public ChatSwitchHarness()
    {
        Directory.CreateDirectory(_root);

        _database = new Database(new AppPaths(_root));
        _store = new ChatStore(_database);
    }

    public ChatStore Store => _store;

    public static string Diagnostic { get; private set; } = string.Empty;

    public IReadOnlyList<Guid> Corpus { get; private set; } = [];

    public IReadOnlyList<Guid> Burst { get; private set; } = [];

    public async Task SeedAsync()
    {
        await _database.MigrateAsync(CancellationToken.None);

        var chats = new List<Guid>();

        chats.Add(await SeedChatAsync("many short messages", 400, ShortBody));
        chats.Add(await SeedChatAsync("one long markdown", 1, _ => LongMarkdown()));
        chats.Add(await SeedChatAsync("one long json", 1, _ => LongJson()));
        chats.Add(await SeedChatAsync("file heavy", 120, ShortBody, EntryKind.File));

        var second = await SeedChatAsync("many short messages b", 400, ShortBody);
        var third = await SeedChatAsync("many short messages c", 400, ShortBody);

        // Three conversations of the same weight, because bursting into a chat
        // that holds one message proves nothing: the count stays low whether the
        // earlier loads were cancelled or ran to the end.
        //
        // Ordered so the LAST selection is the oldest of the three. Chats come
        // back newest first, so the newest is the row the workspace opens on and
        // the warm-up leaves it in the conversation cache. Ending the burst there
        // measured a cache hit and reported zero view models built, which looks
        // exactly like perfect cancellation and is not.
        Burst = [third, second, chats[0]];

        Corpus = chats;
    }

    private async Task<Guid> SeedChatAsync(
        string name,
        int messages,
        Func<int, string> body,
        EntryKind kind = EntryKind.Sentence)
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Name = name,
            CreatedAt = DateTimeOffset.Now,
            UpdatedAt = DateTimeOffset.Now,
        };

        await _store.AddChatAsync(chat, CancellationToken.None);

        for (var i = 0; i < messages; i++)
        {
            var source = body(i);

            await _store.AddEntryAsync(
                new Entry
                {
                    Id = Guid.NewGuid(),
                    ChatId = chat.Id,
                    Kind = kind,
                    Source = source,
                    Result = source + " (prelozeno)",
                    CreatedAt = DateTimeOffset.Now.AddMinutes(-messages + i),
                    State = EntryState.Done,
                    TargetLanguage = "cs",
                    GeneratedTokens = 40 + (i % 17),
                    DurationMs = 900 + (i % 250),
                    FileName = kind == EntryKind.File ? $"document-{i}.md" : null,
                    FileSizeBytes = kind == EntryKind.File ? 4096 + i : null,
                },
                CancellationToken.None);
        }

        return chat.Id;
    }

    private static string ShortBody(int i) =>
        $"Line {i}: the build is green and the runtime reported {i % 90} tokens per second.";

    private static string LongMarkdown()
    {
        var text = new StringBuilder();

        for (var i = 0; i < 900; i++)
        {
            text.Append("## Section ").Append(i).Append("\n\n")
                .Append("A paragraph with `code`, **bold** and a [link](https://example.com/")
                .Append(i)
                .Append(") that is long enough to wrap across the reading column more than once.\n\n");
        }

        return text.ToString();
    }

    private static string LongJson()
    {
        var text = new StringBuilder("{\n");

        for (var i = 0; i < 1200; i++)
        {
            text.Append("  \"key_").Append(i).Append("\": \"value ").Append(i).Append("\",\n");
        }

        return text.Append("  \"last\": \"value\"\n}").ToString();
    }

    public async Task<ScenarioResult> SingleSwitchAsync(string label, Guid chat)
    {
        var workspace = await WorkspaceAsync();

        return await MeasureAsync(label, workspace, [chat]);
    }

    public async Task<ScenarioResult> BurstAsync(string label, IReadOnlyList<Guid> chats)
    {
        var workspace = await WorkspaceAsync();

        return await MeasureAsync(label, workspace, chats);
    }

    private async Task<ChatWorkspaceViewModel> WorkspaceAsync()
    {
        var workspace = new ChatWorkspaceViewModel(_store, new ClockService(), _ => null);

        await workspace.LoadAsync(CancellationToken.None);

        workspace.SelectedRow = null;
        await workspace.EntriesLoaded;

        return workspace;
    }

    /// <summary>
    /// Selection is set for each chat in turn with no wait between them, which is
    /// what a burst of clicks does. First content is the first time the surface
    /// carries a row; fully loaded is when the last selection has settled.
    /// </summary>
    private static async Task<ScenarioResult> MeasureAsync(
        string label,
        ChatWorkspaceViewModel workspace,
        IReadOnlyList<Guid> chats)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var startedAt = EntryViewModel.ConstructedCount;
        var gen0 = GC.CollectionCount(0);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var clock = Stopwatch.StartNew();
        var painted = default(double?);

        // First content is taken from the collection itself rather than from the
        // poll below. The poll resolves at about 15 ms on this machine, which is
        // longer than the whole load: read from there, paging and no paging look
        // identical because both land inside one tick.
        void OnChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (painted is null && workspace.Entries.Count > 0)
            {
                painted = clock.Elapsed.TotalMilliseconds;
            }
        }

        workspace.Entries.CollectionChanged += OnChanged;

        try
        {
            foreach (var chat in chats)
            {
                workspace.SelectedRow = workspace.Rows.First(r => r.Id == chat);
            }

            // Awaiting the load the last selection started is what makes this a
            // measurement rather than a race with it. Polling on Entries.Count
            // alone returned the moment any publish landed, which for a burst was
            // the page of a load that a later selection then cancelled.
            while (clock.Elapsed < Timeout && !Settled(workspace, chats[^1]))
            {
                await Task.Delay(1);
            }

            await workspace.EntriesLoaded;
        }
        finally
        {
            workspace.Entries.CollectionChanged -= OnChanged;
        }

        Diagnostic = string.Join(
            " ",
            $"[{label}]",
            $"selected={workspace.SelectedRow?.Id.ToString()[..8]}",
            $"wanted={chats[^1].ToString()[..8]}",
            $"entries={workspace.Entries.Count}",
            $"loadedTask={workspace.EntriesLoaded.Status}");

        return new ScenarioResult(
            label,
            painted ?? clock.Elapsed.TotalMilliseconds,
            clock.Elapsed.TotalMilliseconds,
            (int)(EntryViewModel.ConstructedCount - startedAt),
            GC.GetTotalAllocatedBytes(precise: true) - allocated,
            GC.CollectionCount(0) - gen0);
    }

    private static bool Settled(ChatWorkspaceViewModel workspace, Guid last) =>
        workspace.SelectedRow?.Id == last && workspace.EntriesLoaded.IsCompleted && workspace.Entries.Count > 0;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

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
}

