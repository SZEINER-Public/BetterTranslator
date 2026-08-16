using BetterTranslator.Indexing.Retrieval;
using FluentAssertions;
using Xunit;

namespace BetterTranslator.Tests;

/// <summary>
/// The two retrieval paths and the fusion that decides an edge's colour.
/// Lexical needs no model, so it is what makes a project searchable the moment
/// it is indexed.
/// </summary>
public sealed class RetrievalTests
{
    private static (Bm25Index Index, Guid Settings, Guid Sync, Guid Unrelated) BuildIndex()
    {
        var index = new Bm25Index();

        var settings = Guid.NewGuid();
        var sync = Guid.NewGuid();
        var unrelated = Guid.NewGuid();

        index.Add(settings, "Open Settings and pin the sidebar to your workspace.");
        index.Add(sync, "Bubble keeps your workspace in sync while you work.");
        index.Add(unrelated, "A locked file pauses the queue until you close it.");

        return (index, settings, sync, unrelated);
    }

    [Fact]
    public void LexicalSearchFindsTheChunkThatSharesTheTerm()
    {
        var (index, settings, _, _) = BuildIndex();

        var hits = index.Search("settings");

        hits.Should().NotBeEmpty();
        hits[0].ChunkId.Should().Be(settings);
        hits[0].Kind.Should().Be(RetrievalKind.Lexical);
    }

    [Fact]
    public void AChunkSharingNoTermIsNotReturned()
    {
        var (index, _, _, unrelated) = BuildIndex();

        index.Search("settings").Should().NotContain(h => h.ChunkId == unrelated);
    }

    [Fact]
    public void ATermInTwoChunksRanksBothWithTheCloserOneFirst()
    {
        var (index, settings, sync, _) = BuildIndex();

        var hits = index.Search("workspace");

        hits.Select(h => h.ChunkId).Should().Contain([settings, sync]);
    }

    [Fact]
    public void ARarerTermOutweighsACommonOne()
    {
        var index = new Bm25Index();
        var rare = Guid.NewGuid();
        var common = Guid.NewGuid();

        // "the" appears everywhere; "nastaveni" only once.
        index.Add(rare, "nastaveni the the the");
        index.Add(common, "the the the the");
        index.Add(Guid.NewGuid(), "the the the the the");

        var hits = index.Search("nastaveni the");

        hits[0].ChunkId.Should().Be(rare, "the rare term carries more weight");
    }

    [Fact]
    public void SearchingForNothingReturnsNothing()
    {
        var (index, _, _, _) = BuildIndex();

        index.Search("").Should().BeEmpty();
        index.Search("   ").Should().BeEmpty();
    }

    [Fact]
    public void AnEmptyIndexReturnsNothingRatherThanThrowing() =>
        new Bm25Index().Search("workspace").Should().BeEmpty();

    [Theory]
    [InlineData("Open Settings, pin it.", new[] { "open", "settings", "pin", "it" })]
    [InlineData("bubble sync --watch", new[] { "bubble", "sync", "watch" })]
    [InlineData("nastaveni a pripnete", new[] { "nastaveni", "a", "pripnete" })]
    public void TokenizingSplitsOnPunctuationAndLowercases(string text, string[] expected) =>
        Bm25Index.Tokenize(text).Should().Equal(expected);

    [Fact]
    public void AccentedLettersSurviveTokenizing() =>
        Bm25Index.Tokenize("pripněte panel").Should().Equal("pripněte", "panel");

    // ---- dense ----

    [Fact]
    public void CosineIsOneForIdenticalVectors() =>
        RetrievalEngine.Cosine([1f, 2f, 3f], [1f, 2f, 3f]).Should().BeApproximately(1.0, 0.0001);

    [Fact]
    public void CosineIsZeroForOrthogonalVectors() =>
        RetrievalEngine.Cosine([1f, 0f], [0f, 1f]).Should().BeApproximately(0.0, 0.0001);

    [Fact]
    public void CosineIgnoresMagnitude() =>
        RetrievalEngine.Cosine([1f, 1f], [10f, 10f]).Should().BeApproximately(1.0, 0.0001);

    [Theory]
    [InlineData(null)]
    [InlineData(new float[0])]
    public void AMissingVectorScoresZeroRatherThanNaN(float[]? vector) =>
        RetrievalEngine.Cosine([1f, 2f], vector).Should().Be(0);

    [Fact]
    public void MismatchedVectorLengthsScoreZero() =>
        RetrievalEngine.Cosine([1f, 2f, 3f], [1f, 2f]).Should().Be(0);

    [Fact]
    public void AZeroVectorScoresZeroRatherThanDividingByZero() =>
        RetrievalEngine.Cosine([0f, 0f], [1f, 1f]).Should().Be(0);

    [Fact]
    public void DenseRanksByVectorSimilarity()
    {
        var near = Guid.NewGuid();
        var far = Guid.NewGuid();

        var hits = RetrievalEngine.Dense(
            [1f, 0f],
            [(near, [0.99f, 0.1f]), (far, [0.1f, 0.99f])]);

        hits[0].ChunkId.Should().Be(near);
        hits[0].Kind.Should().Be(RetrievalKind.Dense);
    }

    [Fact]
    public void DenseReturnsNothingWithNoQueryVector() =>
        RetrievalEngine.Dense(null, [(Guid.NewGuid(), new[] { 1f })]).Should().BeEmpty();

    [Fact]
    public void ChunksWithNoVectorAreSkippedByDense()
    {
        // Until a model has embedded them, chunks carry a null vector.
        var hits = RetrievalEngine.Dense([1f, 0f], [(Guid.NewGuid(), null)]);

        hits.Should().BeEmpty();
    }

    // ---- fusion ----

    [Fact]
    public void AChunkBothPathsFoundIsMarkedFused()
    {
        var shared = Guid.NewGuid();

        var fused = RetrievalEngine.Fuse(
            [new ScoredChunk(shared, 0.9, RetrievalKind.Dense)],
            [new ScoredChunk(shared, 4.2, RetrievalKind.Lexical)]);

        fused.Should().ContainSingle();
        fused[0].Kind.Should().Be(RetrievalKind.Fused);
    }

    [Fact]
    public void AChunkOnlyOnePathFoundKeepsThatPathsKind()
    {
        var denseOnly = Guid.NewGuid();
        var lexicalOnly = Guid.NewGuid();

        var fused = RetrievalEngine.Fuse(
            [new ScoredChunk(denseOnly, 0.9, RetrievalKind.Dense)],
            [new ScoredChunk(lexicalOnly, 4.2, RetrievalKind.Lexical)]);

        fused.Single(h => h.ChunkId == denseOnly).Kind.Should().Be(RetrievalKind.Dense);
        fused.Single(h => h.ChunkId == lexicalOnly).Kind.Should().Be(RetrievalKind.Lexical);
    }

    [Fact]
    public void AChunkBothPathsRankedWellBeatsOneSinglePathTopHit()
    {
        var both = Guid.NewGuid();
        var denseTop = Guid.NewGuid();

        var fused = RetrievalEngine.Fuse(
            [new ScoredChunk(denseTop, 0.99, RetrievalKind.Dense), new ScoredChunk(both, 0.8, RetrievalKind.Dense)],
            [new ScoredChunk(both, 5.0, RetrievalKind.Lexical)]);

        fused[0].ChunkId.Should().Be(both, "agreement across both paths outweighs one path's top hit");
    }

    [Fact]
    public void FusionDegradesToLexicalAloneWhenThereAreNoEmbeddings()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var fused = RetrievalEngine.Fuse(
            [],
            [new ScoredChunk(a, 5.0, RetrievalKind.Lexical), new ScoredChunk(b, 2.0, RetrievalKind.Lexical)]);

        fused.Should().HaveCount(2);
        fused[0].ChunkId.Should().Be(a, "lexical order survives with no dense path");
        fused.Should().OnlyContain(h => h.Kind == RetrievalKind.Lexical);
    }

    [Fact]
    public void RanksAreAssignedFromOneAfterOrdering()
    {
        var fused = RetrievalEngine.Fuse(
            [new ScoredChunk(Guid.NewGuid(), 0.9, RetrievalKind.Dense)],
            [new ScoredChunk(Guid.NewGuid(), 4.2, RetrievalKind.Lexical)]);

        fused.Select(h => h.Rank).Should().Equal(1, 2);
    }

    [Fact]
    public void TheLogDetailNamesTheRankScoreAndPath()
    {
        var hit = new ScoredChunk(Guid.NewGuid(), 0.89, RetrievalKind.Lexical) { Rank = 1 };

        hit.LogDetail.Should().Be("query.hit - rank 1 - 0.89 - bm25");

        (hit with { Kind = RetrievalKind.Dense }).LogDetail.Should().Contain("dense");
        (hit with { Kind = RetrievalKind.Fused }).LogDetail.Should().Contain("fused");
    }

    [Fact]
    public void FusingNothingReturnsNothing() =>
        RetrievalEngine.Fuse([], []).Should().BeEmpty();
}
