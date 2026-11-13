using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// Errata G65 (D10a), Amendment 3 ruling 50 (SEM56), model-free: a reconcile under one model over an index that holds another model's rows reads the digests of its own model. Task 6's FSEM22 pointed
/// <c>IndexedDigestsAsync</c> at the hashed model's id and every probe stayed green, because SEM26 asks the index for its digests directly and never runs a reconcile through
/// <see cref="EmbeddingIndexer"/>, and every other reconcile probe runs the hashed embedder, whose id is the one the mutant reads.
///
/// <para><b>Oracle.</b> The digests the log holds are the ones <c>AcceptAsync</c> returns when it appends them, not the ones the indexer reports; the rows a model holds are read
/// from the index per model, not from the indexer's return value.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReconcileUnderOtherModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly EmbeddingModel Other = new("other-test-model", "1", 4);

    /// <summary>A model that is not the hashed one: its own id, four dimensions, a vector that is the same for every text.</summary>
    private sealed class OtherModelEmbedder : ITextEmbedder
    {
        public EmbeddingModel Model => Other;

        public Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding>.Ok(new Embedding(Other, [1f, 0f, 0f, 0f])));
    }

    /// <summary>
    /// SEM56. A log of two discussion posts, both indexed under <c>hashed-ngram@1</c> in one index. Under another model the index holds neither: <c>CountAbsentAsync</c> says 2, a reconcile
    /// stores 2 vectors and the other model's rows are exactly the log's two digests; the hashed model's two rows are untouched (the old rows are kept, R11.10); a second reconcile and a second
    /// count find nothing absent. Predicted red on FSEM22, FSEM53 and FSEM54: <c>CountAbsentAsync under other-test-model@1 said 0, expected 2</c> (FSEM22, FSEM54) and <c>a reconcile under
    /// other-test-model@1 stored 0 vectors, expected 2</c> (FSEM22, FSEM53). Green on the tree as it stands.
    /// </summary>
    [Fact]
    public async Task R11_10_SEM56_AReconcileUnderAnotherModelIndexesTheLogIntoItAndLeavesTheFirstModelsRows()
    {
        var store = new InMemoryEventStore(Clock(), new PermissiveLifecycleAppendGate());
        var index = new InMemoryVectorIndex();
        var hashed = new EmbeddingIndexer(new HashedNGramEmbedder(), index);
        var digests = new[]
        {
            await AcceptAsync(store, hashed, "reconcile-1", Ct, title: "About reconcile one", body: "the first post, about quokka marsupials"),
            await AcceptAsync(store, hashed, "reconcile-2", Ct, title: "About reconcile two", body: "the second post, about a different animal altogether"),
        };
        Assert.Equal(2, digests.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2L, Require(await index.CountAsync(HashedNGramEmbedding.Model, Ct)));

        var other = new EmbeddingIndexer(new OtherModelEmbedder(), index);
        Assert.Equal(0L, Require(await index.CountAsync(Other, Ct)));
        var log = await LogAsync(store, Ct);

        var absent = Require(await other.CountAbsentAsync(store, Ct));
        Assert.True(absent == 2, $"CountAbsentAsync under {Other.Id} said {absent}, expected 2: the index holds both posts under {HashedNGramEmbedding.Model.Id} and neither under {Other.Id}");

        var stored = Require(await other.ReconcileAsync(store, Ct));
        Assert.True(stored == 2, $"a reconcile under {Other.Id} stored {stored} vectors, expected 2");

        var underOther = Require(await index.IndexedDigestsAsync(Other, Ct));
        Assert.True(digests.ToHashSet(StringComparer.Ordinal).SetEquals(underOther), $"the digests held under {Other.Id} are not the log's two");
        var underHashed = Require(await index.IndexedDigestsAsync(HashedNGramEmbedding.Model, Ct));
        Assert.True(digests.ToHashSet(StringComparer.Ordinal).SetEquals(underHashed), $"the digests held under {HashedNGramEmbedding.Model.Id} changed");
        Assert.Equal((2L, 2L), (Require(await index.CountAsync(HashedNGramEmbedding.Model, Ct)), Require(await index.CountAsync(Other, Ct))));

        Assert.Equal(0L, Require(await other.ReconcileAsync(store, Ct)));
        Assert.Equal(0L, Require(await other.CountAbsentAsync(store, Ct)));
        Assert.Equal(0L, Require(await hashed.CountAbsentAsync(store, Ct)));
        Assert.Equal(2, log.Count);
    }
}
