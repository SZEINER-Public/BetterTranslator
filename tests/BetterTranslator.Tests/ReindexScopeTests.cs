using System.IO;
using BetterTranslator.Core.Services;
using BetterTranslator.Indexing;
using BetterTranslator.Indexing.Index;
using BetterTranslator.Indexing.Readers;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// S12's reindex menu. Each scope resolves to a real queue, and the scope
/// travels inside the request rather than in shared state, so it cannot leak
/// into a later pass.
/// </summary>
public sealed class ReindexScopeTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-reindex", Guid.NewGuid().ToString("N"));
    private Database _database = null!;
    private IndexStore _store = null!;
    private IndexingService _service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = new Database(new AppPaths(_root));
        await _database.MigrateAsync(CancellationToken.None);

        _store = new IndexStore(_database);
        _service = new IndexingService(_store, new DocumentReaders());
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

    private string WriteFile(string name, string body)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, body);
        return path;
    }

    private async Task<IReadOnlyList<ReindexScope>> IndexThreeAndResolveAsync()
    {
        var files = new[]
        {
            WriteFile("a.txt", "alpha wording"),
            WriteFile("b.txt", "beta wording"),
            WriteFile("c.md", "# gamma\n\nwording"),
        };

        await _service.IndexAsync(IndexingRequest.ForFiles(files), null, CancellationToken.None);

        return await new ReindexScopeResolver(_store).ResolveAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TheMenuOffersItsFourScopes()
    {
        var scopes = await IndexThreeAndResolveAsync();

        scopes.Select(s => s.Title).Should().Equal(
            "Everything", "Only what changed", "Pick sources...", "Just the chats");
    }

    [Fact]
    public async Task EverythingCountsWhatIsActuallyIndexed()
    {
        var scopes = await IndexThreeAndResolveAsync();
        var everything = scopes.Single(s => s.Kind == ReindexScopeKind.Everything);

        everything.Count.Should().Be(3);
        everything.Detail.Should().Be("3 sources currently indexed");
        everything.IsAvailable.Should().BeTrue();
    }

    [Fact]
    public async Task NothingChangedSaysSoAndIsUnavailable()
    {
        var scopes = await IndexThreeAndResolveAsync();
        var changed = scopes.Single(s => s.Kind == ReindexScopeKind.OnlyWhatChanged);

        changed.Count.Should().Be(0);
        changed.Detail.Should().Be("Nothing has changed");
        changed.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task EditingAFileMakesItShowUpUnderOnlyWhatChanged()
    {
        await IndexThreeAndResolveAsync();

        // Edit one of the three on disk.
        File.WriteAllText(Path.Combine(_root, "b.txt"), "beta wording, now different");

        var scopes = await new ReindexScopeResolver(_store).ResolveAsync(CancellationToken.None);
        var changed = scopes.Single(s => s.Kind == ReindexScopeKind.OnlyWhatChanged);

        changed.Count.Should().Be(1);
        changed.Detail.Should().Be("1 new or edited since the last run");
        changed.Sources.Single().Name.Should().Be("b.txt");
    }

    [Fact]
    public async Task ADeletedFileAlsoCountsAsChanged()
    {
        await IndexThreeAndResolveAsync();
        File.Delete(Path.Combine(_root, "a.txt"));

        var scopes = await new ReindexScopeResolver(_store).ResolveAsync(CancellationToken.None);

        scopes.Single(s => s.Kind == ReindexScopeKind.OnlyWhatChanged).Count.Should().Be(1);
    }

    [Fact]
    public async Task JustTheChatsIsEmptyWhenNoChatIsIndexed()
    {
        var scopes = await IndexThreeAndResolveAsync();
        var chats = scopes.Single(s => s.Kind == ReindexScopeKind.JustTheChats);

        chats.Count.Should().Be(0);
        chats.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task EachScopeOpensTheLogWithItsOwnLine()
    {
        var scopes = await IndexThreeAndResolveAsync();

        scopes.Single(s => s.Kind == ReindexScopeKind.Everything).OpeningLogLine(3)
            .Should().Be("Index run started - 3 of 3 sources current");

        scopes.Single(s => s.Kind == ReindexScopeKind.OnlyWhatChanged).OpeningLogLine(3)
            .Should().Be("Index run started - 0 changed of 3");

        scopes.Single(s => s.Kind == ReindexScopeKind.PickedSources).OpeningLogLine(3)
            .Should().Be("Index run started - 3 picked of 3");
    }

    [Fact]
    public async Task EachScopeBuildsItsOwnQueue()
    {
        var scopes = await IndexThreeAndResolveAsync();

        var everything = scopes.Single(s => s.Kind == ReindexScopeKind.Everything).ToRequest();
        var chats = scopes.Single(s => s.Kind == ReindexScopeKind.JustTheChats).ToRequest();

        everything.Files.Should().HaveCount(3);
        chats.Files.Should().BeEmpty("no chat is indexed, so its queue is empty");
    }

    [Fact]
    public async Task TheQueuedCountReflectsTheScopeThatWasRun()
    {
        var scopes = await IndexThreeAndResolveAsync();

        // Narrow the scope to one picked source.
        var all = scopes.Single(s => s.Kind == ReindexScopeKind.Everything);
        var picked = new ReindexScope
        {
            Kind = ReindexScopeKind.PickedSources,
            Sources = [all.Sources[0]],
        };

        var progress = new SyncProgress<IndexingProgress>();
        await _service.IndexAsync(picked.ToRequest(), progress, CancellationToken.None);

        var reports = progress.Reports;
        reports[0].FilesTotal.Should().Be(1, "the queue is the scope, not everything indexed");
        reports[0].Queued.Should().Be(1);
        reports[^1].FilesDone.Should().Be(1);
    }

    [Fact]
    public async Task AScopeCannotLeakIntoALaterPass()
    {
        var scopes = await IndexThreeAndResolveAsync();
        var all = scopes.Single(s => s.Kind == ReindexScopeKind.Everything);

        // Run a narrow scope first.
        var narrow = new ReindexScope { Kind = ReindexScopeKind.PickedSources, Sources = [all.Sources[0]] };
        var first = new SyncProgress<IndexingProgress>();
        await _service.IndexAsync(narrow.ToRequest(), first, CancellationToken.None);

        // Then a wide one. Nothing from the first run may constrain it.
        var wide = new SyncProgress<IndexingProgress>();
        await _service.IndexAsync(all.ToRequest(), wide, CancellationToken.None);

        var firstReports = first.Reports;
        var wideReports = wide.Reports;
        firstReports[0].FilesTotal.Should().Be(1);
        wideReports[0].FilesTotal.Should().Be(3, "the earlier narrow scope did not survive into this pass");
    }

    [Fact]
    public async Task ThePickStageListsEverySourceWithItsChunkCount()
    {
        var scopes = await IndexThreeAndResolveAsync();
        var pick = scopes.Single(s => s.Kind == ReindexScopeKind.PickedSources);

        pick.Sources.Should().HaveCount(3);
        pick.Sources.Should().OnlyContain(s => s.ChunkCount > 0);
        pick.Detail.Should().Be("Choose from what is already indexed");
    }

    [Fact]
    public void TheContentHashIsStableAndShared()
    {
        // One implementation, shared by the indexer and the resolver.
        const string text = "alpha wording";

        var hash = ContentHash.Of(text);

        hash.Should().HaveLength(16);
        hash.Should().Be(ContentHash.Of(text));
    }
}
