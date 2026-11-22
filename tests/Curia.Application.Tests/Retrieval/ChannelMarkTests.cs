using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Retrieval;
using Curia.Application.Tests.InMemory;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Content;
using Curia.Domain.Retrieval;
using Curia.Domain.Search;
using Curia.Domain.Verification;
using Xunit;
using static Curia.Application.Tests.Retrieval.RetrievalTestLog;
using Curia.Tests.Shared;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// R9.67 (errata G64), PR 8's probes MBC1, MBC2, MBC3 and MBC3b: a result states which channel ranked it, and the page counts them over the results it serves.
///
/// <para><b>The oracle is what the test puts in.</b> Channel membership is set by the words a post holds (the keyword channel ranks a post that carries the query's term and no
/// other) and by the digests a stub <see cref="IVectorIndex"/> returns at the distances the test names (the vector channel ranks exactly those); it is never read from
/// <see cref="RankedPost.LexicalRank"/> or <see cref="RankedPost.VectorRank"/>. The stub is the independent oracle MBC4's pairing of two members of one derivation is not.</para>
///
/// <para><b>The members do not exist on the base, so they are read by name.</b> A probe that referred to <c>RankedPost.MatchedBy</c> or <c>SearchPage.ByChannel</c> would be red by
/// a compile error and not by its own assertion. They are read by reflection: <c>MatchedBy</c> on a result and <c>ByChannel</c> (with <c>Words</c>, <c>Meaning</c>,
/// <c>WordsAndMeaning</c>) on the page, the wire's <c>matched_by</c> and <c>by_channel</c> in Pascal case; an enum or a string is read by its spelling with the underscores and case
/// removed. The build must name them so; a different home for them is a change to these helpers and nothing else.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ChannelMarkTests
{
    private const string Term = "zebra";

    /// <summary>A vector index that answers with the matches the test names, nearest first, whatever the query: the vector channel's membership is the test's input.</summary>
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

    private static SearchQuery Query(string? text, int limit = 25, RetrievalCursor? cursor = null, string? board = null) =>
        new(text, board is null ? null : Curia.Application.Tests.Typed.Board(board), [], [], null, null, cursor, limit);

    private static async Task<SearchPage> SearchAsync(Harness h, SearchQuery query, CancellationToken ct)
    {
        var log = await LogAsync(h.Store, ct).ConfigureAwait(false);
        var fold = VerificationProjector.Fold(PostProjector.Fold(log), AgentStandingProjector.Fold(log), _ => true, DisputedPosts.None);
        return Require(await SearchOverAsync(h.Search, log, RetrievalSurface.RestSearch, query, fold, ct).ConfigureAwait(false));
    }

    /// <summary>One post of the scene: a question by an author of its own (so the share cap never fires), titled without the term, its body as the scene says.</summary>
    private static Task<string> PutAsync(Harness h, string id, string body, CancellationToken ct) =>
        AcceptAsync(h.Store, h.Indexer, id, ct, author: "https://agents.example/" + id, title: "a note about " + id, body: body);

    // ----- reading what does not exist yet, by name -----

#pragma warning disable CA1308 // the wire words are lower case; the comparison is of spellings
    private static string Squash(string? value) => (value ?? string.Empty).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
#pragma warning restore CA1308

    private static object? Read(object? owner, string member) =>
        owner?.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance)?.GetValue(owner);

    private static bool HasMember(object owner, string member) =>
        owner.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance) is not null;

    /// <summary>The mark a result carries, squashed (<c>words</c>, <c>meaning</c>, <c>wordsandmeaning</c>), or null when the result carries none.</summary>
    private static string? Mark(RankedPost result) => Read(result, "MatchedBy") is { } mark ? Squash(mark.ToString()) : null;

    private static string Marks(SearchPage page) =>
        string.Join(" ", page.Results.Select(r => $"{r.Post.PostId}={Mark(r) ?? "none"}"));

    /// <summary>The page's three counts as (words, meaning, words_and_meaning), or null when the page states none.</summary>
    private static (int Words, int Meaning, int Both)? Counts(SearchPage page)
    {
        if (Read(page, "ByChannel") is not { } counts) return null;
        int Number(string member) => Convert.ToInt32(Read(counts, member) ?? throw new InvalidOperationException($"ByChannel has no {member}"), System.Globalization.CultureInfo.InvariantCulture);
        return (Number("Words"), Number("Meaning"), Number("WordsAndMeaning"));
    }

    private static string Show((int Words, int Meaning, int Both)? counts) =>
        counts is { } c ? $"{{words {c.Words}, meaning {c.Meaning}, words_and_meaning {c.Both}}}" : "no member";

    /// <summary>The scene of MBC1: <c>b</c> carries the term and is returned by the stub (both channels), <c>m</c> does not carry it and is returned (the vector channel alone), <c>w</c> carries it and is not returned (the keyword channel alone), <c>n</c> is neither.</summary>
    private static async Task<(Harness H, string B, string M, string W, string N)> SceneAsync(CancellationToken ct)
    {
        var h = NewHarness();
        var b = await PutAsync(h, "b", $"{Term} {Term} wandering in the grass", ct).ConfigureAwait(false);
        var m = await PutAsync(h, "m", "a striped animal grazing on a plain", ct).ConfigureAwait(false);
        var w = await PutAsync(h, "w", $"{Term} crossing", ct).ConfigureAwait(false);
        var n = await PutAsync(h, "n", "something about pottery", ct).ConfigureAwait(false);
        h.Index.Matches.Add((b, 0.2));
        h.Index.Matches.Add((m, 0.3));
        return (h, b, m, w, n);
    }

    /// <summary>
    /// MBC1. R9.67: <c>words</c> is a result only the keyword channel ranked, <c>meaning</c> one only the vector channel ranked, <c>words_and_meaning</c> one both ranked, and a post
    /// neither ranked is no result. The inputs set each: <c>b</c> holds the term twice and is the stub's nearest; <c>m</c> is the stub's second and holds no term; <c>w</c> holds
    /// the term once and the stub does not return it; <c>n</c> holds nothing the query says. The page's counts equal the tally the inputs make (one of each).
    /// </summary>
    [Fact]
    public async Task MBC1_EachResultCarriesTheChannelTheInputsSetAndThePageCountsThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, _, _, _, _) = await SceneAsync(ct);

        var page = await SearchAsync(h, Query(Term), ct);

        Assert.DoesNotContain(page.Results, r => r.Post.PostId == "n");
        var expected = "b=wordsandmeaning m=meaning w=words";
        var actual = string.Join(" ", page.Results.OrderBy(r => r.Post.PostId, StringComparer.Ordinal).Select(r => $"{r.Post.PostId}={Mark(r) ?? "none"}"));
        Assert.True(expected == actual, $"Expected: matched_by {expected}  Actual: {(page.Results.Any(r => HasMember(r, "MatchedBy")) ? actual : "no member: " + actual)}");

        var counts = Counts(page);
        Assert.True(counts == (1, 1, 1), $"Expected: by_channel {{words 1, meaning 1, words_and_meaning 1}}  Actual: by_channel {Show(counts)}");
    }

    /// <summary>
    /// MBC1's control, green on the base: the fused order is the base's. Hand table, k 60: b is lexical rank 1 and vector rank 1 (2/61); m is vector rank 2 (1/62); w is lexical rank
    /// 2 (1/62), a tie with m that the log's sequence breaks (m was accepted first). The marks change no order: <c>b m w</c>. Carries information through FMBC1 (a derivation that
    /// reorders), nothing about the marks.
    /// </summary>
    [Fact]
    public async Task MBC1_Control_TheFusedOrderIsTheBasesOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, _, _, _, _) = await SceneAsync(ct);

        var page = await SearchAsync(h, Query(Term), ct);

        Assert.Equal(["b", "m", "w"], page.Results.Select(r => r.Post.PostId));
    }

    /// <summary>
    /// MBC2. R9.67: the counts are over the results the page serves. Four posts of four authors, <c>limit</c> 2: <c>b1</c> and <c>b2</c> are both channels (the stub's two nearest),
    /// <c>m1</c> is the vector channel alone (the stub's third), <c>w1</c> is the keyword channel alone. Hand table: b1 2/61, b2 1/61 + 1/62, m1 1/63, w1 lexical rank 3 so 1/63, the
    /// tie going to m1 by sequence: page 1 is <c>b1 b2</c> and page 2 is <c>m1 w1</c>. Page 2 counts {words 1, meaning 1, words_and_meaning 0}; the whole list would read {1, 1, 2}.
    /// (The disputed half of this probe, a post dropped from the page after the window, is in the Api suite: a post is disputed only by a signed declaration.)
    /// </summary>
    [Fact]
    public async Task MBC2_ASecondPageCountsItsOwnResultsAndNotTheList()
    {
        var ct = TestContext.Current.CancellationToken;
        var h = NewHarness();
        var b1 = await PutAsync(h, "b1", $"{Term} {Term} {Term} herd", ct);
        var b2 = await PutAsync(h, "b2", $"{Term} {Term} herd", ct);
        var m1 = await PutAsync(h, "m1", "a plain with grazing animals", ct);
        await PutAsync(h, "w1", $"{Term} herd", ct);
        h.Index.Matches.Add((b1, 0.1));
        h.Index.Matches.Add((b2, 0.15));
        h.Index.Matches.Add((m1, 0.2));

        var first = await SearchAsync(h, Query(Term, limit: 2), ct);
        Assert.Equal(["b1", "b2"], first.Results.Select(r => r.Post.PostId));
        Assert.NotNull(first.Next);
        var second = await SearchAsync(h, Query(Term, limit: 2, cursor: first.Next), ct);
        Assert.Equal(["m1", "w1"], second.Results.Select(r => r.Post.PostId));

        var firstCounts = Counts(first);
        var secondCounts = Counts(second);
        Assert.True(firstCounts == (0, 0, 2), $"Expected: page 1 by_channel {{words 0, meaning 0, words_and_meaning 2}}  Actual: {Show(firstCounts)}");
        Assert.True(secondCounts == (1, 1, 0), $"Expected: page 2 by_channel {{words 1, meaning 1, words_and_meaning 0}}  Actual: {Show(secondCounts)}");
    }

    /// <summary>
    /// MBC3. R9.67: a request with a board filter and no text ran neither channel by words or meaning, so a result carries no mark and the page no count. <b>Green on the base for the
    /// wrong reason</b> (no member exists to be present): it carries information only through FMBC4, the gate removed.
    /// </summary>
    [Fact]
    public async Task MBC3_AFilterWithNoTextCarriesNeitherMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, _, _, _, _) = await SceneAsync(ct);

        var page = await SearchAsync(h, Query(null, board: "postgres"), ct);

        Assert.Equal(4, page.Results.Length);
        Assert.True(Counts(page) is null, $"Expected: no by_channel  Actual: by_channel {Show(Counts(page))}");
        Assert.True(page.Results.All(r => Mark(r) is null), $"Expected: no matched_by  Actual: {Marks(page)}");
    }

    /// <summary>
    /// MBC3b. R9.67 (Amendment 1, ruling 3): the members are present if and only if the query has a term under <c>LexicalSearch.Tokenize</c>. A text of punctuation only, white space
    /// only or nothing is a listing: the stub returns two vector matches whatever the query, and the page still carries neither member, and its order is the base's. Hand table:
    /// no term, so every post is a lexical hit at score zero in sequence order (lexical ranks 1 to 4, b m w n); where the embedder ranks the stub's matches, b is vector rank 1 and
    /// m rank 2, which keeps <c>b m w n</c>. <b>Green on the base for the wrong reason</b>: it carries information only through FMBC4 and FMBC9.
    /// </summary>
    [Theory]
    [InlineData("???")]
    [InlineData(" ")]
    [InlineData("")]
    public async Task MBC3b_AQueryWithNoTermIsAListingAndCarriesNeitherMember(string text)
    {
        var ct = TestContext.Current.CancellationToken;
        var (h, _, _, _, _) = await SceneAsync(ct);

        var page = await SearchAsync(h, Query(text, board: "postgres"), ct);

        Assert.Equal(["b", "m", "w", "n"], page.Results.Select(r => r.Post.PostId));
        Assert.True(Counts(page) is null, $"Expected: no by_channel for a query with no term  Actual: by_channel {Show(Counts(page))}");
        Assert.True(page.Results.All(r => Mark(r) is null), $"Expected: no matched_by for a query with no term  Actual: {Marks(page)}");
    }
}
