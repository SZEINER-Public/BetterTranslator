using BetterTranslator.Engine.Corpus;
using BetterTranslator.Indexing.Chunking;
using BetterTranslator.Indexing.Index;
using BetterTranslator.Indexing.Readers;

namespace BetterTranslator.Indexing;

/// <summary>
/// What the indexer is doing right now. The progress line, the queued count and
/// the activity log all render from this, so they cannot disagree.
/// </summary>
public sealed record IndexingProgress
{
    /// <summary>For example "Parsing CHANGELOG.md".</summary>
    public required string Activity { get; init; }

    public int FilesDone { get; init; }

    public int FilesTotal { get; init; }

    public int ChunksWritten { get; init; }

    public int WordsRead { get; init; }

    /// <summary>Files still waiting, the figure beside the progress bar.</summary>
    public int Queued => Math.Max(0, FilesTotal - FilesDone);

    public double Fraction => FilesTotal <= 0 ? 0 : Math.Clamp((double)FilesDone / FilesTotal, 0, 1);

    public int Percent => (int)Math.Floor(Fraction * 100);

    public bool IsFinished { get; init; }

    public bool WasCancelled { get; init; }
}

/// <summary>
/// What to index. The scope travels in the request and is never held in mutable
/// shared state, so it cannot leak into a later pass.
/// </summary>
public sealed record IndexingRequest
{
    public required IReadOnlyList<string> Folders { get; init; }

    public required IReadOnlyList<string> Files { get; init; }

    public IReadOnlyList<string> Repositories { get; init; } = [];

    /// <summary>Replace what is already indexed rather than adding alongside it.</summary>
    public bool Replace { get; init; }

    public static IndexingRequest ForFiles(IEnumerable<string> files) =>
        new() { Folders = [], Files = [.. files] };

    public static IndexingRequest ForFolder(string folder) =>
        new() { Folders = [folder], Files = [] };
}

/// <summary>
/// Reads real files, chunks them and writes them to the index, reporting real
/// counts as it goes. Nothing here touches the UI thread, and the token is
/// checked between chunks so a cancel lands within one.
/// </summary>
public sealed class IndexingService(IndexStore store, DocumentReaders readers)
{
    /// <summary>
    /// Embeds a chunk, when a runtime is available. Null while none is, in
    /// which case chunks are stored with no vector and retrieval runs on the
    /// lexical path alone.
    /// </summary>
    public Func<string, CancellationToken, Task<float[]>>? Embed { get; set; }

    public async Task<IndexTotals> IndexAsync(
        IndexingRequest request,
        IProgress<IndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        // A token that is already cancelled reports Stopped like any other
        // cancel, rather than throwing out of the setup and leaving the caller
        // to handle two different shapes of the same outcome.
        if (cancellationToken.IsCancellationRequested)
        {
            progress?.Report(new IndexingProgress
            {
                Activity = "Stopped",
                IsFinished = true,
                WasCancelled = true,
            });

            return IndexTotals.Empty;
        }

        await store.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        if (request.Replace)
        {
            await store.ClearAsync(cancellationToken).ConfigureAwait(false);
        }

        // Resolve the whole work list first, so the queued count is real from
        // the first report rather than climbing as directories are walked.
        var work = ResolveWork(request);

        var filesDone = 0;
        var chunksWritten = 0;
        var wordsRead = 0;

        progress?.Report(new IndexingProgress
        {
            Activity = work.Count == 0 ? "Nothing to index" : "Starting",
            FilesTotal = work.Count,
        });

        foreach (var item in work)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                progress?.Report(new IndexingProgress
                {
                    Activity = "Stopped",
                    FilesDone = filesDone,
                    FilesTotal = work.Count,
                    ChunksWritten = chunksWritten,
                    WordsRead = wordsRead,
                    IsFinished = true,
                    WasCancelled = true,
                });

                return await store.GetTotalsAsync(CancellationToken.None).ConfigureAwait(false);
            }

            progress?.Report(new IndexingProgress
            {
                Activity = "Parsing " + Path.GetFileName(item.Path),
                FilesDone = filesDone,
                FilesTotal = work.Count,
                ChunksWritten = chunksWritten,
                WordsRead = wordsRead,
            });

            try
            {
                var content = await readers.ReadAsync(item.Path, cancellationToken).ConfigureAwait(false);

                // A scanned PDF and a DOCX whose body is one embedded image both
                // return a string rather than failing, and indexing that string
                // fills the store with chunks that match nothing and embeddings
                // that pull real queries towards noise. Said out loud, because
                // "0 chunks" with no reason reads as a bug in the indexer.
                //
                // Only the extracted formats are gated. A .txt or .md file's
                // content is its bytes -- there is no extraction step to fail,
                // so a fifteen-character note is short, not broken, and refusing
                // it would lose a real document to catch nothing.
                if (content.Format is DocumentKind.Pdf or DocumentKind.Word
                    && ExtractedText.RejectionReason(content.Text) is { } reason)
                {
                    progress?.Report(new IndexingProgress
                    {
                        Activity = $"Skipped {Path.GetFileName(item.Path)}: {reason}",
                        FilesDone = filesDone,
                        FilesTotal = work.Count,
                        ChunksWritten = chunksWritten,
                        WordsRead = wordsRead,
                    });

                    filesDone++;
                    continue;
                }

                var chunks = TextChunker.Chunk(content.Text);

                var source = new IndexedSource
                {
                    Id = Guid.NewGuid(),
                    Name = item.DisplayName,
                    Location = item.Path,
                    Kind = item.Kind,
                    FileCount = 1,
                    ChunkCount = chunks.Count,
                    WordCount = chunks.Sum(c => c.WordCount),
                    IndexedAt = DateTimeOffset.Now,
                    ContentHash = ContentHash.Of(content.Text),
                };

                await store.AddSourceAsync(source, cancellationToken).ConfigureAwait(false);

                var indexed = new List<IndexedChunk>(chunks.Count);
                foreach (var chunk in chunks)
                {
                    // Checked per chunk: a cancel stops within one, not at the
                    // end of a large file.
                    cancellationToken.ThrowIfCancellationRequested();

                    // Embedding is optional: with no runtime the chunk is
                    // stored without a vector and stays findable lexically.
                    float[]? vector = null;
                    if (Embed is not null)
                    {
                        try
                        {
                            vector = await Embed(chunk.Text, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            // A runtime that fails mid-run must not abandon the
                            // index; the chunk keeps its text.
                            vector = null;
                        }
                    }

                    indexed.Add(new IndexedChunk
                    {
                        Id = Guid.NewGuid(),
                        SourceId = source.Id,
                        Ordinal = chunk.Ordinal,
                        Text = chunk.Text,
                        WordCount = chunk.WordCount,
                        FilePath = item.Path,
                        Vector = vector,
                    });
                }

                await store.AddChunksAsync(indexed, cancellationToken).ConfigureAwait(false);

                chunksWritten += chunks.Count;
                wordsRead += source.WordCount;
            }
            catch (OperationCanceledException)
            {
                progress?.Report(new IndexingProgress
                {
                    Activity = "Stopped",
                    FilesDone = filesDone,
                    FilesTotal = work.Count,
                    ChunksWritten = chunksWritten,
                    WordsRead = wordsRead,
                    IsFinished = true,
                    WasCancelled = true,
                });

                return await store.GetTotalsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException)
            {
                // One unreadable file must not abandon the whole run.
                progress?.Report(new IndexingProgress
                {
                    Activity = $"Skipped {Path.GetFileName(item.Path)}: {ex.Message}",
                    FilesDone = filesDone,
                    FilesTotal = work.Count,
                    ChunksWritten = chunksWritten,
                    WordsRead = wordsRead,
                });
            }

            filesDone++;
        }

        var totals = await store.GetTotalsAsync(cancellationToken).ConfigureAwait(false);

        progress?.Report(new IndexingProgress
        {
            Activity = "Everything in this project is indexed",
            FilesDone = filesDone,
            FilesTotal = work.Count,
            ChunksWritten = chunksWritten,
            WordsRead = wordsRead,
            IsFinished = true,
        });

        return totals;
    }

    /// <summary>
    /// Expands folders and repositories into the files a reader exists for.
    /// </summary>
    private List<WorkItem> ResolveWork(IndexingRequest request)
    {
        var work = new List<WorkItem>();

        foreach (var file in request.Files)
        {
            if (File.Exists(file) && readers.IsSupported(file))
            {
                work.Add(new WorkItem(file, Path.GetFileName(file), SourceKind.File));
            }
        }

        foreach (var folder in request.Folders.Concat(request.Repositories))
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var kind = request.Repositories.Contains(folder) ? SourceKind.Repository : SourceKind.Folder;

            foreach (var file in EnumerateReadable(folder))
            {
                work.Add(new WorkItem(file, Path.GetRelativePath(folder, file), kind));
            }
        }

        return work;
    }

    /// <summary>
    /// Build output, dependency trees and version control are noise, not project
    /// wording. Written as globs rather than a substring test because
    /// <c>**/bin/**</c> excludes a bin folder at any depth INCLUDING the root,
    /// which a check for a slash-delimited segment gets wrong at the top level.
    /// </summary>
    public static readonly string[] DefaultExcludes =
    [
        "**/.git/**", "**/bin/**", "**/obj/**", "**/node_modules/**",
        "**/vendor/**", "**/.vs/**", "**/packages/**",
    ];

    private IEnumerable<string> EnumerateReadable(string folder) =>
        CorpusSelector
            // Types are left to the readers: they are the authority on what can
            // be opened, and naming the extensions twice would let the two lists
            // drift apart.
            .Select(folder, include: null, exclude: DefaultExcludes, types: [])
            .Select(f => f.FullPath)
            .Where(readers.IsSupported);


    private sealed record WorkItem(string Path, string DisplayName, SourceKind Kind);
}
