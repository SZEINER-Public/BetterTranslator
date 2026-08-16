using Microsoft.Data.Sqlite;

namespace BetterTranslator.Core.Services;

/// <summary>
/// The local SQLite store. Migrations are an ordered list applied once each;
/// the count of applied steps is the schema version, so adding a step at the
/// end is the only supported way to change the schema.
/// </summary>
public sealed class Database
{
    private static readonly string[] Migrations =
    [
        """
        CREATE TABLE chats (
            id                TEXT    NOT NULL PRIMARY KEY,
            name              TEXT    NOT NULL,
            created_at        TEXT    NOT NULL,
            updated_at        TEXT    NOT NULL,
            is_pinned         INTEGER NOT NULL DEFAULT 0,
            name_provisional  INTEGER NOT NULL DEFAULT 1
        );

        CREATE TABLE entries (
            id           TEXT    NOT NULL PRIMARY KEY,
            chat_id      TEXT    NOT NULL REFERENCES chats(id) ON DELETE CASCADE,
            kind         INTEGER NOT NULL,
            source       TEXT    NOT NULL,
            result       TEXT    NOT NULL DEFAULT '',
            context      TEXT    NULL,
            created_at   TEXT    NOT NULL,
            state        INTEGER NOT NULL DEFAULT 0,
            file_path    TEXT    NULL,
            file_name    TEXT    NULL,
            file_size    INTEGER NULL
        );

        CREATE INDEX ix_entries_chat ON entries(chat_id, created_at);

        CREATE TABLE settings (
            key   TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );

        CREATE TABLE memory_entries (
            id          TEXT    NOT NULL PRIMARY KEY,
            source_term TEXT    NOT NULL,
            target_term TEXT    NOT NULL,
            category    TEXT    NOT NULL,
            confidence  INTEGER NOT NULL,
            origin      TEXT    NULL,
            learned_at  TEXT    NOT NULL
        );

        CREATE INDEX ix_memory_source ON memory_entries(source_term);
        """,

        // The result is headed with the language it was translated into, which
        // has to be the one chosen at the time. Rows written before this keep
        // the empty default and are headed neutrally rather than being given a
        // language the database never recorded.
        """
        ALTER TABLE entries ADD COLUMN target_language TEXT NOT NULL DEFAULT '';
        """,

        // What the translation cost, shown in Advanced. Nullable rather than
        // defaulted to zero: rows written before this, and rows answered from
        // memory without a model, were never measured, and reporting them as
        // "0 tokens in 0 ms" would state a measurement that was never taken.
        """
        ALTER TABLE entries ADD COLUMN generated_tokens INTEGER NULL;
        ALTER TABLE entries ADD COLUMN duration_ms INTEGER NULL;
        """,

        // The pair the row was sent under, so a retry runs that pair and not
        // whatever the pickers are set to when the button is pressed. Codes,
        // not names: target_language stays as it is, because it is what the
        // result is headed with, and a heading is the one place a display name
        // belongs. Rows written before this keep the empty default, which the
        // reader treats as unrecorded rather than as a language.
        """
        ALTER TABLE entries ADD COLUMN source_code TEXT NOT NULL DEFAULT '';
        ALTER TABLE entries ADD COLUMN target_code TEXT NOT NULL DEFAULT '';
        """,
    ];

    private readonly string _connectionString;

    public Database(AppPaths paths)
    {
        paths.EnsureCreated();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabaseFile,
            ForeignKeys = true,
        }.ToString();
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (var busy = connection.CreateCommand())
        {
            busy.CommandText = "PRAGMA busy_timeout = 5000;";
            await busy.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return connection;
    }

    /// <summary>
    /// Write-ahead logging, so a reader never blocks the writer.
    ///
    /// There are two processes on this file now: the window, and an agent
    /// translating through bt or through the server inside it. Both write chats
    /// and entries. Under the default rollback journal a read and a write
    /// exclude each other outright, so the window listing chats could make an
    /// agent's row wait out the busy timeout and fail.
    ///
    /// Set once and kept: the mode lives in the file, not in the connection.
    /// Tolerated when it will not take -- a data folder on a network share
    /// cannot do WAL, and the honest outcome there is the journal it can do
    /// rather than a database nobody can open.
    /// </summary>
    private static async Task PreferWriteAheadLogAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode = WAL;";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
        }
    }

    /// <summary>
    /// Folds the write-ahead log back into the database file and empties it.
    ///
    /// For the one operation that treats the database as a file rather than as a
    /// database: copying the data folder somewhere else. A .db copied while its
    /// -wal still holds committed pages is a database missing its most recent
    /// writes.
    /// </summary>
    public async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException)
        {
        }
    }

    public async Task MigrateAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);

        await PreferWriteAheadLogAsync(connection, cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);",
            cancellationToken).ConfigureAwait(false);

        var applied = await ReadVersionAsync(connection, cancellationToken).ConfigureAwait(false);

        for (var step = applied; step < Migrations.Length; step++)
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = Migrations[step];
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var version = connection.CreateCommand())
            {
                version.Transaction = (SqliteTransaction)transaction;
                version.CommandText = "DELETE FROM schema_version; INSERT INTO schema_version (version) VALUES ($v);";
                version.Parameters.AddWithValue("$v", step + 1);
                await version.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReadVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version FROM schema_version LIMIT 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0 : Convert.ToInt32(value);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
