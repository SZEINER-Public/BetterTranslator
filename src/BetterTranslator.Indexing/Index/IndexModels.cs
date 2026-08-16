namespace BetterTranslator.Indexing.Index;

public enum SourceKind
{
    Folder,
    Repository,
    File,
    Chat,
    PastedNote,
}

/// <summary>
/// One thing memory was pointed at. Counts are stored as they were measured, so
/// the rail and the map read the same figures rather than each deriving its own.
/// </summary>
public sealed record IndexedSource
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Absolute path, or the chat id for a chat source.</summary>
    public required string Location { get; init; }

    public required SourceKind Kind { get; init; }

    public int FileCount { get; init; }

    public int ChunkCount { get; init; }

    public int WordCount { get; init; }

    public required DateTimeOffset IndexedAt { get; init; }

    /// <summary>Content hash, so an unchanged file can be skipped on a reindex.</summary>
    public string? ContentHash { get; init; }
}

public sealed record IndexedChunk
{
    public required Guid Id { get; init; }

    public required Guid SourceId { get; init; }

    public required int Ordinal { get; init; }

    public required string Text { get; init; }

    public required int WordCount { get; init; }

    /// <summary>The file this chunk came from, for a folder or repository source.</summary>
    public string? FilePath { get; init; }

    /// <summary>Null until the embedding endpoint has been reached.</summary>
    public float[]? Vector { get; init; }
}

/// <summary>
/// What the map's stats strip and the rail count. Every figure traces to a row
/// in the index; none of them is a constant.
/// </summary>
public sealed record IndexTotals(int Sources, int Files, int Chunks, int Words, int Embedded)
{
    public static IndexTotals Empty { get; } = new(0, 0, 0, 0, 0);

    public bool IsEmpty => Chunks == 0 && Sources == 0;
}
