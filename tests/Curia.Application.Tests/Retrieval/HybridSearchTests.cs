using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain.Content;
using Curia.Domain.Retrieval;
using Curia.Domain.Search;
using Curia.Domain.Verification;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class HybridSearchTests
{
    private sealed record Harness(InMemoryEventStore Store, EmbeddingIndexer Indexer, HybridSearch Search);

    private static Harness NewHarness(RetrievalFloors? floors = null)
    {
        var embedder = new HashedNGramEmbedder();
        var index = new InMemoryVectorIndex();
        return new Harness(new InMemoryEventStore(Clock()), new EmbeddingIndexer(embedder, index), new HybridSearch(embedder, index, floors ?? RetrievalFloors.Published));
    }

    private static SearchQuery Query(string? text, int limit = 25, RetrievalCursor? cursor = null, VerificationLevel? floor = null, PostKind? kind = null) =>
        new(text, null, kind is { } k ? [k] : [], [], null, floor, cursor, limit);

    private static async Task<SearchPage> SearchAsync(Harness h, SearchQuery query, CancellationToken ct, VerificationFold? verification = null)
    {
        var log = await LogAsync(h.Store, ct).ConfigureAwait(false);
        var fold = verification ?? VerificationProjector.Fold(PostProjector.Fold(log), AgentStandingProjector.Fold(log), _ => true);
        return Require(await h.Search.SearchAsync(log, RetrievalSurface.RestSearch, query, fold, ct).ConfigureAwait(false));
    }

    [Fact]
    public async Task R9_4_BothChannelsRankAndThePageSaysHowItWasMade()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        await AcceptAsync(h.Store, h.Indexer, "reset", ct, title: "ECONNRESET from npgsql", body: "npgsql throws ECONNRESET after the pooler idles the connection");
        await AcceptAsync(h.Store, h.Indexer, "pool", ct, title: "npgsql pooling defaults", body: "what are the pooler's idle timeouts");
        await AcceptAsync(h.Store, h.Indexer, "keys", ct, title: "rotating an ed25519 key", body: "how do I rotate a signing key without breaking heads");

        var page = await SearchAsync(h, Query("npgsql ECONNRESET"), ct);

        Assert.Equal("reset", page.Results[0].Post.PostId);
        Assert.True(page.Results[0].LexicalRank > 0 && page.Results[0].VectorRank > 0, "the top hit came through both channels");
        Assert.Equal(page.Results[0].LexicalTerm + page.Results[0].VectorTerm, page.Results[0].Fused, 1e-12);
        Assert.DoesNotContain(page.Results, r => r.Post.PostId == "keys" && r.LexicalRank > 0);
        Assert.Equal((RetrievalSurface.RestSearch, VerificationLevel.V0, "published"), (page.Surface, page.Floor, page.FloorSource));
        Assert.Equal(HashedNGramEmbedding.Model, page.Model);
        Assert.Equal((60, 200), (page.K, page.CandidateDepth));
    }

    [Fact]
    public async Task R10_2_ARequestedFloorRemovesUngradedAnswersAndKeepsQuestionsAndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        var question = await AcceptAsync(h.Store, h.Indexer, "q", ct, title: "ECONNRESET from npgsql", body: "npgsql ECONNRESET on idle");
        await AcceptAsync(h.Store, h.Indexer, "a", ct, kind: PostKind.Answer, title: null, body: "npgsql ECONNRESET: set the keepalive", parent: "q", author: "https://agents.example/helper");

        var open = await SearchAsync(h, Query("npgsql ECONNRESET"), ct);
        Assert.Equal(2, open.Results.Length);

        var gated = await SearchAsync(h, Query("npgsql ECONNRESET", floor: VerificationLevel.V1), ct);
        Assert.Equal(["q"], gated.Results.Select(r => r.Post.PostId));
        Assert.Equal((VerificationLevel.V1, "requested"), (gated.Floor, gated.FloorSource));
        _ = question;
    }

    [Fact]
    public async Task R10_2_AConfiguredFloorOverridesThePublishedOneAndSaysSo()
    {
        var ct = TestContext.Current.CancellationToken;
        var floors = Require(RetrievalFloors.Parse([new("rest-search", "V1")]));
        var h = NewHarness(floors);
        await AcceptAsync(h.Store, h.Indexer, "a", ct, kind: PostKind.Answer, title: null, body: "npgsql ECONNRESET: set the keepalive", parent: "q0", author: "https://agents.example/helper");

        var page = await SearchAsync(h, Query("npgsql ECONNRESET"), ct);
        Assert.Empty(page.Results);
        Assert.Equal((VerificationLevel.V1, "configured"), (page.Floor, page.FloorSource));

        Assert.False(RetrievalFloors.Parse([new("rest-search", "V-")]).TryGetValue(out _, out var badLevel));
        Assert.Equal("curia/search/not-a-floor", badLevel!.Type);
        Assert.False(RetrievalFloors.Parse([new("graphql", "V0")]).TryGetValue(out _, out var badSurface));
        Assert.Equal("curia/retrieval/unknown-surface", badSurface!.Type);
    }

    [Fact]
    public async Task Table13_AnAnswerAtV2OutranksAnUnverifiedOneWithTheSameWords()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        var unverified = await AcceptAsync(h.Store, h.Indexer, "a1", ct, kind: PostKind.Answer, title: null, body: "npgsql ECONNRESET: set the keepalive", parent: "q0", author: "https://agents.example/one");
        var reproduced = await AcceptAsync(h.Store, h.Indexer, "a2", ct, kind: PostKind.Answer, title: null, body: "npgsql ECONNRESET: set the keepalive", parent: "q0", author: "https://agents.example/two");

        // Same text, so fusion ties; a1 is earlier and would win on seq. Table 13's weight decides instead.
        var fold = new StubFold(reproduced, VerificationLevel.V2);
        var page = await SearchAsync(h, Query("npgsql ECONNRESET keepalive"), ct, fold.Fold);
        Assert.Equal(["a2", "a1"], page.Results.Select(r => r.Post.PostId));
        Assert.Equal((VerificationLevel.V2, 2.0), (page.Results[0].Level, page.Results[0].Weight));
        _ = unverified;
    }

    [Fact]
    public async Task R9_7_APostAppendedBetweenPagesNeitherShiftsNorRepeatsAResult()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        for (var i = 0; i < 5; i++)
            await AcceptAsync(h.Store, h.Indexer, $"p{i}", ct, title: $"npgsql ECONNRESET case {i}", body: $"variant {i} of the npgsql ECONNRESET question", author: $"https://agents.example/a{i}");

        var first = await SearchAsync(h, Query("npgsql ECONNRESET", limit: 2), ct);
        Assert.NotNull(first.Next);
        Assert.Equal(2, first.Results.Length);

        // A new, very relevant post lands between pages.
        await AcceptAsync(h.Store, h.Indexer, "late", ct, title: "npgsql ECONNRESET", body: "npgsql ECONNRESET npgsql ECONNRESET", author: "https://agents.example/late");

        var second = await SearchAsync(h, Query("npgsql ECONNRESET", limit: 2, cursor: first.Next), ct);
        var third = await SearchAsync(h, Query("npgsql ECONNRESET", limit: 2, cursor: second.Next), ct);
        var paged = first.Results.Concat(second.Results).Concat(third.Results).Select(r => r.Post.PostId).ToList();

        Assert.Equal(5, paged.Count);
        Assert.Equal(5, paged.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("late", paged);
        Assert.Null(third.Next);

        // A fresh query sees it, at the top.
        var fresh = await SearchAsync(h, Query("npgsql ECONNRESET", limit: 2), ct);
        Assert.Equal("late", fresh.Results[0].Post.PostId);
        Assert.True(fresh.CorpusBound > first.CorpusBound);
    }

    [Fact]
    public async Task R10_7_OneAuthorIsCappedAtHalfAPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        for (var i = 0; i < 3; i++)
            await AcceptAsync(h.Store, h.Indexer, $"loud{i}", ct, title: "npgsql ECONNRESET", body: "npgsql ECONNRESET again", author: "https://agents.example/loud");
        await AcceptAsync(h.Store, h.Indexer, "quiet", ct, title: "npgsql ECONNRESET", body: "npgsql ECONNRESET once", author: "https://agents.example/quiet");

        var page = await SearchAsync(h, Query("npgsql ECONNRESET", limit: 4), ct);
        Assert.Equal(4, page.Results.Length);
        Assert.Equal(2, page.Results.Take(3).Count(r => r.Post.Author.EndsWith("/loud", StringComparison.Ordinal)));
        Assert.Contains(page.Results, r => r.Deferred);
    }

    [Fact]
    public async Task R10_6_AnAnnotatedNearDuplicateIsDeferredBehindItsOriginalOnAPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        var original = await AcceptAsync(h.Store, h.Indexer, "orig", ct, title: "npgsql ECONNRESET", body: "npgsql ECONNRESET on idle", author: "https://agents.example/a");
        await AcceptAsync(h.Store, h.Indexer, "dupe", ct, title: "npgsql ECONNRESET", body: "npgsql ECONNRESET on idle", author: "https://agents.example/b", possibleDuplicateOf: original);
        await AcceptAsync(h.Store, h.Indexer, "other", ct, title: "npgsql ECONNRESET elsewhere", body: "a different npgsql ECONNRESET", author: "https://agents.example/c");

        var page = await SearchAsync(h, Query("npgsql ECONNRESET"), ct);
        Assert.Equal(["orig", "other", "dupe"], page.Results.Select(r => r.Post.PostId));
        Assert.True(page.Results[2].Deferred);
    }

    [Fact]
    public async Task R9_6_AFilterWithoutTextIsAListingInSeqOrderWithNoVectorChannel()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        await AcceptAsync(h.Store, h.Indexer, "one", ct, author: "https://agents.example/a");
        await AcceptAsync(h.Store, h.Indexer, "two", ct, author: "https://agents.example/b");

        var page = await SearchAsync(h, Query(null), ct);
        Assert.Equal(["one", "two"], page.Results.Select(r => r.Post.PostId));
        Assert.All(page.Results, r => Assert.Equal(0, r.VectorRank));
    }

    /// <summary>A fold whose only graded digest is the one named; every other digest is V0.</summary>
    private sealed class StubFold(string digest, VerificationLevel level)
    {
        public VerificationFold Fold { get; } = new(
            ImmutableDictionary<string, VerificationState>.Empty.WithComparers(StringComparer.Ordinal)
                .Add(digest, new VerificationState(digest, level, 2, [], [])),
            []);
    }
}
