using System.IO;
using BetterTranslator.Core.Services;
using BetterTranslator.Indexing;
using BetterTranslator.Indexing.Index;
using BetterTranslator.Indexing.Readers;
using BetterTranslator.Indexing.Retrieval;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// A lookup against real indexed content, with no model involved. This is what
/// makes the underlines, the popover and the activity log render real data
/// before a runtime exists.
/// </summary>
public sealed class MemoryServiceTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-memory", Guid.NewGuid().ToString("N"));
    private Database _database = null!;
    private IndexStore _store = null!;
    private IndexingService _service = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        _database = new Database(new AppPaths(_root));
        await _database.MigrateAsync(CancellationToken.None);

        _store = new IndexStore(_database);
        _service = new IndexingService(_store, new DocumentReaders());
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }

        return Task.CompletedTask;
    }

    private async Task<MemoryService> IndexAndLoadAsync(params (string Name, string Body)[] files)
    {
        var paths = files.Select(f =>
        {
            var path = Path.Combine(_root, f.Name);
            File.WriteAllText(path, f.Body);
            return path;
        }).ToList();

        await _service.IndexAsync(IndexingRequest.ForFiles(paths), null, CancellationToken.None);

        var sources = await _store.GetSourcesAsync(CancellationToken.None);
        var chunks = new List<IndexedChunk>();

        foreach (var source in sources)
        {
            chunks.AddRange(await _store.GetChunksAsync(source.Id, CancellationToken.None));
        }

        var memory = new MemoryService();
        memory.Load(chunks, id => sources.FirstOrDefault(s => s.Id == id)?.Name ?? "memory");
        return memory;
    }

    [Fact]
    public async Task ALookupFindsRealWordingWithNoModel()
    {
        var memory = await IndexAndLoadAsync(
            ("glossary.txt", "Open Settings and pin the sidebar to your workspace."),
            ("other.txt", "A locked file pauses the queue."));

        var lookup = memory.Lookup("workspace", queryVector: null, unsureThresholdPercent: 80);

        lookup.Hits.Should().NotBeEmpty("the lexical path needs no embeddings");
        lookup.Hits[0].Kind.Should().Be(RetrievalKind.Lexical);
    }

    [Fact]
    public async Task MatchesPointAtTermsThatAreActuallyInTheIndexedWording()
    {
        var memory = await IndexAndLoadAsync(
            ("glossary.txt", "Open Settings and pin the sidebar to your workspace."));

        var lookup = memory.Lookup("workspace sidebar", null, 80);

        lookup.Matches.Should().NotBeEmpty();
        lookup.Matches.Should().OnlyContain(m => m.Confidence >= 0 && m.Confidence <= 100);

        // Nothing is invented: every term marked appears in the phrase.
        foreach (var match in lookup.Matches)
        {
            "workspace sidebar".Should().Contain(match.Term);
        }
    }

    [Fact]
    public async Task ATermThatAppearsNowhereProducesNoMatch()
    {
        var memory = await IndexAndLoadAsync(("glossary.txt", "Open Settings and pin the sidebar."));

        var lookup = memory.Lookup("zzzzznotaword", null, 80);

        lookup.Hits.Should().BeEmpty();
        lookup.Matches.Should().BeEmpty();
    }

    [Fact]
    public async Task TheLogOpensWithTheQueryAndClosesWithWhatWasReturned()
    {
        var memory = await IndexAndLoadAsync(("glossary.txt", "Open Settings and pin the sidebar to your workspace."));

        var lookup = memory.Lookup("workspace", null, 80);

        lookup.Events.Should().NotBeEmpty();
        lookup.Events[0].Line.Should().StartWith("Query received");
        lookup.Events[^1].Line.Should().StartWith("Returned");
        lookup.Events[^1].Line.Should().Contain("ms");
    }

    [Fact]
    public async Task EveryHitGetsItsOwnLogRowNamingTheSource()
    {
        var memory = await IndexAndLoadAsync(("glossary.txt", "Open Settings and pin the sidebar to your workspace."));

        var lookup = memory.Lookup("workspace", null, 80);

        var hitRows = lookup.Events.Where(e => e.Line.Contains("memory hit")).ToList();
        hitRows.Should().HaveCount(lookup.Hits.Count);
        hitRows.Should().OnlyContain(e => e.Detail.StartsWith("query.hit"));

        // The source name, never an internal id.
        hitRows.Should().OnlyContain(e => e.Line.StartsWith("glossary.txt"));
    }

    [Fact]
    public async Task MatchesAreOrderedByWhereTheyAppear()
    {
        var memory = await IndexAndLoadAsync(("glossary.txt", "pin the sidebar to your workspace"));

        var lookup = memory.Lookup("sidebar workspace", null, 80);

        lookup.Matches.Select(m => m.Start).Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task ChunksAreStoredWithNoVectorWhenNoRuntimeIsAvailable()
    {
        await IndexAndLoadAsync(("a.txt", "alpha beta gamma"));

        var sources = await _store.GetSourcesAsync(CancellationToken.None);
        var chunks = await _store.GetChunksAsync(sources[0].Id, CancellationToken.None);

        chunks.Should().NotBeEmpty();
        chunks.Should().OnlyContain(c => c.Vector == null, "nothing has embedded them yet");
    }

    [Fact]
    public async Task TheEmbeddingHookPopulatesVectorsWhenARuntimeIsAvailable()
    {
        // Stands in for the runtime's embedding endpoint.
        _service.Embed = (text, _) => Task.FromResult(new[] { text.Length / 100f, 0.5f });

        var path = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(path, "alpha beta gamma");
        await _service.IndexAsync(IndexingRequest.ForFiles([path]), null, CancellationToken.None);

        var sources = await _store.GetSourcesAsync(CancellationToken.None);
        var chunks = await _store.GetChunksAsync(sources[0].Id, CancellationToken.None);

        chunks.Should().OnlyContain(c => c.Vector != null && c.Vector.Length == 2);
    }

    [Fact]
    public async Task ARuntimeThatFailsMidRunDoesNotAbandonTheIndex()
    {
        _service.Embed = (_, _) => throw new InvalidOperationException("runtime went away");

        var path = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(path, "alpha beta gamma");

        var totals = await _service.IndexAsync(IndexingRequest.ForFiles([path]), null, CancellationToken.None);

        totals.Chunks.Should().BeGreaterThan(0, "the text is still indexed and still findable lexically");
        totals.Embedded.Should().Be(0);
    }

    [Fact]
    public async Task DenseJoinsTheFusionOnceVectorsExist()
    {
        _service.Embed = (text, _) => Task.FromResult(
            text.Contains("workspace", StringComparison.OrdinalIgnoreCase) ? new[] { 1f, 0f } : [0f, 1f]);

        var path = Path.Combine(_root, "a.txt");
        await File.WriteAllTextAsync(path, "Open Settings and pin the sidebar to your workspace.");
        await _service.IndexAsync(IndexingRequest.ForFiles([path]), null, CancellationToken.None);

        var sources = await _store.GetSourcesAsync(CancellationToken.None);
        var chunks = await _store.GetChunksAsync(sources[0].Id, CancellationToken.None);

        var memory = new MemoryService();
        memory.Load(chunks, _ => "a.txt");

        var lookup = memory.Lookup("workspace", queryVector: [1f, 0f], unsureThresholdPercent: 80);

        lookup.Hits[0].Kind.Should().Be(RetrievalKind.Fused, "both paths found it");
    }
}
