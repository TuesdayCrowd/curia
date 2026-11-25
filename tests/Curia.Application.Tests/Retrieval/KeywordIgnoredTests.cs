using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain;
using Curia.Domain.Search;
using Curia.Domain.Primitives;
using Curia.Domain.Retrieval;
using Curia.Domain.Verification;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;
using Curia.Tests.Shared;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// Errata G81 (R9.72), plan KWC8: a page of <c>HybridSearch</c> carries the words the keyword channel ignored, and a query of only listed words is served by the vector channel alone.
///
/// <para>The oracle is what the scene puts in: the words each post holds, and the digests a stub <see cref="IVectorIndex"/> returns (the one the G64 probes use). <c>SearchPage.IgnoredTerms</c> does
/// not exist on the base, so it is read by name (as <c>ChannelMarkTests</c> reads <c>ByChannel</c>): the red is the assertion and not a compile error. The build must name it so.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeywordIgnoredTests
{
    private sealed class StubVectorIndex : IVectorIndex
    {
        public List<(string Digest, double Distance)> Matches { get; } = [];

        public Task<Result<IndexedVector>> UpsertAsync(string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IndexedVector>.Ok(new IndexedVector(digest, postId, sequence)));

        public Task<Result<ImmutableArray<VectorMatch>>> NearestAsync(Embedding query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ImmutableArray<VectorMatch>>.Ok([.. Matches.OrderBy(m => m.Distance).Select(m => new VectorMatch(m.Digest, "stub", 0, m.Distance))]));

        public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => Task.FromResult(Result<long>.Ok(0));

        public Task<Result<IReadOnlySet<string>>> IndexedDigestsAsync(EmbeddingModel model, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<IReadOnlySet<string>>.Ok(new HashSet<string>()));
    }

    private sealed record Harness(InMemoryEventStore Store, EmbeddingIndexer Indexer, StubVectorIndex Index, HybridSearch Search);

    private static Harness NewHarness()
    {
        var embedder = new HashedNGramEmbedder();
        var index = new StubVectorIndex();
        return new Harness(
            new InMemoryEventStore(Clock(), new PermissiveLifecycleAppendGate()),
            new EmbeddingIndexer(embedder, index),
            index,
            new HybridSearch(embedder, index, RetrievalFloors.Published, RetrievalRows.Hashed));
    }

    private static SearchQuery Query(string? text, string? board = null) =>
        new(text, board is null ? null : Curia.Application.Tests.Typed.Board(board), [], [], null, null, null, 25);

    private static async Task<SearchPage> SearchAsync(Harness h, SearchQuery query, CancellationToken ct)
    {
        var log = await LogAsync(h.Store, ct).ConfigureAwait(false);
        var fold = VerificationProjector.Fold(PostProjector.Fold(log), AgentStandingProjector.Fold(log), _ => true, DisputedPosts.None);
        return Require(await SearchOverAsync(h.Search, log, RetrievalSurface.RestSearch, query, fold, ct).ConfigureAwait(false));
    }

    private static Task<string> PutAsync(Harness h, string id, string body, CancellationToken ct) =>
        AcceptAsync(h.Store, h.Indexer, id, ct, author: "https://agents.example/" + id, title: "a note about " + id, body: body);

    /// <summary>The scene: <c>w</c> says <c>the</c> and <c>pool</c>, <c>t</c> says <c>the</c> alone, <c>m</c> says neither and is the stub's one neighbour.</summary>
    private static async Task<(Harness H, string M)> SceneAsync(CancellationToken ct)
    {
        var h = NewHarness();
        await PutAsync(h, "w", "the pool needs keepalive", ct).ConfigureAwait(false);
        await PutAsync(h, "t", "the weather and the garden", ct).ConfigureAwait(false);
        var m = await PutAsync(h, "m", "a striped animal grazing on a plain", ct).ConfigureAwait(false);
        h.Index.Matches.Add((m, 0.3));
        return (h, m);
    }

    // ----- reading what does not exist yet, by name -----

    private static string[]? Ignored(SearchPage page) =>
        page.GetType().GetProperty("IgnoredTerms", BindingFlags.Public | BindingFlags.Instance)?.GetValue(page) is System.Collections.IEnumerable words
            ? [.. words.Cast<string>()]
            : null;

    private static string Show(string[]? words) => words is null ? "no member" : "[" + string.Join(", ", words) + "]";

    private static string Ids(SearchPage page) => string.Join(" ", page.Results.Select(r => r.Post.PostId));

    /// <summary>
    /// KWC8. A query of only listed words (<c>the</c>) is answered by the vector channel alone: the stub's one neighbour <c>m</c> is the one result, marked <c>meaning</c>, the page counts
    /// {words 0, meaning 1, words_and_meaning 0}, and <c>IgnoredTerms</c> is <c>[the]</c>. The posts that say <c>the</c> (<c>w</c>, <c>t</c>) are not results: on the base they are, marked <c>words</c>.
    /// </summary>
    [Fact]
    public async Task KWC8_AnAllIgnoredQueryIsServedByMeaningAloneAndStatesWhatItIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, _) = await SceneAsync(ct);

        var page = await SearchAsync(h, Query("the", board: "postgres"), ct);

        Assert.True(Ids(page) == "m", $"Expected: results [m] (the vector channel's one neighbour)  Actual: [{Ids(page)}]");
        Assert.True(page.ByChannel == new ChannelCounts(0, 1, 0), $"Expected: by_channel {{words 0, meaning 1, words_and_meaning 0}}  Actual: {page.ByChannel}");
        Assert.True(Ignored(page)?.SequenceEqual(["the"]) == true, $"Expected: ignored [the]  Actual: {Show(Ignored(page))}");
    }

    /// <summary>KWC8. A query with a term among listed words ranks over the term and states what it ignored: <c>the pool</c> lists <c>w</c> alone by words (the post that only says <c>the</c> is not a result) and ignores <c>[the]</c>.</summary>
    [Fact]
    public async Task KWC8_ATermAmongListedWordsListsOnlyThePostsHoldingItAndStatesTheIgnoredWord()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, _) = await SceneAsync(ct);

        var page = await SearchAsync(h, Query("the pool", board: "postgres"), ct);

        Assert.True(!page.Results.Any(r => r.Post.PostId == "t"), $"Expected: no result for the post that only says \"the\"  Actual: results [{Ids(page)}]");
        Assert.True(Ignored(page)?.SequenceEqual(["the"]) == true, $"Expected: ignored [the]  Actual: {Show(Ignored(page))}");
    }

    /// <summary>KWC8. A listing (no text) and a query that ignored nothing both state an empty array, never an absent member: absent means a Forum that does not apply the list.</summary>
    [Fact]
    public async Task KWC8_AListingAndAQueryThatIgnoredNothingStateAnEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, _) = await SceneAsync(ct);

        var listing = await SearchAsync(h, Query(null, board: "postgres"), ct);
        var plain = await SearchAsync(h, Query("pool", board: "postgres"), ct);

        Assert.True(Ignored(listing)?.Length == 0, $"Expected: a listing states ignored []  Actual: {Show(Ignored(listing))}");
        Assert.True(Ignored(plain)?.Length == 0, $"Expected: \"pool\" states ignored []  Actual: {Show(Ignored(plain))}");
    }
}
