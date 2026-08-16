namespace BetterTranslator.Indexing.Retrieval;

public enum RetrievalKind
{
    /// <summary>Dense vector similarity.</summary>
    Dense,

    /// <summary>BM25 lexical.</summary>
    Lexical,

    /// <summary>Both agreed on this chunk.</summary>
    Fused,
}

/// <summary>One chunk a retrieval turned up, and how it was found.</summary>
public sealed record ScoredChunk(Guid ChunkId, double Score, RetrievalKind Kind)
{
    /// <summary>Position in the result list, from 1. Set when the list is ranked.</summary>
    public int Rank { get; init; }

    /// <summary>
    /// "query.hit - rank 1 - 0.89 - bm25", the activity log's subtitle.
    ///
    /// The score is formatted invariantly: this is a technical readout of a
    /// retrieval score, not a figure in the user's own units, and the design
    /// shows a decimal point regardless of locale.
    /// </summary>
    public string LogDetail =>
        $"query.hit - rank {Rank} - {Score.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} - {Kind switch
        {
            RetrievalKind.Dense => "dense",
            RetrievalKind.Lexical => "bm25",
            _ => "fused",
        }}";
}

/// <summary>
/// Combines the two retrieval paths. Dense needs embeddings and so waits on a
/// model; lexical does not, so a project is searchable as soon as it is
/// indexed, and the fusion degrades to lexical alone rather than to nothing.
/// </summary>
public static class RetrievalEngine
{
    /// <summary>
    /// Cosine similarity. Returns 0 for a zero or mismatched vector rather
    /// than a NaN that would poison the ranking.
    /// </summary>
    public static double Cosine(float[]? a, float[]? b)
    {
        if (a is null || b is null || a.Length == 0 || a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, magnitudeA = 0, magnitudeB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * (double)b[i];
            magnitudeA += a[i] * (double)a[i];
            magnitudeB += b[i] * (double)b[i];
        }

        if (magnitudeA <= 0 || magnitudeB <= 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(magnitudeA) * Math.Sqrt(magnitudeB));
    }

    /// <summary>
    /// Scores chunks by vector similarity against a query vector.
    /// </summary>
    public static IReadOnlyList<ScoredChunk> Dense(
        float[]? queryVector,
        IEnumerable<(Guid ChunkId, float[]? Vector)> chunks,
        int take = 10)
    {
        if (queryVector is null || queryVector.Length == 0)
        {
            return [];
        }

        return
        [
            .. chunks
                .Select(c => new ScoredChunk(c.ChunkId, Cosine(queryVector, c.Vector), RetrievalKind.Dense))
                .Where(h => h.Score > 0)
                .OrderByDescending(h => h.Score)
                .Take(take)
        ];
    }

    /// <summary>
    /// Reciprocal rank fusion. A chunk both paths found becomes Fused; one
    /// found by a single path keeps that path's kind, which is what colours
    /// its edge on the map.
    /// </summary>
    public static IReadOnlyList<ScoredChunk> Fuse(
        IReadOnlyList<ScoredChunk> dense,
        IReadOnlyList<ScoredChunk> lexical,
        int take = 10)
    {
        // The constant keeps a single top-1 hit from dominating a chunk that
        // both paths ranked well.
        const double K = 60.0;

        var combined = new Dictionary<Guid, (double Score, bool InDense, bool InLexical)>();

        for (var i = 0; i < dense.Count; i++)
        {
            var id = dense[i].ChunkId;
            var existing = combined.GetValueOrDefault(id);
            combined[id] = (existing.Score + (1.0 / (K + i + 1)), true, existing.InLexical);
        }

        for (var i = 0; i < lexical.Count; i++)
        {
            var id = lexical[i].ChunkId;
            var existing = combined.GetValueOrDefault(id);
            combined[id] = (existing.Score + (1.0 / (K + i + 1)), existing.InDense, true);
        }

        var ranked = combined
            .Select(entry => new ScoredChunk(
                entry.Key,
                entry.Value.Score,
                entry.Value is { InDense: true, InLexical: true }
                    ? RetrievalKind.Fused
                    : entry.Value.InDense ? RetrievalKind.Dense : RetrievalKind.Lexical))
            .OrderByDescending(h => h.Score)
            .Take(take)
            .ToList();

        // Rank is assigned after ordering, so it is the position shown.
        return [.. ranked.Select((hit, index) => hit with { Rank = index + 1 })];
    }
}
