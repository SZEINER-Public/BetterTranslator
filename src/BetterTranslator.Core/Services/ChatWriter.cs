using BetterTranslator.Core.Languages;
using BetterTranslator.Core.Models;

namespace BetterTranslator.Core.Services;

/// <summary>
/// How a translation becomes a row, for every surface that produces one.
///
/// A translation is written twice and the two writes are separated by the model:
/// the row goes in when the send starts, so the source is on the surface while
/// the answer is still coming, and only the result and its cost are written when
/// it lands. Nothing else about the row moves -- rewriting the source or the
/// timestamp seconds later would make a row that was sent once look like a row
/// that was sent twice.
///
/// The field set matters as much as the sequence. Three of them decide how a row
/// renders and how a retry behaves, and each is easy to get subtly wrong from a
/// caller: the language NAME heads the result, the canonical CODES are what a
/// retry runs, and a result equal to its own source is dropped on load. So a
/// caller hands over the pair it was judged on and this derives all three.
/// </summary>
public sealed class ChatWriter(ChatStore store)
{
    private const int ProvisionalNameLength = 40;

    /// <summary>
    /// D6, unconfirmed: the name a chat carries until a model can summarize it.
    /// </summary>
    public static string ProvisionalName(string text)
    {
        var trimmed = text.Trim();

        return trimmed.Length <= ProvisionalNameLength
            ? trimmed
            : trimmed[..ProvisionalNameLength].TrimEnd();
    }

    /// <summary>
    /// A single word or a short list of them renders as source and result
    /// stacked; anything with a space in it is a sentence and renders one above
    /// the other.
    /// </summary>
    public static EntryKind KindFor(string text) =>
        text.Contains(' ', StringComparison.Ordinal) ? EntryKind.Sentence : EntryKind.Words;

    /// <summary>
    /// Starts a chat named after the first thing it holds, exactly as the window
    /// names one.
    /// </summary>
    public async Task<Chat> StartChatAsync(string firstText, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var chat = new Chat
        {
            Id = Guid.NewGuid(),
            Name = ProvisionalName(firstText),
            CreatedAt = now,
            UpdatedAt = now,
            NameIsProvisional = true,
        };

        await store.AddChatAsync(chat, cancellationToken).ConfigureAwait(false);

        return chat;
    }

    /// <summary>
    /// The row as it looks while the model is still working: the source, the
    /// pair, and no result. Written before the send so that what is on the
    /// surface is what was asked for, whatever happens next.
    /// </summary>
    public async Task<Entry> BeginAsync(
        Guid chatId,
        string source,
        TranslationDirection direction,
        DateTimeOffset now,
        FileEntry? file,
        CancellationToken cancellationToken)
    {
        var entry = new Entry
        {
            Id = Guid.NewGuid(),
            ChatId = chatId,
            Kind = file is null ? KindFor(source) : EntryKind.File,
            Source = source,
            Result = string.Empty,
            CreatedAt = now,
            State = EntryState.Pending,
            TargetLanguage = direction.Target.Name,
            SourceCode = direction.Source.Code.Value,
            TargetCode = direction.Target.Code.Value,
            FilePath = file?.Path,
            FileName = file?.Name,
            FileSizeBytes = file?.SizeBytes,
        };

        await store.AddEntryAsync(entry, cancellationToken).ConfigureAwait(false);

        return entry;
    }

    /// <summary>
    /// Lands the answer. A result identical to the source is not an answer: the
    /// reader's own loader drops it back to empty, so storing one would produce
    /// a row that reads as translated on this run and untranslated on the next.
    /// </summary>
    public async Task FinishAsync(
        Entry entry,
        string? result,
        int generatedTokens,
        int durationMs,
        CancellationToken cancellationToken)
    {
        var landed = !string.IsNullOrEmpty(result)
            && !string.Equals(result, entry.Source, StringComparison.Ordinal);

        entry.Result = landed ? result! : string.Empty;
        entry.State = landed ? EntryState.Done : EntryState.Failed;
        entry.GeneratedTokens = generatedTokens > 0 ? generatedTokens : null;
        entry.DurationMs = durationMs > 0 ? durationMs : null;

        await store.UpdateEntryResultAsync(entry, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The file an entry came from, for a row that shows one.</summary>
public sealed record FileEntry(string Path, string Name, long SizeBytes);
