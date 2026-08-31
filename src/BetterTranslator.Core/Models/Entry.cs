namespace BetterTranslator.Core.Models;

public enum EntryKind
{
    /// <summary>A single word or a short list of them, rendered as source and result stacked.</summary>
    Words,

    /// <summary>A sentence, rendered as source above result.</summary>
    Sentence,

    /// <summary>An attached file, with a preview affordance.</summary>
    File,
}

public enum EntryState
{
    Pending,
    Done,

    /// <summary>
    /// D2: the runtime was unreachable or inference failed. The entry keeps its
    /// source and offers Retry rather than raising a toast.
    /// </summary>
    Failed,

    Stopped,
}

public sealed class Entry
{
    public required Guid Id { get; init; }

    public required Guid ChatId { get; init; }

    public required EntryKind Kind { get; init; }

    public required string Source { get; set; }

    public string Result { get; set; } = string.Empty;

    /// <summary>Optional note under a sentence entry, as in "Context: ...".</summary>
    public string? Context { get; set; }

    /// <summary>
    /// The language this entry was translated into, by its English name. The
    /// result is headed with it, so it has to be what was chosen at the time
    /// rather than whatever the composer is set to now. Empty on entries
    /// written before the column existed.
    /// </summary>
    public string TargetLanguage { get; init; } = string.Empty;

    /// <summary>
    /// The pair this entry was sent under, as canonical codes.
    ///
    /// Recorded rather than worked out again, because a retry has to run the
    /// direction the row was created with and the two pickers above it move.
    /// Reading <see cref="TargetLanguage"/> back asks a display name what the
    /// translation was for, which fails on any name the registry does not know
    /// and then has nothing to answer with but whatever the composer is set to
    /// now -- a different language under the heading the row already carries.
    ///
    /// Empty on rows written before these columns existed, and empty on the
    /// source side when detection left it unknown. Both are honest gaps, not
    /// defaults: the code that reads them treats an empty side as unrecorded
    /// rather than as a language.
    /// </summary>
    public string SourceCode { get; init; } = string.Empty;

    /// <inheritdoc cref="SourceCode"/>
    public string TargetCode { get; init; } = string.Empty;

    public required DateTimeOffset CreatedAt { get; init; }

    public EntryState State { get; set; } = EntryState.Pending;

    /// <summary>
    /// Tokens the model generated for this entry, and how long the whole
    /// translation took. Null on an entry that was never run through a model --
    /// one answered from memory, or written before these were recorded -- which
    /// is why they are nullable rather than zero: no measurement and a
    /// measurement of nothing are different facts.
    ///
    /// Generated only. The runtime exports no tokenizer, so the prompt side
    /// cannot be counted, and a guess beside a real figure would read as one.
    /// </summary>
    public int? GeneratedTokens { get; set; }

    public int? DurationMs { get; set; }

    /// <summary>Set for file entries only.</summary>
    public string? FilePath { get; init; }

    public string? FileName { get; init; }

    public long? FileSizeBytes { get; init; }
}
