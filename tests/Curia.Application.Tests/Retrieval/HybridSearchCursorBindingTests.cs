using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Retrieval;
using Curia.Domain.Search;
using Curia.Domain.Verification;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// R9.35 (errata G36) at the use case: a cursor belongs to the query that minted it, and a continuation under any other is refused before either
/// retrieval channel is asked.
///
/// <para><b>Oracles.</b> The refusal is the one the errata names (<see cref="PublishedSearchPage.MismatchType"/>); "before either channel" is the
/// fakes' own call counters, which wrap the embedder and the index and count what the use case asks of them; the walk a probe compares against is a
/// second walk at the same <c>limit</c>, and the seeded ids are the posts this fact appended. The page-size probe asserts first that the fixture
/// moves: a probe that passes for a list that does not move holds nothing.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class HybridSearchCursorBindingTests
{
    private const string Terms = "npgsql ECONNRESET";

    private sealed class CountingEmbedder(ITextEmbedder inner) : ITextEmbedder
    {
        internal int Calls { get; set; }

        public EmbeddingModel Model => inner.Model;

        public Result<Embedding> Embed(string text)
        {
            Calls++;
            return inner.Embed(text);
        }
    }

    private sealed class CountingIndex(IVectorIndex inner) : IVectorIndex
    {
        internal int Nearest { get; set; }

        public Task<Result<IndexedVector>> UpsertAsync(string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default) =>
            inner.UpsertAsync(digest, postId, sequence, embedding, cancellationToken);

        public Task<Result<ImmutableArray<VectorMatch>>> NearestAsync(Embedding query, int limit, CancellationToken cancellationToken = default)
        {
            Nearest++;
            return inner.NearestAsync(query, limit, cancellationToken);
        }

        public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => inner.CountAsync(model, cancellationToken);

        public Task<Result<long>> MaxSequenceAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => inner.MaxSequenceAsync(model, cancellationToken);
    }

    /// <summary>A store, and two searchers over the same corpus: one on the published floors and one configured at V1, each counting what it asks of its channels.</summary>
    private sealed class Harness
    {
        internal InMemoryEventStore Store { get; }

        internal EmbeddingIndexer Indexer { get; }

        internal CountingEmbedder Embedder { get; }

        internal CountingIndex Index { get; }

        internal HybridSearch Published { get; }

        internal HybridSearch AtV1 { get; }

        internal Harness()
        {
            var raw = new HashedNGramEmbedder();
            var inner = new InMemoryVectorIndex();
            Store = new InMemoryEventStore(Clock(), new PermissiveLifecycleAppendGate());
            Indexer = new EmbeddingIndexer(raw, inner);
            Embedder = new CountingEmbedder(raw);
            Index = new CountingIndex(inner);
            Published = new HybridSearch(Embedder, Index, RetrievalFloors.Published);
            AtV1 = new HybridSearch(Embedder, Index, Require(RetrievalFloors.Parse([new("rest-search", "V1")])));
        }

        internal void ResetCounters() => (Embedder.Calls, Index.Nearest) = (0, 0);
    }

    private static SearchQuery Query(
        string? text = Terms, string? board = "postgres", string? author = null, PostKind[]? kinds = null, string[]? tags = null,
        VerificationLevel? floor = null, int limit = 2, RetrievalCursor? cursor = null) =>
        new(text, board is null ? null : Typed.Board(board), [.. kinds ?? []], [.. tags ?? []], author is null ? null : Typed.Agent(author), floor, cursor, limit);

    private static async Task<Result<SearchPage>> RunAsync(Harness h, HybridSearch search, SearchQuery query, CancellationToken ct)
    {
        var log = await LogAsync(h.Store, ct).ConfigureAwait(false);
        var fold = VerificationProjector.Fold(PostProjector.Fold(log), AgentStandingProjector.Fold(log), _ => true, DisputedPosts.None);
        return await search.SearchAsync(log, RetrievalSurface.RestSearch, query, fold, ct).ConfigureAwait(false);
    }

    /// <summary>Four questions by four authors, tagged, so a page of two has a second page.</summary>
    private static async Task<Harness> FourQuestionsAsync(CancellationToken ct)
    {
        var h = new Harness();
        for (var i = 0; i < 4; i++)
            await AcceptAsync(h.Store, h.Indexer, $"p{i}", ct, title: $"{Terms} case {i}", body: $"variant {i} of the {Terms} question", tags: ["tls"], author: $"https://agents.example/a{i}").ConfigureAwait(false);
        await AcceptAsync(h.Store, h.Indexer, "ans", ct, kind: PostKind.Answer, title: null, body: $"{Terms}: set the keepalive", parent: "p0", author: "https://agents.example/helper").ConfigureAwait(false);
        return h;
    }

    public static TheoryData<string> Members() => ["q", "board", "author", "kind", "tags", "requested floor", "floor in force", "limit"];

    [Theory]
    [MemberData(nameof(Members))]
    public async Task R9_35_AContinuationUnderAnyBoundMemberIsRefusedBeforeEitherChannelIsAsked(string member)
    {
        var ct = TestContext.Current.CancellationToken;
        var h = await FourQuestionsAsync(ct);

        var first = Require(await RunAsync(h, h.Published, Query(), ct));
        Assert.NotNull(first.Next);

        var presented = Query(cursor: first.Next);
        (HybridSearch Search, SearchQuery Query) changed = member switch
        {
            "q" => (h.Published, presented with { Text = Terms + " keepalive" }),
            "board" => (h.Published, presented with { Board = Typed.Board("other") }),
            "author" => (h.Published, presented with { Author = Typed.Agent("https://agents.example/a1") }),
            "kind" => (h.Published, presented with { Kinds = [PostKind.Question] }),
            "tags" => (h.Published, presented with { Tags = ["tls"] }),
            "requested floor" => (h.Published, presented with { RequestedFloor = VerificationLevel.V1 }),
            "floor in force" => (h.AtV1, presented),
            "limit" => (h.Published, presented with { Limit = 3 }),
            _ => throw new ArgumentOutOfRangeException(nameof(member), member, "not a member this fact varies"),
        };

        h.ResetCounters();
        var refused = await RunAsync(h, changed.Search, changed.Query, ct);

        Assert.True(!refused.TryGetValue(out var page, out var error), $"expected a refusal under a changed {member}, got a page of {page?.Results.Length}");
        Assert.Equal(PublishedSearchPage.MismatchType(), error!.Type);
        Assert.Equal((0, 0), (h.Embedder.Calls, h.Index.Nearest));
    }

    /// <summary>Control for the theory: the identical query continues, and the counters show the channels were asked, so zero above means refused-first.</summary>
    [Fact]
    public async Task R9_35_TheIdenticalQueryContinuesAndAsksBothChannels()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = await FourQuestionsAsync(ct);
        var first = Require(await RunAsync(h, h.Published, Query(), ct));

        h.ResetCounters();
        var second = Require(await RunAsync(h, h.Published, Query(cursor: first.Next), ct));

        Assert.Equal(2, second.Results.Length);
        Assert.True(h.Embedder.Calls > 0 && h.Index.Nearest > 0, $"the control did not reach the channels (embed {h.Embedder.Calls}, nearest {h.Index.Nearest})");
    }

    /// <summary>One author with four posts and two others with one each, all matching one query.</summary>
    private static async Task<Harness> HeavyAuthorAsync(CancellationToken ct)
    {
        var h = new Harness();
        for (var i = 0; i < 4; i++)
            await AcceptAsync(h.Store, h.Indexer, $"heavy{i}", ct, title: $"{Terms} {Terms} heavy {i}", body: $"{Terms} heavy variant {i}", author: "https://agents.example/heavy").ConfigureAwait(false);
        await AcceptAsync(h.Store, h.Indexer, "quiet0", ct, title: "A question about pooling", body: $"{Terms} once", author: "https://agents.example/quiet0").ConfigureAwait(false);
        await AcceptAsync(h.Store, h.Indexer, "quiet1", ct, title: "A question about idling", body: $"{Terms} once more", author: "https://agents.example/quiet1").ConfigureAwait(false);
        return h;
    }

    private static async Task<string[]> WalkAsync(Harness h, int limit, CancellationToken ct)
    {
        var served = new List<string>();
        RetrievalCursor? cursor = null;
        do
        {
            var page = Require(await RunAsync(h, h.Published, Query(board: null, limit: limit, cursor: cursor), ct).ConfigureAwait(false));
            served.AddRange(page.Results.Select(r => r.Post.PostId));
            cursor = page.Next;
        }
        while (cursor is not null);
        return [.. served];
    }

    [Fact]
    public async Task R9_35_ALimitThatMovesTheListIsRefusedAndTheFixtureShowsItMoves()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = await HeavyAuthorAsync(ct);
        string[] seeded = ["heavy0", "heavy1", "heavy2", "heavy3", "quiet0", "quiet1"];

        // Non-vacuity first: the list an offset indexes depends on the page size, or this probe holds nothing.
        var atTwo = await WalkAsync(h, 2, ct);
        var atFour = await WalkAsync(h, 4, ct);
        Assert.True(!atTwo.SequenceEqual(atFour), $"the fixture's order does not move with the page size, so the probe below would pass for a list that does not move: {string.Join(",", atTwo)}");

        // Control: at one limit the walk serves every seeded post once, and a second walk agrees with it.
        Assert.Equal(seeded.Order(StringComparer.Ordinal), atTwo.Order(StringComparer.Ordinal));
        Assert.Equal(atTwo, await WalkAsync(h, 2, ct));

        // The probe: page 1 at two, its cursor presented at four.
        var first = Require(await RunAsync(h, h.Published, Query(board: null, limit: 2), ct));
        var presented = await RunAsync(h, h.Published, Query(board: null, limit: 4, cursor: first.Next), ct);

        var repeated = presented.TryGetValue(out var page, out _)
            ? page!.Results.Select(r => r.Post.PostId).Intersect(first.Results.Select(r => r.Post.PostId)).ToArray()
            : [];
        Assert.True(
            !presented.IsOk,
            $"expected a refusal for a cursor from limit 2 presented at limit 4, got a page {(repeated.Length > 0 ? "repeating " + string.Join(",", repeated.Select(r => $"\"{r}\"")) : $"of {page?.Results.Length}")}");
    }

    [Fact]
    public async Task R9_35_TheRefusalEchoesNoQueryValueAndNoCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = await FourQuestionsAsync(ct);
        var first = Require(await RunAsync(h, h.Published, Query(), ct));

        const string Marker = "zzmarkerqq";
        var refused = await RunAsync(h, h.Published, Query(text: $"{Terms} {Marker}", cursor: first.Next), ct);

        Assert.True(!refused.TryGetValue(out _, out var error), "expected an error under a changed q, got a page");
        var said = $"{error!.Type} {error.Title} {error.Detail}";
        Assert.DoesNotContain(Marker, said, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Next!.Encode(), said, StringComparison.Ordinal);
        Assert.DoesNotContain(first.Next.Binding, said, StringComparison.Ordinal);
    }
}
