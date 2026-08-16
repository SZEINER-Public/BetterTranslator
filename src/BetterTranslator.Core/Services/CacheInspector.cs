namespace BetterTranslator.Core.Services;

/// <summary>
/// One row of the Your data cache table.
/// </summary>
public sealed record CacheEntry
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string Note { get; init; }

    /// <summary>Real bytes on disk, measured rather than remembered.</summary>
    public required long Bytes { get; init; }

    /// <summary>Deleting this also wipes what memory has learned.</summary>
    public bool WipesMemory { get; init; }

    /// <summary>False where deletion is not offered.</summary>
    public bool CanDelete { get; init; } = true;

    /// <summary>Through the one byte formatter, so every surface agrees.</summary>
    public string SizeLabel => ByteSize.Format(Bytes);
}

/// <summary>
/// Measures what the application is storing, and deletes it on request. Every
/// figure comes from the file system at the moment it is asked for, so the
/// table re-reads from disk after a delete rather than subtracting a number.
/// </summary>
public sealed class CacheInspector(AppPaths paths, Database database)
{
    /// <summary>
    /// Settles the database into its file, for a caller about to copy that file.
    /// See <see cref="Database.CheckpointAsync"/>.
    /// </summary>
    public Task SettleAsync(CancellationToken cancellationToken) => database.CheckpointAsync(cancellationToken);

    public const string IndexDatabase = "index-database";
    public const string EmbeddingCache = "embedding-cache";
    public const string FilePreviews = "file-previews";
    public const string LearnedMemory = "learned-memory";
    public const string ChatHistory = "chat-history";

    /// <summary>
    /// The five rows, measured now. The database is one file, so the rows that
    /// live inside it report their share of it by row count rather than
    /// claiming the whole file each.
    /// </summary>
    public async Task<IReadOnlyList<CacheEntry>> InspectAsync(CancellationToken cancellationToken)
    {
        // The write-ahead log is part of how much room the database is taking
        // up, and after a busy session it is not a rounding error. Reporting the
        // .db alone would understate it by everything written since the last
        // checkpoint.
        var databaseBytes = FileBytes(paths.DatabaseFile)
            + FileBytes(paths.DatabaseFile + "-wal")
            + FileBytes(paths.DatabaseFile + "-shm");
        var shares = await TableSharesAsync(databaseBytes, cancellationToken).ConfigureAwait(false);

        return
        [
            new CacheEntry
            {
                Id = IndexDatabase,
                Name = "Index database",
                Note = "Chunks and vectors for everything indexed",
                Bytes = shares.Index,
                WipesMemory = true,
            },
            new CacheEntry
            {
                Id = EmbeddingCache,
                Name = "Embedding cache",
                Note = "Reusable vectors, so a reindex is faster",
                Bytes = FolderBytes(paths.EmbeddingCacheFolder),
            },
            new CacheEntry
            {
                Id = FilePreviews,
                Name = "File previews",
                Note = "Text and page images pulled out of documents",
                Bytes = FolderBytes(paths.PreviewCacheFolder),
            },
            new CacheEntry
            {
                Id = LearnedMemory,
                Name = "Learned memory",
                Note = "Term pairs, your corrections, rejected phrasings",
                Bytes = shares.Memory,
                WipesMemory = true,
            },
            new CacheEntry
            {
                Id = ChatHistory,
                Name = "Chat history",
                Note = "Every translation you have run",
                Bytes = shares.Chats,
            },
        ];
    }

    /// <summary>Total of the rows, the figure in the card header.</summary>
    public static long Total(IEnumerable<CacheEntry> entries) => entries.Sum(e => e.Bytes);

    /// <summary>
    /// Deletes what a row names. Returns the bytes actually freed, measured as
    /// the difference rather than assumed from the row's figure.
    /// </summary>
    public async Task<long> DeleteAsync(string id, CancellationToken cancellationToken)
    {
        var before = await InspectAsync(cancellationToken).ConfigureAwait(false);
        var beforeBytes = before.FirstOrDefault(e => e.Id == id)?.Bytes ?? 0;

        switch (id)
        {
            case EmbeddingCache:
                ClearFolder(paths.EmbeddingCacheFolder);
                break;

            case FilePreviews:
                ClearFolder(paths.PreviewCacheFolder);
                break;

            case IndexDatabase:
                await ExecuteAsync("DELETE FROM chunks;", cancellationToken).ConfigureAwait(false);
                await ExecuteAsync("DELETE FROM sources;", cancellationToken).ConfigureAwait(false);
                break;

            case LearnedMemory:
                await ExecuteAsync("DELETE FROM memory_entries;", cancellationToken).ConfigureAwait(false);
                break;

            case ChatHistory:
                await ExecuteAsync("DELETE FROM entries;", cancellationToken).ConfigureAwait(false);
                await ExecuteAsync("DELETE FROM chats;", cancellationToken).ConfigureAwait(false);
                break;

            default:
                return 0;
        }

        var after = await InspectAsync(cancellationToken).ConfigureAwait(false);
        var afterBytes = after.FirstOrDefault(e => e.Id == id)?.Bytes ?? 0;

        return Math.Max(0, beforeBytes - afterBytes);
    }

    /// <summary>Removes everything the application stores on this machine.</summary>
    public async Task DeleteEverythingAsync(CancellationToken cancellationToken)
    {
        string[] statements =
        [
            "DELETE FROM chunks;",
            "DELETE FROM sources;",
            "DELETE FROM entries;",
            "DELETE FROM chats;",
            "DELETE FROM memory_entries;",
            "DELETE FROM settings;",
        ];

        foreach (var statement in statements)
        {
            await ExecuteAsync(statement, cancellationToken).ConfigureAwait(false);
        }

        ClearFolder(paths.EmbeddingCacheFolder);
        ClearFolder(paths.PreviewCacheFolder);
    }

    /// <summary>
    /// Runs one statement, tolerating a table that has not been created yet.
    /// The index tables arrive with the first indexing run, so deleting before
    /// then must not fail.
    /// </summary>
    private async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase))
        {
            // Nothing of that kind has been stored yet, so nothing to remove.
        }
    }

    /// <summary>
    /// Splits the database file between the things stored in it, weighted by
    /// row count, so the five rows sum to what is actually on disk.
    /// </summary>
    private async Task<(long Index, long Memory, long Chats)> TableSharesAsync(
        long databaseBytes,
        CancellationToken cancellationToken)
    {
        var chunks = await CountAsync("chunks", cancellationToken).ConfigureAwait(false);
        var memory = await CountAsync("memory_entries", cancellationToken).ConfigureAwait(false);
        var entries = await CountAsync("entries", cancellationToken).ConfigureAwait(false);

        var total = chunks + memory + entries;
        if (total == 0)
        {
            return (0, 0, 0);
        }

        return (
            databaseBytes * chunks / total,
            databaseBytes * memory / total,
            databaseBytes * entries / total);
    }

    private async Task<long> CountAsync(string table, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM {table};";

            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return value is null or DBNull ? 0 : Convert.ToInt64(value);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            // The table has not been created yet.
            return 0;
        }
    }

    private static long FileBytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;

    private static long FolderBytes(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Sum(f => new FileInfo(f).Length);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void ClearFolder(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A file in use is left; the next pass picks it up.
            }
        }
    }
}
