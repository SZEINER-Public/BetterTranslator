using BetterTranslator.Indexing.Index;

namespace BetterTranslator.Indexing.Retrieval;

/// <summary>One thing the retrieval did, for the live activity log.</summary>
public sealed record RetrievalEvent(string Line, string Detail, RetrievalKind Kind)
{
    public static RetrievalEvent Query(string text, int scope) =>
        new($"Query received - scoped to {(scope == 1 ? "1 source" : $"{scope} sources")}",
            "query.received", RetrievalKind.Lexical);

    public static RetrievalEvent Returned(int chunks, long milliseconds) =>
        new($"Returned {(chunks == 1 ? "1 chunk" : $"{chunks} chunks")} in {milliseconds} ms",
            "query.returned", RetrievalKind.Fused);
}

/// <summary>
/// What a lookup produced: the chunks that matched, the terms memory would
/// apply, and the log of what happened.
/// </summary>
public sealed record MemoryLookup(
    IReadOnlyList<ScoredChunk> Hits,
    IReadOnlyList<MemoryMatch> Matches,
    IReadOnlyList<RetrievalEvent> Events);

/// <summary>
/// Runs a lookup against the index and turns the hits into the terms a result
/// would be marked with.
///
/// The lexical path needs no model, so this produces real matches from real
/// indexed content today; the dense path joins in once chunks carry vectors.
/// </summary>
public sealed class MemoryService
{
    private readonly Bm25Index _lexical = new();
    private readonly List<(Guid ChunkId, string Text, string Source, float[]? Vector)> _chunks = [];

    public int ChunkCount => _chunks.Count;

    /// <summary>Loads the index into memory for lookup.</summary>
    /// <summary>
    /// Loads every indexed chunk from a store, which is the whole of what a
    /// lookup can see. Here rather than at the callers because there are two of
    /// them now -- the window's Memory screen and the agent gateway -- and the
    /// one thing they must not disagree about is what "the project" contains,
    /// down to the fallback name an orphaned chunk is attributed to.
    /// </summary>
    public async Task LoadFromAsync(Index.IndexStore store, CancellationToken cancellationToken)
    {
        await store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        var sources = await store.GetSourcesAsync(cancellationToken).ConfigureAwait(false);
        var chunks = new List<IndexedChunk>();

        foreach (var source in sources)
        {
            chunks.AddRange(await store.GetChunksAsync(source.Id, cancellationToken).ConfigureAwait(false));
        }

        Load(chunks, id => sources.FirstOrDefault(s => s.Id == id)?.Name ?? "memory");
    }

    public void Load(IEnumerable<IndexedChunk> chunks, Func<Guid, string> sourceName)
    {
        _chunks.Clear();

        foreach (var chunk in chunks)
        {
            _lexical.Add(chunk.Id, chunk.Text);
            _chunks.Add((chunk.Id, chunk.Text, sourceName(chunk.SourceId), chunk.Vector));
        }
    }

    /// <summary>
    /// Looks a phrase up. <paramref name="queryVector"/> is null until a model
    /// can embed the query, at which point the dense path joins the fusion.
    /// </summary>
    public MemoryLookup Lookup(string phrase, float[]? queryVector, int unsureThresholdPercent)
    {
        var events = new List<RetrievalEvent> { RetrievalEvent.Query(phrase, _chunks.Count) };
        var started = DateTimeOffset.UtcNow;

        var lexical = _lexical.Search(phrase);
        var dense = RetrievalEngine.Dense(queryVector, _chunks.Select(c => (c.ChunkId, c.Vector)));
        var hits = RetrievalEngine.Fuse(dense, lexical);

        foreach (var hit in hits)
        {
            var source = _chunks.FirstOrDefault(c => c.ChunkId == hit.ChunkId).Source ?? "memory";
            events.Add(new RetrievalEvent($"{source} - memory hit", hit.LogDetail, hit.Kind));
        }

        events.Add(RetrievalEvent.Returned(hits.Count, (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds));

        return new MemoryLookup(hits, BuildMatches(phrase, hits, unsureThresholdPercent), events);
    }

    /// <summary>
    /// The text behind a set of hits, best first, for the block that goes to
    /// the model when Memory is attached. Hits whose chunk is no longer loaded
    /// are dropped rather than returned empty: a heading over a blank passage
    /// tells the model there was material when there was not.
    /// </summary>
    public IReadOnlyList<MemoryPassage> Passages(IReadOnlyList<ScoredChunk> hits, int take = 4) =>
    [
        .. hits
            .Select(hit => _chunks.FirstOrDefault(c => c.ChunkId == hit.ChunkId))
            .Where(chunk => !string.IsNullOrWhiteSpace(chunk.Text))
            .Take(take)
            .Select(chunk => new MemoryPassage(chunk.Source ?? "memory", chunk.Text))
    ];

    /// <summary>
    /// Turns hits into the terms that would be marked in a result. A term is
    /// only offered when the indexed wording actually contains it, so nothing
    /// is invented.
    /// </summary>
    private List<MemoryMatch> BuildMatches(string phrase, IReadOnlyList<ScoredChunk> hits, int threshold)
    {
        var matches = new List<MemoryMatch>();
        var seen = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);

        var queryTerms = Bm25Index.Tokenize(phrase);
        if (queryTerms.Count == 0 || hits.Count == 0)
        {
            return matches;
        }

        // The best hit's score sets the ceiling, so confidence is relative to
        // what this lookup actually found rather than an absolute scale.
        var best = hits.Max(h => h.Score);

        foreach (var hit in hits)
        {
            var chunk = _chunks.FirstOrDefault(c => c.ChunkId == hit.ChunkId);
            if (chunk.Text is null)
            {
                continue;
            }

            foreach (var term in queryTerms)
            {
                if (!seen.Add(term))
                {
                    continue;
                }

                var start = phrase.IndexOf(term, StringComparison.CurrentCultureIgnoreCase);
                if (start < 0 || !chunk.Text.Contains(term, StringComparison.CurrentCultureIgnoreCase))
                {
                    continue;
                }

                var confidence = best <= 0 ? 0 : (int)Math.Round(Math.Clamp(hit.Score / best, 0, 1) * 100);

                matches.Add(new MemoryMatch
                {
                    Term = phrase.Substring(start, term.Length),
                    Start = start,
                    Confidence = confidence,
                    Origin = hit.Kind == RetrievalKind.Fused ? "From term pairs" : "From project files",
                    Reason = $"Found in {chunk.Source}.",
                    Alternatives = [],
                });
            }
        }

        _ = threshold;
        return [.. matches.OrderBy(m => m.Start)];
    }
}
