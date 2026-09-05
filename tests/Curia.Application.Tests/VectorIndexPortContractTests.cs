using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// What every <see cref="IVectorIndex"/> promises, run against the in-memory adapter here and
/// against pgvector in <c>Curia.Infrastructure.Tests</c>. Two adapters that happen to agree are
/// not a contract; this is.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class VectorIndexPortContractTests
{
    protected abstract IVectorIndex CreateIndex();

    private static readonly EmbeddingModel ModelA = new("contract-a", "1", 3);
    private static readonly EmbeddingModel ModelB = new("contract-b", "1", 3);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    /// <summary>Unit vectors in three dimensions, so cosine distance is exactly 1 - dot.</summary>
    private static Embedding Unit(EmbeddingModel model, double x, double y, double z)
    {
        var norm = Math.Sqrt(x * x + y * y + z * z);
        return new Embedding(model, [(float)(x / norm), (float)(y / norm), (float)(z / norm)]);
    }

    [Fact]
    public async Task R9_4_NearestReturnsStoredVectorsNearestFirstWithCosineDistance()
    {
        var ct = TestContext.Current.CancellationToken;
        var index = CreateIndex();

        // Stored farthest first, so an adapter that returned insertion order -- or seq order, or
        // anything but distance -- comes back reversed. The first draft of this test stored them
        // nearest first and passed an adapter that ordered by seq alone.
        Require(await index.UpsertAsync("sha256:" + new string('c', 64), "post-c", 1, Unit(ModelA, 0, 0, 1), ct));
        Require(await index.UpsertAsync("sha256:" + new string('b', 64), "post-b", 2, Unit(ModelA, 0.6, 0.8, 0), ct));
        Require(await index.UpsertAsync("sha256:" + new string('a', 64), "post-a", 3, Unit(ModelA, 1, 0, 0), ct));

        var nearest = Require(await index.NearestAsync(Unit(ModelA, 1, 0, 0), 10, ct));

        Assert.Equal(["post-a", "post-b", "post-c"], nearest.Select(m => m.PostId));
        Assert.Equal(0.0, nearest[0].Distance, 1e-6);
        Assert.Equal(0.4, nearest[1].Distance, 1e-6);
        Assert.Equal(1.0, nearest[2].Distance, 1e-6);
        Assert.Equal(3, nearest[0].Sequence);

        // And from the other side, so the order is the query's, not the corpus's.
        Assert.Equal(["post-c", "post-b", "post-a"], Require(await index.NearestAsync(Unit(ModelA, 0, 0, 1), 10, ct)).Select(m => m.PostId));
    }

    [Fact]
    public async Task R9_4_TheLimitBoundsThePageAndIsValidated()
    {
        var ct = TestContext.Current.CancellationToken;
        var index = CreateIndex();
        for (var i = 0; i < 5; i++)
            Require(await index.UpsertAsync("sha256:" + new string((char)('a' + i), 64), $"post-{i}", i, Unit(ModelA, 1, i, 0), ct));

        Assert.Equal(2, Require(await index.NearestAsync(Unit(ModelA, 1, 0, 0), 2, ct)).Length);

        Assert.False((await index.NearestAsync(Unit(ModelA, 1, 0, 0), 0, ct)).TryGetValue(out _, out var tooSmall));
        Assert.Equal("curia/retrieval/limit-out-of-range", tooSmall!.Type);
    }

    [Fact]
    public async Task R9_5_ModelsNeverMix()
    {
        var ct = TestContext.Current.CancellationToken;
        var index = CreateIndex();
        var digest = "sha256:" + new string('d', 64);

        Require(await index.UpsertAsync(digest, "post-d", 1, Unit(ModelA, 1, 0, 0), ct));
        Require(await index.UpsertAsync(digest, "post-d", 1, Unit(ModelB, 0, 1, 0), ct));

        Assert.Single(Require(await index.NearestAsync(Unit(ModelA, 1, 0, 0), 10, ct)));
        Assert.Single(Require(await index.NearestAsync(Unit(ModelB, 1, 0, 0), 10, ct)));
        Assert.Equal(1, Require(await index.CountAsync(ModelA, ct)));
        Assert.Equal(1, Require(await index.CountAsync(ModelB, ct)));
        Assert.Equal(0, Require(await index.CountAsync(new EmbeddingModel("contract-a", "2", 3), ct)));
    }

    [Fact]
    public async Task R11_10_UpsertReplacesSoAReplayConvergesOnTheSameRows()
    {
        var ct = TestContext.Current.CancellationToken;
        var index = CreateIndex();
        var digest = "sha256:" + new string('e', 64);

        Require(await index.UpsertAsync(digest, "post-e", 7, Unit(ModelA, 1, 0, 0), ct));
        Require(await index.UpsertAsync(digest, "post-e", 7, Unit(ModelA, 0, 1, 0), ct));

        var nearest = Require(await index.NearestAsync(Unit(ModelA, 0, 1, 0), 10, ct));
        var only = Assert.Single(nearest);
        Assert.Equal(0.0, only.Distance, 1e-6);
        Assert.Equal(1, Require(await index.CountAsync(ModelA, ct)));
    }

    [Fact]
    public async Task R11_10_TheHighWaterMarkIsTheHighestIndexedSequencePerModel()
    {
        var ct = TestContext.Current.CancellationToken;
        var index = CreateIndex();

        Assert.Equal(0, Require(await index.MaxSequenceAsync(ModelA, ct)));
        Require(await index.UpsertAsync("sha256:" + new string('1', 64), "p1", 12, Unit(ModelA, 1, 0, 0), ct));
        Require(await index.UpsertAsync("sha256:" + new string('2', 64), "p2", 7, Unit(ModelA, 0, 1, 0), ct));
        Require(await index.UpsertAsync("sha256:" + new string('3', 64), "p3", 40, Unit(ModelB, 0, 1, 0), ct));

        Assert.Equal(12, Require(await index.MaxSequenceAsync(ModelA, ct)));
        Assert.Equal(40, Require(await index.MaxSequenceAsync(ModelB, ct)));
    }

    [Fact]
    public async Task TiesBreakBySequenceSoAPageIsStable()
    {
        var ct = TestContext.Current.CancellationToken;
        var index = CreateIndex();

        Require(await index.UpsertAsync("sha256:" + new string('f', 64), "later", 9, Unit(ModelA, 1, 0, 0), ct));
        Require(await index.UpsertAsync("sha256:" + new string('9', 64), "earlier", 4, Unit(ModelA, 1, 0, 0), ct));

        Assert.Equal(["earlier", "later"], Require(await index.NearestAsync(Unit(ModelA, 1, 0, 0), 10, ct)).Select(m => m.PostId));
    }
}

[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovery needs the concrete class public; every [Fact] is inherited, so the analyzer's test-class heuristic does not see it.")]
public sealed class InMemoryVectorIndexContractTests : VectorIndexPortContractTests
{
    protected override IVectorIndex CreateIndex() => new InMemory.InMemoryVectorIndex();
}
