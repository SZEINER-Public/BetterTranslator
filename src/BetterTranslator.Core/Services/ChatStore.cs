using System.Globalization;
using BetterTranslator.Core.Models;
using Microsoft.Data.Sqlite;

namespace BetterTranslator.Core.Services;

/// <summary>
/// Reads and writes chats and their entries. Every call is asynchronous and
/// takes a token: none of this may run on the UI thread.
/// </summary>
public sealed class ChatStore(Database database)
{
    private const string RoundTrip = "O";

    public async Task<IReadOnlyList<Chat>> GetChatsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, created_at, updated_at, is_pinned, name_provisional FROM chats ORDER BY updated_at DESC;";

        var chats = new List<Chat>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            chats.Add(new Chat
            {
                Id = Guid.Parse(reader.GetString(0)),
                Name = reader.GetString(1),
                CreatedAt = ParseMoment(reader.GetString(2)),
                UpdatedAt = ParseMoment(reader.GetString(3)),
                IsPinned = reader.GetInt64(4) != 0,
                NameIsProvisional = reader.GetInt64(5) != 0,
            });
        }

        return chats;
    }

    public async Task AddChatAsync(Chat chat, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO chats (id, name, created_at, updated_at, is_pinned, name_provisional)
            VALUES ($id, $name, $created, $updated, $pinned, $provisional);
            """;
        command.Parameters.AddWithValue("$id", chat.Id.ToString());
        command.Parameters.AddWithValue("$name", chat.Name);
        command.Parameters.AddWithValue("$created", chat.CreatedAt.ToString(RoundTrip, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$updated", chat.UpdatedAt.ToString(RoundTrip, CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$pinned", chat.IsPinned ? 1 : 0);
        command.Parameters.AddWithValue("$provisional", chat.NameIsProvisional ? 1 : 0);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RenameChatAsync(Guid id, string name, bool provisional, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE chats SET name = $name, name_provisional = $provisional WHERE id = $id;";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$provisional", provisional ? 1 : 0);
        command.Parameters.AddWithValue("$id", id.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPinnedAsync(Guid id, bool pinned, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE chats SET is_pinned = $pinned WHERE id = $id;";
        command.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
        command.Parameters.AddWithValue("$id", id.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteChatAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // Foreign keys are on, so the entries go with it.
        command.CommandText = "DELETE FROM chats WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One row out of a conversation, leaving the chat and everything else in it
    /// alone. The only way to take a single message back: deleting the chat was
    /// the whole conversation, and there was nothing between the two.
    /// </summary>
    public async Task DeleteEntryAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM entries WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Entry>> GetEntriesAsync(Guid chatId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, chat_id, kind, source, result, context, created_at, state, file_path, file_name, file_size,
                   target_language, generated_tokens, duration_ms, source_code, target_code
            FROM entries WHERE chat_id = $chat ORDER BY created_at;
            """;
        command.Parameters.AddWithValue("$chat", chatId.ToString());

        var entries = new List<Entry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            entries.Add(new Entry
            {
                Id = Guid.Parse(reader.GetString(0)),
                ChatId = Guid.Parse(reader.GetString(1)),
                Kind = (EntryKind)reader.GetInt64(2),
                Source = reader.GetString(3),
                Result = reader.GetString(4),
                Context = reader.IsDBNull(5) ? null : reader.GetString(5),
                CreatedAt = ParseMoment(reader.GetString(6)),
                State = (EntryState)reader.GetInt64(7),
                FilePath = reader.IsDBNull(8) ? null : reader.GetString(8),
                FileName = reader.IsDBNull(9) ? null : reader.GetString(9),
                FileSizeBytes = reader.IsDBNull(10) ? null : reader.GetInt64(10),
                TargetLanguage = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
                GeneratedTokens = reader.IsDBNull(12) ? null : (int)reader.GetInt64(12),
                DurationMs = reader.IsDBNull(13) ? null : (int)reader.GetInt64(13),
                SourceCode = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
                TargetCode = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
            });
        }

        return entries;
    }

    public async Task<Entry?> GetEntryAsync(Guid entryId, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, chat_id, kind, source, result, context, created_at, state, file_path, file_name, file_size,
                   target_language, generated_tokens, duration_ms, source_code, target_code
            FROM entries WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", entryId.ToString());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new Entry
        {
            Id = Guid.Parse(reader.GetString(0)),
            ChatId = Guid.Parse(reader.GetString(1)),
            Kind = (EntryKind)reader.GetInt64(2),
            Source = reader.GetString(3),
            Result = reader.GetString(4),
            Context = reader.IsDBNull(5) ? null : reader.GetString(5),
            CreatedAt = ParseMoment(reader.GetString(6)),
            State = (EntryState)reader.GetInt64(7),
            FilePath = reader.IsDBNull(8) ? null : reader.GetString(8),
            FileName = reader.IsDBNull(9) ? null : reader.GetString(9),
            FileSizeBytes = reader.IsDBNull(10) ? null : reader.GetInt64(10),
            TargetLanguage = reader.IsDBNull(11) ? string.Empty : reader.GetString(11),
            GeneratedTokens = reader.IsDBNull(12) ? null : (int)reader.GetInt64(12),
            DurationMs = reader.IsDBNull(13) ? null : (int)reader.GetInt64(13),
            SourceCode = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
            TargetCode = reader.IsDBNull(15) ? string.Empty : reader.GetString(15),
        };
    }

    /// <summary>
    /// Writes back what the model produced. Only the result and the state move:
    /// the entry was stored the moment it was sent, so a translation arriving
    /// seconds later must not rewrite the text or the timestamp beside it.
    /// </summary>
    public async Task UpdateEntryResultAsync(Entry entry, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();

        // The cost travels with the result, because it is measured by the same
        // call that produced it and would otherwise be lost on the next launch.
        command.CommandText =
            """
            UPDATE entries
            SET result = $result, state = $state, generated_tokens = $tokens, duration_ms = $ms
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$result", entry.Result);
        command.Parameters.AddWithValue("$state", (int)entry.State);
        command.Parameters.AddWithValue("$tokens", (object?)entry.GeneratedTokens ?? DBNull.Value);
        command.Parameters.AddWithValue("$ms", (object?)entry.DurationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", entry.Id.ToString());

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AddEntryAsync(Entry entry, CancellationToken cancellationToken)
    {
        await using var connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                """
                INSERT INTO entries
                    (id, chat_id, kind, source, result, context, created_at, state, file_path, file_name, file_size,
                     target_language, source_code, target_code)
                VALUES
                    ($id, $chat, $kind, $source, $result, $context, $created, $state, $path, $fname, $fsize,
                     $target, $fromCode, $toCode);
                """;
            command.Parameters.AddWithValue("$id", entry.Id.ToString());
            command.Parameters.AddWithValue("$chat", entry.ChatId.ToString());
            command.Parameters.AddWithValue("$kind", (int)entry.Kind);
            command.Parameters.AddWithValue("$source", entry.Source);
            command.Parameters.AddWithValue("$result", entry.Result);
            command.Parameters.AddWithValue("$context", (object?)entry.Context ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", entry.CreatedAt.ToString(RoundTrip, CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$state", (int)entry.State);
            command.Parameters.AddWithValue("$path", (object?)entry.FilePath ?? DBNull.Value);
            command.Parameters.AddWithValue("$fname", (object?)entry.FileName ?? DBNull.Value);
            command.Parameters.AddWithValue("$fsize", (object?)entry.FileSizeBytes ?? DBNull.Value);
            command.Parameters.AddWithValue("$target", entry.TargetLanguage);
            command.Parameters.AddWithValue("$fromCode", entry.SourceCode);
            command.Parameters.AddWithValue("$toCode", entry.TargetCode);

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // An entry is what makes a chat recent, so the sort key moves with it.
        await using (var touch = connection.CreateCommand())
        {
            touch.Transaction = (SqliteTransaction)transaction;
            touch.CommandText = "UPDATE chats SET updated_at = $updated WHERE id = $chat;";
            touch.Parameters.AddWithValue("$updated", entry.CreatedAt.ToString(RoundTrip, CultureInfo.InvariantCulture));
            touch.Parameters.AddWithValue("$chat", entry.ChatId.ToString());
            await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset ParseMoment(string raw) =>
        DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
