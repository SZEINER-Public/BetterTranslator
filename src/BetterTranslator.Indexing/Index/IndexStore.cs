using System.Globalization;
using BetterTranslator.Core.Services;
using Microsoft.Data.Sqlite;

namespace BetterTranslator.Indexing.Index;

/// <summary>
/// Sources, chunks and vectors in the same local database as the chats. Every
/// count the application shows is read back from here.
/// </summary>
public sealed class IndexStore(Database database)
{
    private const string RoundTrip = "O";

    /// <summary>
    /// Creates the index tables. Idempotent, so it can run alongside the chat
    /// migrations without ordering between the two.
    /// </summary>
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS sources (
                id           TEXT    NOT NULL PRIMARY KEY,
                name         TEXT    NOT NULL,
                location     TEXT    NOT NULL,
                kind         INTEGER NOT NULL,
                file_count   INTEGER NOT NULL DEFAULT 0,
                chunk_count  INTEGER NOT NULL DEFAULT 0,
                word_count   INTEGER NOT NULL DEFAULT 0,
                indexed_at   TEXT    NOT NULL,
                content_hash TEXT    NULL
            );

            CREATE TABLE IF NOT EXISTS chunks (
                id         TEXT    NOT NULL PRIMARY KEY,
                source_id  TEXT    NOT NULL REFERENCES sources(id) ON DELETE CASCADE,
                ordinal    INTEGER NOT NULL,
                text       TEXT    NOT NULL,
                word_count INTEGER NOT NULL,
                file_path  TEXT    NULL,
                vector     BLOB    NULL
            );

            CREATE INDEX IF NOT EXISTS ix_chunks_source ON chunks(source_id, ordinal);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddSourceAsync(IndexedSource source, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO sources (id, name, location, kind, file_count, chunk_count, word_count, indexed_at, content_hash)
            VALUES ($id, $name, $location, $kind, $files, $chunks, $words, $at, $hash)
            ON CONFLICT(id) DO UPDATE SET
                name = $name, location = $location, kind = $kind,
                file_count = $files, chunk_count = $chunks, word_count = $words,
                indexed_at = $at, content_hash = $hash;
            """;

        command.Parameters.AddWithValue("$id", source.Id.ToString());
        command.Parameters.AddWithValue("$name", source.Name);
        command.Parameters.AddWithValue("$location", source.Location);
        command.Parameters.AddWithValue("$kind", (int)source.Kind);
        command.Parameters.AddWithValue("$files", source.FileCount);
        command.Parameters.AddWithValue("$chunks", source.ChunkCount);
        command.Parameters.AddWithValue("$words", source.WordCount);
        command.Parameters.AddWithValue("$at", source.IndexedAt.ToString(RoundTrip, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$hash", (object?)source.ContentHash ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes a batch of chunks in one transaction. Called per file rather than
    /// per chunk, so a large folder does not open a transaction per paragraph.
    /// </summary>
    public async Task AddChunksAsync(IEnumerable<IndexedChunk> chunks, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using var command = connection.CreateCommand();
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO chunks (id, source_id, ordinal, text, word_count, file_path, vector)
                VALUES ($id, $source, $ordinal, $text, $words, $path, $vector);
                """;

            command.Parameters.AddWithValue("$id", chunk.Id.ToString());
            command.Parameters.AddWithValue("$source", chunk.SourceId.ToString());
            command.Parameters.AddWithValue("$ordinal", chunk.Ordinal);
            command.Parameters.AddWithValue("$text", chunk.Text);
            command.Parameters.AddWithValue("$words", chunk.WordCount);
            command.Parameters.AddWithValue("$path", (object?)chunk.FilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$vector", (object?)Pack(chunk.Vector) ?? DBNull.Value);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IndexedSource>> GetSourcesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT id, name, location, kind, file_count, chunk_count, word_count, indexed_at, content_hash
            FROM sources ORDER BY indexed_at DESC;
            """;

        var sources = new List<IndexedSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            sources.Add(new IndexedSource
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                Location = reader.GetString(2),
                Kind = (SourceKind)reader.GetInt64(3),
                FileCount = (int)reader.GetInt64(4),
                ChunkCount = (int)reader.GetInt64(5),
                WordCount = (int)reader.GetInt64(6),
                IndexedAt = DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                ContentHash = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }

        return sources;
    }

    /// <summary>
    /// Chunks for one source, in order, with their vectors where they exist.
    /// </summary>
    public async Task<IReadOnlyList<IndexedChunk>> GetChunksAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT id, source_id, ordinal, text, word_count, file_path, vector
            FROM chunks WHERE source_id = $source ORDER BY ordinal;
            """;
        command.Parameters.AddWithValue("$source", sourceId.ToString());

        var chunks = new List<IndexedChunk>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chunks.Add(new IndexedChunk
            {
                Id = Guid.Parse(reader.GetString(0)),
                SourceId = Guid.Parse(reader.GetString(1)),
                Ordinal = (int)reader.GetInt64(2),
                Text = reader.GetString(3),
                WordCount = (int)reader.GetInt64(4),
                FilePath = reader.IsDBNull(5) ? null : reader.GetString(5),
                Vector = reader.IsDBNull(6) ? null : Unpack((byte[])reader[6]),
            });
        }

        return chunks;
    }

    /// <summary>
    /// The totals every surface reads. Computed from the rows, so a figure can
    /// never drift from what is actually stored.
    /// </summary>
    public async Task<IndexTotals> GetTotalsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM sources),
                (SELECT COALESCE(SUM(file_count), 0) FROM sources),
                (SELECT COUNT(*) FROM chunks),
                (SELECT COALESCE(SUM(word_count), 0) FROM chunks),
                (SELECT COUNT(*) FROM chunks WHERE vector IS NOT NULL);
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return IndexTotals.Empty;
        }

        return new IndexTotals(
            (int)reader.GetInt64(0),
            (int)reader.GetInt64(1),
            (int)reader.GetInt64(2),
            (int)reader.GetInt64(3),
            (int)reader.GetInt64(4));
    }

    public async Task RemoveSourceAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM chunks WHERE source_id = $id; DELETE FROM sources WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM chunks; DELETE FROM sources;";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static float[] Unpack(byte[] bytes)
    {
        var vector = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, vector, 0, bytes.Length);
        return vector;
    }

    private static byte[]? Pack(float[]? vector)
    {
        if (vector is null)
        {
            return null;
        }

        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }
}
