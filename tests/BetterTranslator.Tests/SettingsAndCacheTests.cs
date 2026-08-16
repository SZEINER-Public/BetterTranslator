using System.IO;
using BetterTranslator.Core.Models;
using BetterTranslator.Core.Services;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// S13. Settings survive a restart, and every size figure is measured from disk
/// rather than remembered, so deleting a row frees what it named.
/// </summary>
public sealed class SettingsAndCacheTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-settings-store", Guid.NewGuid().ToString("N"));
    private AppPaths _paths = null!;
    private Database _database = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _paths = new AppPaths(_root);
        _database = new Database(_paths);
        await _database.MigrateAsync(CancellationToken.None);
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
    public async Task DefaultsAreTheDocumentedOnesBeforeAnythingIsSaved()
    {
        var settings = await new SettingsStore(_database).LoadAsync(CancellationToken.None);

        settings.LearnFromMyEdits.Should().BeTrue();
        settings.UnderlineMemoryWords.Should().BeTrue();
        settings.ReindexFilesWhenTheyChange.Should().BeTrue();
        settings.DefaultScopeForNewChats.Should().Be(ChatScope.WholeProject);
        settings.UnsureThresholdPercent.Should().Be(80);
    }

    [Fact]
    public async Task EverySettingSurvivesARoundTrip()
    {
        var store = new SettingsStore(_database);

        await store.SaveAsync(new AppSettings
        {
            LearnFromMyEdits = false,
            UnderlineMemoryWords = false,
            ReindexFilesWhenTheyChange = false,
            DefaultScopeForNewChats = ChatScope.ThisChat,
            UnsureThresholdPercent = 65,
            TargetLanguage = "German",
        }, CancellationToken.None);

        var loaded = await store.LoadAsync(CancellationToken.None);

        loaded.LearnFromMyEdits.Should().BeFalse();
        loaded.UnderlineMemoryWords.Should().BeFalse();
        loaded.ReindexFilesWhenTheyChange.Should().BeFalse();
        loaded.DefaultScopeForNewChats.Should().Be(ChatScope.ThisChat);
        loaded.UnsureThresholdPercent.Should().Be(65);
        loaded.TargetLanguage.Should().Be("German");
    }

    [Fact]
    public async Task SavingTwiceUpdatesRatherThanDuplicating()
    {
        var store = new SettingsStore(_database);

        await store.SaveAsync(new AppSettings { UnsureThresholdPercent = 70 }, CancellationToken.None);
        await store.SaveAsync(new AppSettings { UnsureThresholdPercent = 90 }, CancellationToken.None);

        (await store.LoadAsync(CancellationToken.None)).UnsureThresholdPercent.Should().Be(90);
    }

    [Fact]
    public async Task TheCacheTableHasItsFiveRowsWithTheDocumentedNotes()
    {
        var entries = await new CacheInspector(_paths, _database).InspectAsync(CancellationToken.None);

        entries.Select(e => e.Name).Should().Equal(
            "Index database", "Embedding cache", "File previews", "Learned memory", "Chat history");

        entries.Single(e => e.Id == CacheInspector.EmbeddingCache).Note
            .Should().Be("Reusable vectors, so a reindex is faster");

        // The two that wipe memory are marked, so the consequence is stated.
        entries.Where(e => e.WipesMemory).Select(e => e.Name)
            .Should().Equal("Index database", "Learned memory");
    }

    [Fact]
    public async Task SizesComeFromDiskAndUseTheOneFormatter()
    {
        var inspector = new CacheInspector(_paths, _database);

        // 300 KB of previews on disk.
        var payload = new byte[300 * 1024];
        await File.WriteAllBytesAsync(Path.Combine(_paths.PreviewCacheFolder, "preview.bin"), payload);

        var entries = await inspector.InspectAsync(CancellationToken.None);
        var previews = entries.Single(e => e.Id == CacheInspector.FilePreviews);

        previews.Bytes.Should().Be(payload.Length);
        previews.SizeLabel.Should().Be(ByteSize.Format(payload.Length), "one formatter is behind every figure");
    }

    [Fact]
    public async Task DeletingARowFreesTheSizeItNamedAndTheTableReReadsFromDisk()
    {
        var inspector = new CacheInspector(_paths, _database);

        await File.WriteAllBytesAsync(
            Path.Combine(_paths.EmbeddingCacheFolder, "vectors.bin"),
            new byte[200 * 1024]);

        var before = await inspector.InspectAsync(CancellationToken.None);
        before.Single(e => e.Id == CacheInspector.EmbeddingCache).Bytes.Should().Be(200 * 1024);

        var freed = await inspector.DeleteAsync(CacheInspector.EmbeddingCache, CancellationToken.None);
        freed.Should().Be(200 * 1024, "the figure freed is the figure the row named");

        var after = await inspector.InspectAsync(CancellationToken.None);
        after.Single(e => e.Id == CacheInspector.EmbeddingCache).Bytes
            .Should().Be(0, "the table re-reads from disk rather than subtracting");
    }

    [Fact]
    public async Task DeletingChatHistoryRemovesTheChats()
    {
        var chats = new ChatStore(_database);
        var now = DateTimeOffset.Now;

        await chats.AddChatAsync(
            new Chat { Id = Guid.NewGuid(), Name = "workspace", CreatedAt = now, UpdatedAt = now },
            CancellationToken.None);

        (await chats.GetChatsAsync(CancellationToken.None)).Should().ContainSingle();

        await new CacheInspector(_paths, _database)
            .DeleteAsync(CacheInspector.ChatHistory, CancellationToken.None);

        (await chats.GetChatsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteEverythingClearsChatsAndSettingsTogether()
    {
        var chats = new ChatStore(_database);
        var store = new SettingsStore(_database);
        var now = DateTimeOffset.Now;

        await chats.AddChatAsync(
            new Chat { Id = Guid.NewGuid(), Name = "workspace", CreatedAt = now, UpdatedAt = now },
            CancellationToken.None);
        await store.SaveAsync(new AppSettings { UnsureThresholdPercent = 55 }, CancellationToken.None);

        await new CacheInspector(_paths, _database).DeleteEverythingAsync(CancellationToken.None);

        (await chats.GetChatsAsync(CancellationToken.None)).Should().BeEmpty();

        // Settings fall back to their defaults rather than to nothing.
        (await store.LoadAsync(CancellationToken.None)).UnsureThresholdPercent.Should().Be(80);
    }

    [Fact]
    public async Task AnUnknownRowFreesNothingRatherThanThrowing() =>
        (await new CacheInspector(_paths, _database).DeleteAsync("not-a-row", CancellationToken.None))
            .Should().Be(0);

    [Fact]
    public async Task TheHeaderTotalIsTheSumOfTheRows()
    {
        await File.WriteAllBytesAsync(Path.Combine(_paths.PreviewCacheFolder, "a.bin"), new byte[1000]);
        await File.WriteAllBytesAsync(Path.Combine(_paths.EmbeddingCacheFolder, "b.bin"), new byte[2000]);

        var entries = await new CacheInspector(_paths, _database).InspectAsync(CancellationToken.None);

        CacheInspector.Total(entries).Should().Be(entries.Sum(e => e.Bytes));
        CacheInspector.Total(entries).Should().BeGreaterThanOrEqualTo(3000);
    }
}
