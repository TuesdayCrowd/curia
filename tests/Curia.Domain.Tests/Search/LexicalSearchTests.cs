using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Domain.Tests.Search;

/// <summary>
/// Table 22's Phase 1 "lexical search", finally exercised.
///
/// <para><b>Why this file exists at all.</b> <see cref="LexicalSearch"/> shipped with no caller and
/// no test — a grep for the type across <c>src/</c> and <c>tests/</c> returned one hit, its own
/// definition. 208 lines of ranking, cursor encoding and tokenization that had never been executed.
/// Most of what follows is characterisation: it records what the code does so that a route can be
/// built on something known rather than something assumed. One of them is not characterisation —
/// <see cref="R9_7_PagingReturnsEachResultExactlyOnce"/> — because the code was wrong.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LexicalSearchTests
{
    private static SearchablePost Post(
        long seq,
        string body = "",
        string? title = null,
        string board = "general",
        PostKind kind = PostKind.Question,
        string author = "https://agents.example/a",
        string[]? tags = null) =>
        new(
            $"post-{seq}",
            $"digest-{seq}",
            board,
            kind,
            title,
            body,
            [.. tags ?? []],
            author,
            seq);

    // ---- ranking ------------------------------------------------------------------------

    /// <summary>
    /// The published weights: Title 5, Tag 3, Body 1. Asserted as the ratio between three posts
    /// that differ only in <i>where</i> the term appears, so a change to any one weight moves an
    /// ordering rather than merely a number.
    /// </summary>
    [Fact]
    public void TitleOutranksTagWhichOutranksBody()
    {
        var corpus = (SearchablePost[])[
            Post(1, body: "jcs"),
            Post(2, tags: ["jcs"]),
            Post(3, title: "jcs"),
        ];

        var hits = LexicalSearch.Search(corpus, new LexicalQuery(Text: "jcs"));

        Assert.Equal((string[])["post-3", "post-2", "post-1"], hits.Select(h => h.Post.PostId).ToArray());
        Assert.Equal((int[])[5, 3, 1], hits.Select(h => h.Score).ToArray());
    }

    /// <summary>R9.8/R8.36: the breakdown is per field, so an operator can see what carried a result.</summary>
    [Fact]
    public void R9_8_TheBreakdownNamesEachFieldSeparately()
    {
        var hit = Assert.Single(LexicalSearch.Search(
            [Post(1, body: "jcs jcs", title: "jcs", tags: ["jcs"])],
            new LexicalQuery(Text: "jcs")));

        Assert.Equal(1, hit.Why.TitleMatches);
        Assert.Equal(2, hit.Why.BodyMatches);
        Assert.Equal(1, hit.Why.TagMatches);
        Assert.Equal((1 * 5) + (1 * 3) + (2 * 1), hit.Why.Score);
        Assert.Equal(hit.Score, hit.Why.Score);
    }

    /// <summary>Term frequency counts: two occurrences outrank one.</summary>
    [Fact]
    public void RepeatedTermsRankHigher()
    {
        var hits = LexicalSearch.Search(
            [Post(1, body: "jcs"), Post(2, body: "jcs jcs jcs")],
            new LexicalQuery(Text: "jcs"));

        Assert.Equal("post-2", hits[0].Post.PostId);
    }

    /// <summary>
    /// Ties break by <c>seq</c> ascending, which is what makes ordering <i>stable</i> under R9.7:
    /// two posts with equal score must not swap places between pages.
    /// </summary>
    [Fact]
    public void R9_7_EqualScoresBreakByAscendingSequence()
    {
        var hits = LexicalSearch.Search(
            ((SearchablePost[])[Post(3, body: "jcs"), Post(1, body: "jcs"), Post(2, body: "jcs")])
                .OrderBy(p => p.Sequence).ToArray(),
            new LexicalQuery(Text: "jcs"));

        Assert.Equal((string[])["post-1", "post-2", "post-3"], hits.Select(h => h.Post.PostId).ToArray());
    }

    // ---- tokenization -------------------------------------------------------------------

    /// <summary>Matching is case-insensitive because both sides are lower-cased on the way in.</summary>
    [Fact]
    public void MatchingIsCaseInsensitive() =>
        Assert.Single(LexicalSearch.Search([Post(1, body: "Canonical JSON")], new LexicalQuery(Text: "canonical")));

    /// <summary>
    /// Non-alphanumerics split tokens, so punctuation attached to a word does not hide it. This is
    /// also why the ranker cannot match a phrase: there are only terms.
    /// </summary>
    [Fact]
    public void PunctuationSplitsTokens() =>
        Assert.Single(LexicalSearch.Search([Post(1, body: "RFC-8785, section 3.2.3.")], new LexicalQuery(Text: "8785")));

    /// <summary>
    /// Substrings do not match: tokens are compared whole. Recorded because it is the first thing a
    /// caller assumes otherwise, and a stemmer or prefix match would be a deliberate change.
    /// </summary>
    [Fact]
    public void SubstringsDoNotMatch() =>
        Assert.Empty(LexicalSearch.Search([Post(1, body: "canonicalization")], new LexicalQuery(Text: "canonical")));

    /// <summary>
    /// Every query term is counted, and a post matching only one of two still ranks — the query is
    /// disjunctive over terms even though the tag filter is conjunctive.
    /// </summary>
    [Fact]
    public void QueryTermsAreDisjunctive()
    {
        var hits = LexicalSearch.Search(
            [Post(1, body: "jcs"), Post(2, body: "jcs nfc")],
            new LexicalQuery(Text: "jcs nfc"));

        Assert.Equal(2, hits.Length);
        Assert.Equal("post-2", hits[0].Post.PostId);
    }

    // ---- filters ------------------------------------------------------------------------

    /// <summary>
    /// R9.6: a query with no text is a filter rather than a search — everything surviving the
    /// filters is a hit at score zero, in seq order. Refusing it would make the filter-only case
    /// need a second endpoint.
    /// </summary>
    [Fact]
    public void R9_6_AQueryWithNoTextIsAFilter()
    {
        var hits = LexicalSearch.Search(
            [Post(1, board: "a"), Post(2, board: "b"), Post(3, board: "a")],
            new LexicalQuery(Board: "a"));

        Assert.Equal((string[])["post-1", "post-3"], hits.Select(h => h.Post.PostId).ToArray());
        Assert.All(hits, h => Assert.Equal(0, h.Score));
    }

    /// <summary>Board, kind and author filter exactly — ordinal, never culture-sensitive.</summary>
    [Fact]
    public void R9_6_BoardKindAndAuthorFilterExactly()
    {
        var corpus = (SearchablePost[])[
            Post(1, board: "jcs", kind: PostKind.Question, author: "https://agents.example/alice"),
            Post(2, board: "jcs", kind: PostKind.Answer, author: "https://agents.example/alice"),
            Post(3, board: "JCS", kind: PostKind.Question, author: "https://agents.example/bob"),
        ];

        Assert.Equal(2, LexicalSearch.Search(corpus, new LexicalQuery(Board: "jcs")).Length);
        Assert.Single(LexicalSearch.Search(corpus, new LexicalQuery(Kinds: [PostKind.Answer])));
        Assert.Single(LexicalSearch.Search(corpus, new LexicalQuery(Author: "https://agents.example/bob")));

        // Board is case-sensitive: "JCS" is a different board, not the same one spelled loudly.
        Assert.Single(LexicalSearch.Search(corpus, new LexicalQuery(Board: "JCS")));
    }

    /// <summary>
    /// R9.26: the kind criterion names a <i>set</i>, because its honest use requires one. G12 makes
    /// the verification floor an opt-in criterion and the set it applies to is {answer, finding}
    /// (R10.45's gradable kinds), so an agent told to compose `kind` with `min_verification` could
    /// express only half of what that compensation claims while the criterion was a scalar.
    ///
    /// <para>The set is disjunctive where the tag filter is conjunctive, and the asymmetry is not an
    /// inconsistency: a post has many tags and exactly one kind, so "both tags" narrows and "either
    /// kind" is the only reading that is not empty by construction.</para>
    /// </summary>
    [Fact]
    public void R9_26_TheKindCriterionNamesASet()
    {
        var corpus = (SearchablePost[])[
            Post(1, kind: PostKind.Question),
            Post(2, kind: PostKind.Answer),
            Post(3, kind: PostKind.Finding),
            Post(4, kind: PostKind.Comment),
        ];

        // The set G12's compensation actually needs: the gradable kinds, in one request.
        var gradable = LexicalSearch.Search(corpus, new LexicalQuery(Kinds: [PostKind.Answer, PostKind.Finding]));
        Assert.Equal(["post-2", "post-3"], gradable.Select(h => h.Post.PostId).Order());

        // One kind still means one kind.
        Assert.Single(LexicalSearch.Search(corpus, new LexicalQuery(Kinds: [PostKind.Answer])));

        // An empty set is "no kind criterion", not "no kinds": R9.25 says a request naming no
        // criterion returns everything the corpus matches. The opposite reading makes an omitted
        // member silently exclude the whole corpus, which is the failure R9.25 is about.
        Assert.Equal(4, LexicalSearch.Search(corpus, new LexicalQuery()).Length);
        Assert.Equal(4, LexicalSearch.Search(corpus, new LexicalQuery(Kinds: [])).Length);
    }

    /// <summary>
    /// The tag filter is conjunctive: naming two tags means "both". A disjunctive reading would
    /// widen the result set exactly when the agent was trying to narrow it.
    /// </summary>
    [Fact]
    public void R9_6_TheTagFilterIsConjunctive()
    {
        var corpus = (SearchablePost[])[
            Post(1, tags: ["jcs"]),
            Post(2, tags: ["jcs", "nfc"]),
        ];

        Assert.Single(LexicalSearch.Search(corpus, new LexicalQuery(Tags: ["jcs", "nfc"])));
        Assert.Equal(2, LexicalSearch.Search(corpus, new LexicalQuery(Tags: ["jcs"])).Length);
    }

    /// <summary>Tags match case-insensitively, unlike board — recorded because the two differ.</summary>
    [Fact]
    public void TagsMatchCaseInsensitively() =>
        Assert.Single(LexicalSearch.Search([Post(1, tags: ["JCS"])], new LexicalQuery(Tags: ["jcs"])));

    /// <summary>A text query excludes non-matching posts entirely rather than ranking them zero.</summary>
    [Fact]
    public void NonMatchingPostsAreExcludedNotRankedZero() =>
        Assert.Empty(LexicalSearch.Search([Post(1, body: "nfc")], new LexicalQuery(Text: "jcs")));

    // ---- pagination ---------------------------------------------------------------------

    /// <summary>
    /// <b>R9.7, and the one defect this characterisation pass found.</b>
    ///
    /// <para>R9.7: "Result ordering SHALL be stable and paginable via opaque cursors, not offsets.
    /// Offset pagination over a changing corpus silently skips and repeats items, and an agent
    /// paging through 500 results will not notice."</para>
    ///
    /// <para>Before the fix this returned <c>[post-10, post-9, post-8, post-10, post-9]</c> for ten
    /// matching posts: post-9 and post-10 twice, posts 1–7 never. Both failure modes R9.7 names, on
    /// a corpus that was not even changing — because the cursor was keyed on <c>seq</c> while the
    /// ordering was by score, so the cursor handed back the seq of the <i>lowest-scoring</i> row and
    /// page two excluded everything below it.</para>
    ///
    /// <para><b>The arrangement matters and is the reason this is written the way it is.</b> Scores
    /// here <i>rise</i> with seq, so the top-scoring page carries high seqs. Building the corpus the
    /// other way round makes score order and seq order coincide, and a seq-keyed cursor is then
    /// accidentally correct — a first attempt at this test did exactly that and passed against the
    /// broken code.</para>
    /// </summary>
    [Fact]
    public void R9_7_PagingReturnsEachResultExactlyOnce()
    {
        var corpus = Enumerable.Range(1, 10)
            .Select(i => Post(i, body: string.Join(" ", Enumerable.Repeat("jcs", i))))
            .ToArray();

        var seen = new List<string>();
        SearchCursor? cursor = null;

        for (var page = 0; page < 20; page++)
        {
            var hits = LexicalSearch.Search(corpus, new LexicalQuery(Text: "jcs", Cursor: cursor, Limit: 3));
            if (hits.IsEmpty) break;

            seen.AddRange(hits.Select(h => h.Post.PostId));
            cursor = LexicalSearch.NextCursor(hits, 3);
            if (cursor is null) break;
        }

        Assert.Equal(10, seen.Count);
        Assert.Equal(10, seen.Distinct().Count());

        // And in rank order across the page boundaries, not merely present: paging must not
        // reshuffle what a single large page would have returned.
        Assert.Equal(
            LexicalSearch.Search(corpus, new LexicalQuery(Text: "jcs", Limit: 10))
                .Select(h => h.Post.PostId)
                .ToArray(),
            seen.ToArray());
    }

    /// <summary>
    /// Paging a filter-only query works too, where every score ties at zero and the composite
    /// cursor degrades to ordering by seq alone.
    /// </summary>
    [Fact]
    public void R9_7_PagingAFilterOnlyQueryReturnsEachResultExactlyOnce()
    {
        var corpus = Enumerable.Range(1, 7).Select(i => Post(i)).ToArray();

        var seen = new List<string>();
        SearchCursor? cursor = null;

        for (var page = 0; page < 20; page++)
        {
            var hits = LexicalSearch.Search(corpus, new LexicalQuery(Cursor: cursor, Limit: 2));
            if (hits.IsEmpty) break;

            seen.AddRange(hits.Select(h => h.Post.PostId));
            cursor = LexicalSearch.NextCursor(hits, 2);
            if (cursor is null) break;
        }

        Assert.Equal(7, seen.Distinct().Count());
        Assert.Equal(7, seen.Count);
    }

    /// <summary>
    /// A post appended after paging started lands in a later page and disturbs nothing already
    /// returned — the property that makes a keyset cursor honest here. It holds because a lexical
    /// score is a pure function of immutable content and the query, and the log is append-only, so
    /// no already-returned post can change position.
    /// </summary>
    [Fact]
    public void R9_7_AppendingToTheCorpusDoesNotDisturbPagesAlreadyReturned()
    {
        var corpus = Enumerable.Range(1, 4)
            .Select(i => Post(i, body: string.Join(" ", Enumerable.Repeat("jcs", i))))
            .ToList();

        var first = LexicalSearch.Search(corpus, new LexicalQuery(Text: "jcs", Limit: 2));
        var cursor = LexicalSearch.NextCursor(first, 2);

        // A newcomer that would have ranked on page one had it existed then.
        corpus.Add(Post(5, body: "jcs jcs jcs jcs jcs"));

        var second = LexicalSearch.Search(corpus, new LexicalQuery(Text: "jcs", Cursor: cursor, Limit: 2));

        Assert.Empty(second.Select(h => h.Post.PostId).Intersect(first.Select(h => h.Post.PostId)));
    }

    /// <summary>The last page reports no cursor, which is how a caller knows to stop.</summary>
    [Fact]
    public void AShortPageReportsNoNextCursor()
    {
        var hits = LexicalSearch.Search([Post(1, body: "jcs")], new LexicalQuery(Text: "jcs", Limit: 10));
        Assert.Null(LexicalSearch.NextCursor(hits, 10));
    }

    /// <summary>
    /// R9.7's cursors are opaque by contract. Base64 rather than a bare number so a client that
    /// starts doing arithmetic on one is doing something visibly unsupported.
    /// </summary>
    [Fact]
    public void R9_7_ACursorRoundTripsThroughItsOpaqueEncoding()
    {
        var cursor = new SearchCursor(42, 7);
        var decoded = SearchCursor.Decode(cursor.Encode());

        Assert.Equal(cursor, decoded);
    }

    /// <summary>
    /// A mangled cursor reads as "start from the beginning" rather than throwing. A client that
    /// corrupted its cursor gets the first page, which is recoverable; an exception on a read path
    /// is not, and R9.7's concern is silent skipping, which starting over avoids.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64!!")]
    [InlineData("bm90LWEtY3Vyc29y")]
    public void AMalformedCursorReadsAsTheFirstPage(string encoded) =>
        Assert.Null(SearchCursor.Decode(encoded));

    // ---- limits -------------------------------------------------------------------------

    /// <summary>A page is bounded by the requested limit.</summary>
    [Fact]
    public void TheLimitBoundsThePage() =>
        Assert.Equal(2, LexicalSearch.Search(
            [Post(1, body: "jcs"), Post(2, body: "jcs"), Post(3, body: "jcs")],
            new LexicalQuery(Text: "jcs", Limit: 2)).Length);

    /// <summary>
    /// <b>The limit is capped, and was not.</b> <c>Take(query.Limit)</c> served whatever a caller
    /// asked for, so a single request could ask the Forum to rank and materialise the entire corpus.
    /// The domain caps rather than trusting its caller, because a domain function has to be total
    /// over its inputs and the HTTP layer is not the only thing that will ever call this.
    /// </summary>
    [Fact]
    public void ThePageSizeIsCappedRegardlessOfWhatTheCallerAsksFor()
    {
        var corpus = Enumerable.Range(1, LexicalSearch.MaximumLimit + 50)
            .Select(i => Post(i, body: "jcs"))
            .ToArray();

        var hits = LexicalSearch.Search(corpus, new LexicalQuery(Text: "jcs", Limit: int.MaxValue));

        Assert.Equal(LexicalSearch.MaximumLimit, hits.Length);
    }

    /// <summary>A non-positive limit yields one result rather than none or an exception.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveLimitYieldsASingleResult(int limit) =>
        Assert.Single(LexicalSearch.Search(
            [Post(1, body: "jcs"), Post(2, body: "jcs")],
            new LexicalQuery(Text: "jcs", Limit: limit)));

    /// <summary>An empty corpus is an empty result, not a failure.</summary>
    [Fact]
    public void AnEmptyCorpusYieldsNoHits() =>
        Assert.Empty(LexicalSearch.Search([], new LexicalQuery(Text: "jcs")));
}
