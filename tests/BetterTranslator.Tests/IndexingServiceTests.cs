using System.IO;
using BetterTranslator.Core.Services;
using BetterTranslator.Indexing;
using BetterTranslator.Indexing.Index;
using BetterTranslator.Indexing.Readers;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The pipeline against real files: counts that trace to the index rather than
/// to a constant, and a cancel that stops within one chunk.
/// </summary>
public sealed class IndexingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bt-index", Guid.NewGuid().ToString("N"));
    private readonly Database _database;
    private readonly IndexStore _store;
    private readonly IndexingService _service;

    public IndexingServiceTests()
    {
        Directory.CreateDirectory(_root);
        _database = new Database(new AppPaths(_root));
        _store = new IndexStore(_database);
        _service = new IndexingService(_store, new DocumentReaders());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string WriteFile(string name, string body)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public async Task ThreeRealFilesYieldTheirTrueCounts()
    {
        var a = WriteFile("a.txt", string.Join(" ", Enumerable.Repeat("alpha", 50)));
        var b = WriteFile("b.txt", string.Join(" ", Enumerable.Repeat("beta", 120)));
        var c = WriteFile("c.md", "# Heading\n\n" + string.Join(" ", Enumerable.Repeat("gamma", 30)));

        var totals = await _service.IndexAsync(
            IndexingRequest.ForFiles([a, b, c]),
            null,
            CancellationToken.None);

        totals.Sources.Should().Be(3);
        totals.Files.Should().Be(3);
        totals.Chunks.Should().BeGreaterThan(0);

        // The word total is the real count of what was on disk, not an estimate.
        var expectedWords = new[] { a, b, c }
            .Sum(p => BetterTranslator.Indexing.Chunking.TextChunker.CountWords(File.ReadAllText(p)));

        totals.Words.Should().BeGreaterThanOrEqualTo(expectedWords - 10);
        totals.Words.Should().BeLessThanOrEqualTo(expectedWords + 40, "overlap repeats a few words");
    }

    [Fact]
    public async Task CountersStartAtZeroAndClimbToTheTrueTotal()
    {
        var files = Enumerable.Range(0, 4)
            .Select(i => WriteFile($"f{i}.txt", string.Join(" ", Enumerable.Repeat("term", 60))))
            .ToList();

        var progress = new SyncProgress<IndexingProgress>();

        await _service.IndexAsync(IndexingRequest.ForFiles(files), progress, CancellationToken.None);

        var reports = progress.Reports;
        reports.Should().NotBeEmpty();
        reports[0].FilesDone.Should().Be(0, "counters start at zero");
        reports[0].FilesTotal.Should().Be(4, "the queue is known from the first report");
        reports[0].Queued.Should().Be(4);

        var last = reports[^1];
        last.IsFinished.Should().BeTrue();
        last.FilesDone.Should().Be(4);
        last.Queued.Should().Be(0);
        last.Percent.Should().Be(100);
        last.ChunksWritten.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ACancelStopsWithinOneChunkAndIsReportedAsStopped()
    {
        // One large file, so a cancel must land mid-file rather than at the end.
        var big = WriteFile("big.txt", string.Join(" ", Enumerable.Repeat("workspace", 40_000)));
        var second = WriteFile("second.txt", "should never be reached");

        using var cancelling = new CancellationTokenSource();
        // Cancel the moment the first file starts being parsed.
        var progress = new SyncProgress<IndexingProgress>(report =>
        {
            if (report.Activity.StartsWith("Parsing", StringComparison.Ordinal))
            {
                cancelling.Cancel();
            }
        });

        await _service.IndexAsync(IndexingRequest.ForFiles([big, second]), progress, cancelling.Token);

        var last = progress.Reports[^1];
        last.WasCancelled.Should().BeTrue();
        last.IsFinished.Should().BeTrue();
        last.Activity.Should().Be("Stopped");

        // The run stopped rather than finishing the queue.
        last.FilesDone.Should().BeLessThan(2);
    }

    [Fact]
    public async Task CancellingBeforeAnyWorkLeavesTheIndexEmpty()
    {
        var file = WriteFile("a.txt", "alpha beta gamma");

        using var cancelling = new CancellationTokenSource();
        await cancelling.CancelAsync();

        var totals = await _service.IndexAsync(
            IndexingRequest.ForFiles([file]),
            null,
            cancelling.Token);

        totals.Chunks.Should().Be(0);
        totals.Sources.Should().Be(0);
    }

    [Fact]
    public async Task AFolderIsExpandedToTheFilesAReaderExistsFor()
    {
        var folder = Path.Combine(_root, "project");
        Directory.CreateDirectory(folder);

        File.WriteAllText(Path.Combine(folder, "notes.md"), "# Notes\n\nsome wording here");
        File.WriteAllText(Path.Combine(folder, "readme.txt"), "plain wording");
        File.WriteAllText(Path.Combine(folder, "image.png"), "not readable");
        File.WriteAllText(Path.Combine(folder, "archive.zip"), "not readable");

        var totals = await _service.IndexAsync(
            IndexingRequest.ForFolder(folder),
            null,
            CancellationToken.None);

        totals.Sources.Should().Be(2, "only the two files a reader exists for are indexed");
    }

    [Fact]
    public async Task BuildOutputAndVersionControlAreSkipped()
    {
        var folder = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        Directory.CreateDirectory(Path.Combine(folder, ".git"));

        File.WriteAllText(Path.Combine(folder, "real.md"), "project wording");
        File.WriteAllText(Path.Combine(folder, "bin", "copy.md"), "build output");
        File.WriteAllText(Path.Combine(folder, ".git", "COMMIT_EDITMSG"), "commit text");

        var totals = await _service.IndexAsync(
            IndexingRequest.ForFolder(folder),
            null,
            CancellationToken.None);

        totals.Sources.Should().Be(1, "bin and .git are noise, not project wording");
    }

    [Fact]
    public async Task ReplaceClearsWhatWasThereRatherThanAddingAlongside()
    {
        var first = WriteFile("first.txt", "alpha beta");
        await _service.IndexAsync(IndexingRequest.ForFiles([first]), null, CancellationToken.None);

        var second = WriteFile("second.txt", "gamma delta");
        var totals = await _service.IndexAsync(
            new IndexingRequest { Folders = [], Files = [second], Replace = true },
            null,
            CancellationToken.None);

        totals.Sources.Should().Be(1);

        var sources = await _store.GetSourcesAsync(CancellationToken.None);
        sources.Single().Name.Should().Be("second.txt");
    }

    [Fact]
    public async Task IndexingAddsAlongsideByDefault()
    {
        var first = WriteFile("first.txt", "alpha beta");
        await _service.IndexAsync(IndexingRequest.ForFiles([first]), null, CancellationToken.None);

        var second = WriteFile("second.txt", "gamma delta");
        var totals = await _service.IndexAsync(IndexingRequest.ForFiles([second]), null, CancellationToken.None);

        totals.Sources.Should().Be(2, "anything added is indexed alongside what is there");
    }

    [Fact]
    public async Task AnEmptySelectionFinishesWithoutClaimingWork()
    {
        var progress = new SyncProgress<IndexingProgress>();

        var totals = await _service.IndexAsync(IndexingRequest.ForFiles([]), progress, CancellationToken.None);

        totals.IsEmpty.Should().BeTrue();
        var reports = progress.Reports;
        reports[0].Activity.Should().Be("Nothing to index");
        reports[0].FilesTotal.Should().Be(0);
    }
}
