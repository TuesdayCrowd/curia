using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Retrieval;
using Curia.Domain.Search;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// D10b's table of numbers keyed by model id (plan ruling 1, errata G66, R11.102: an id with no row refuses nothing and removes nothing). Each probe calls the table and the types that read it directly; the check
/// under a model with no row, the floor, and the search floor block are the tail of NDT8.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NearDuplicateModelRowTests
{
    private const string BgeId = "bge-small-en-v1.5@1";

    /// <summary>NDT8. <c>RetrievalRows.For(modelId)</c> for an id with no row answers "none" (null).</summary>
    [Fact]
    public void R11_102_NDT8_AModelWithNoRowGetsNoThresholds()
    {
        var none = RetrievalRows.For("no-such-model@1");
        Assert.True(none is null, "Expected: RetrievalRows.For of an id with no row answers none  Actual: a row");
    }

    /// <summary>NDT8b, the control. The hashed-era numbers, exactly: 0.94 refusing, 0.5 overlap, 0.85 annotating, and the floor 0.2. Green on the tree.</summary>
    [Fact]
    public void R8_18_NDT8b_TheHashedRowIsTheHashedEraNumbersExactly()
    {
        var row = RetrievalRows.For("hashed-ngram@1")!;
        Assert.Equal(0.94, row.Thresholds.RefuseCosine);
        Assert.Equal(0.5, row.Thresholds.RefuseOverlap);
        Assert.Equal(0.85, row.Thresholds.AnnotateCosine);
        Assert.Equal(0.2, row.Floor);
        Assert.False(row.ThresholdsDerived);
        Assert.False(row.FloorDerived);
    }

    /// <summary>
    /// NDT8c (Amendment 4 ruling 3). The bge row's thresholds are the carried literals 9400, 5000 and 8500 (a silent drift to the Derivation's 8800 breaks the last), they are not derived, the floor is derived and is
    /// 6400, and a check on that model applies them: a pair at cosine 0.95 with overlap 0.6 is refused and one at 0.86 is annotated.
    /// </summary>
    [Fact]
    public async Task R8_91_NDT8c_TheBgeRowCarriesTheHashedEraThresholdsAndTheDerivedFloor()
    {
        var row = RetrievalRows.For(BgeId)!;
        Assert.Equal((9400, 5000, 8500), (row.RefuseCosineBp, row.RefuseOverlapBp, row.AnnotateCosineBp));
        Assert.False(row.ThresholdsDerived);
        Assert.True(row.FloorDerived);
        Assert.Equal(6400, row.FloorBp);

        var ct = TestContext.Current.CancellationToken;
        var refused = await AssessAsync(new DuplicateCheck(new FixedEmbedder(BgeId), new FixedIndex(0.95), row.Thresholds), ct).ConfigureAwait(true);
        Assert.Equal(DuplicateVerdict.Refuse, refused.Verdict);

        var annotated = await AssessAsync(new DuplicateCheck(new FixedEmbedder(BgeId), new FixedIndex(0.86), row.Thresholds), ct).ConfigureAwait(true);
        Assert.Equal(DuplicateVerdict.Annotate, annotated.Verdict);

        var none = await AssessAsync(new DuplicateCheck(new FixedEmbedder(BgeId), new FixedIndex(0.80), row.Thresholds), ct).ConfigureAwait(true);
        Assert.Equal(DuplicateVerdict.None, none.Verdict);
    }

    /// <summary>NDT8, the tail. A check with no row refuses and annotates nothing, even over a pair at cosine 1.0 with every word shared.</summary>
    [Fact]
    public async Task R11_102_NDT8_ACheckWithNoRowRefusesAndAnnotatesNothing()
    {
        var assessed = await AssessAsync(new DuplicateCheck(new FixedEmbedder("no-such-model@1"), new FixedIndex(1.0), thresholds: null), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Assert.Equal(DuplicateVerdict.None, assessed.Verdict);
        Assert.Null(assessed.Nearest);
        Assert.Null(assessed.Thresholds);
    }

    /// <summary>NDT8, the tail. A search with no row has no floor: a neighbour at cosine 0.05 is a candidate and the page says it removed nothing; with the bge row's floor it is removed and the page publishes 6400 bp.</summary>
    [Fact]
    public async Task R11_102_NDT8_ASearchWithNoRowRemovesNothingAndARowRemovesBelowItsFloor()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(Clock(), new PermissiveLifecycleAppendGate());
        var digest = await AcceptAsync(store, new EmbeddingIndexer(new HashedNGramEmbedder(), new InMemoryVectorIndex()), "far", ct, title: "alpha", body: "unrelated words entirely").ConfigureAwait(true);
        var log = await LogAsync(store, ct).ConfigureAwait(true);
        var query = new SearchQuery("zzz", null, [], [], null, null, null, 25);
        var fold = VerificationProjector.Fold(PostProjector.Fold(log), AgentStandingProjector.Fold(log), _ => true, DisputedPosts.None);

        async Task<SearchPage> PageAsync(RetrievalRow? row) =>
            Require(await SearchOverAsync(new HybridSearch(new FixedEmbedder(BgeId), new FixedIndex(0.05, digest), RetrievalFloors.Published, row), log, RetrievalSurface.RestSearch, query, fold, ct).ConfigureAwait(true));

        var open = await PageAsync(null).ConfigureAwait(true);
        Assert.Equal(-1.0, open.MinimumCosine);
        Assert.Contains(open.Results, r => r.Post.PostId == "far" && r.VectorRank > 0);

        var floored = await PageAsync(RetrievalRows.Bge).ConfigureAwait(true);
        Assert.Equal(0.64, floored.MinimumCosine);
        Assert.DoesNotContain(floored.Results, r => r.VectorRank > 0);
    }

    private static async Task<DuplicateAssessment> AssessAsync(DuplicateCheck check, CancellationToken ct)
    {
        // a stored question and a new one of four words each, three shared: Jaccard 3 of 5 = 0.6 over the search tokenizer's terms
        var stored = new SearchablePost("stored", "sha256:stored", "postgres", PostKind.Question, "alpha bravo charlie delta", "", [], "https://agents.example/other", 1);
        var json = Canonical(PostKind.Question, "https://agents.example/asker", "postgres", "alpha bravo charlie echo", "", []);
        var envelope = Require(PostEnvelope.Read((Curia.Canon.Json.JsonValue.Object)Require(Curia.Canon.Json.JsonReader.ParseUnrestricted(System.Text.Encoding.UTF8.GetBytes(json)))));
        return Require(await AssessOverAsync(check, [stored], envelope, ct).ConfigureAwait(false));
    }

    /// <summary>An embedder that answers one fixed vector under the named model.</summary>
    private sealed class FixedEmbedder(string modelId) : ITextEmbedder
    {
        public EmbeddingModel Model { get; } = new(modelId[..modelId.IndexOf('@', StringComparison.Ordinal)], modelId[(modelId.IndexOf('@', StringComparison.Ordinal) + 1)..], 2);

        public Task<Result<Embedding>> EmbedAsync(string text, EmbeddingRole role, CancellationToken cancellationToken) =>
            Task.FromResult(Result<Embedding>.Ok(new Embedding(Model, [1f, 0f])));
    }

    /// <summary>An index whose one neighbour, the stored post, sits at the cosine it was built with.</summary>
    private sealed class FixedIndex(double cosine, string digest = "sha256:stored") : IVectorIndex
    {
        public Task<Result<IndexedVector>> UpsertAsync(string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Result<ImmutableArray<VectorMatch>>> NearestAsync(Embedding query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ImmutableArray<VectorMatch>>.Ok([new VectorMatch(digest, "far", 1, 1.0 - cosine)]));

        public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => Task.FromResult(Result<long>.Ok(1));

        public Task<Result<IReadOnlySet<string>>> IndexedDigestsAsync(EmbeddingModel model, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlySet<string>>.Ok(new HashSet<string>()));
    }
}
