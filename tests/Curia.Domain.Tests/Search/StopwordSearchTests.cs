using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Domain.Content;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Domain.Tests.Search;

/// <summary>
/// Errata G81 (R9.72), plan probes KWC3 to KWC7: the keyword channel takes as a query's terms the tokens that are not on the published list, and keeps three states apart.
///
/// <para><b>The method does not exist on the base, so it is read by name.</b> <c>LexicalSearch.SearchTerms(string?)</c> is called by reflection and its result's <c>Terms</c>, <c>Ignored</c>
/// and <c>HadToken</c> are read by name (public properties or fields: the build returns a named record, since a value tuple's names are not members). A probe that referred to it
/// directly would be red by a compile error and not by its own assertion. Every oracle below is a hand table written beside the rule's sentence, never the build's own list.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class StopwordSearchTests
{
    private static SearchablePost Post(long seq, string body, string? title = null, string[]? tags = null) =>
        new($"p{seq}", $"digest-{seq}", "general", PostKind.Question, title, body, [.. tags ?? []], "https://agents.example/a", seq);

    // ----- reading what does not exist yet, by name -----

    private static MethodInfo? SearchTermsMethod => typeof(LexicalSearch).GetMethod("SearchTerms", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);

    private static object? Member(object owner, string name) =>
        owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(owner)
        ?? owner.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(owner);

    private static (string[] Terms, string[] Ignored, bool HadToken) SearchTerms(string? text)
    {
        var method = SearchTermsMethod;
        Assert.True(method is not null, "Expected: LexicalSearch.SearchTerms(string?) returns the terms the channel uses, the words it ignored and whether the text had a token  Actual: no such method");

        var result = method.Invoke(null, [text]) ?? throw new InvalidOperationException("SearchTerms returned null");
        string[] Strings(string name) => ((System.Collections.IEnumerable)(Member(result, name) ?? throw new InvalidOperationException($"SearchTerms' result has no {name}"))).Cast<string>().ToArray();
        return (Strings("Terms"), Strings("Ignored"), (bool)(Member(result, "HadToken") ?? throw new InvalidOperationException("SearchTerms' result has no HadToken")));
    }

    private static string Words(IEnumerable<string> words) => "[" + string.Join(", ", words) + "]";

    private static string Ids(IEnumerable<SearchHit> hits) => "[" + string.Join(", ", hits.Select(h => h.Post.PostId)) + "]";

    // ----- KWC3 -----

    /// <summary>
    /// KWC3. The hand table beside the rule's sentence. <c>How do I rotate the key?</c>: terms <c>rotate key</c>, ignored <c>how do i the</c> (in the query's order). <c>don't panic</c> is
    /// cut by the tokenizer into <c>don</c>, <c>t</c>, <c>panic</c>: ignored <c>don t</c>, terms <c>panic</c>. Capitals fold (<c>THE</c> is <c>the</c>). A repeated ignored word appears once, a
    /// repeated term twice. <c>f\u00fcr</c> and <c>\u00e7a</c> are terms: the list is lower-case ASCII and matches no other script. Text with no token has no term, no ignored word and no token.
    /// </summary>
    [Fact]
    public void KWC3_SearchTermsSplitsATextIntoTheTermsTheChannelUsesAndTheWordsItIgnored()
    {
        var rows = new (string? Text, string[] Terms, string[] Ignored, bool HadToken)[]
        {
            ("How do I rotate the key?", ["rotate", "key"], ["how", "do", "i", "the"], true),
            ("don't panic", ["panic"], ["don", "t"], true),
            ("THE The the", [], ["the"], true),
            ("the pool the pool", ["pool", "pool"], ["the"], true),
            ("f\u00fcr \u00e7a", ["f\u00fcr", "\u00e7a"], [], true),
            ("???", [], [], false),
            (null, [], [], false),
        };

        foreach (var (text, terms, ignored, hadToken) in rows)
        {
            var actual = SearchTerms(text);
            Assert.True(
                actual.Terms.SequenceEqual(terms) && actual.Ignored.SequenceEqual(ignored) && actual.HadToken == hadToken,
                $"Expected: SearchTerms(\"{text}\") terms {Words(terms)} ignored {Words(ignored)} had_token {hadToken}  Actual: terms {Words(actual.Terms)} ignored {Words(actual.Ignored)} had_token {actual.HadToken}");
        }
    }

    /// <summary>KWC3's control, green on the base: <c>Tokenize</c> is untouched by the stage (the dedupe's tokenizer, R8.18 (rev.)), so every token of the same texts is still a token.</summary>
    [Fact]
    public void KWC3_Control_TokenizeKeepsEveryWord()
    {
        Assert.Equal(["how", "do", "i", "rotate", "the", "key"], LexicalSearch.Tokenize("How do I rotate the key?"));
        Assert.Equal(["don", "t", "panic"], LexicalSearch.Tokenize("don't panic"));
        Assert.Equal(["f\u00fcr", "\u00e7a"], LexicalSearch.Tokenize("f\u00fcr \u00e7a"));
    }

    // ----- KWC4: the three states -----

    private static ImmutableArray<SearchablePost> ThreePosts() =>
    [
        Post(0, "the cat sat"),
        Post(1, "the pool is deep"),
        Post(2, "swimming pool"),
    ];

    /// <summary>KWC4's control, green on the base: no token at all (null, <c>???</c>) is a listing, every post at score 0 in sequence order. The fix must not touch this state.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("???")]
    public void KWC4_Control_NoTokenIsAListingOfEveryPostAtScoreZero(string? text)
    {
        var hits = LexicalSearch.Rank(ThreePosts(), new LexicalQuery(Text: text));

        Assert.True(hits.Select(h => (h.Post.PostId, h.Score)).SequenceEqual([("p0", 0), ("p1", 0), ("p2", 0)]), $"Expected: a listing [p0, p1, p2] at score 0  Actual: {Ids(hits)} scores {Words(hits.Select(h => h.Score.ToString(System.Globalization.CultureInfo.InvariantCulture)))}");
    }

    /// <summary>
    /// KWC4. A query with a token and no term left lists <b>no post</b>: not a listing (<c>terms.Length &gt; 0</c> was the listing's switch, and a filter that empties it would list all three at
    /// score 0 while every probe about "fewer" stayed green: FKWC3), and not a fallback to the tokens (FKWC5). On the base <c>the</c> lists the two posts that say it, which is the harm this stage
    /// exists for; a build that fixes the filter and not the switch fails here by <b>listing three</b>.
    /// </summary>
    [Fact]
    public void KWC4_AQueryOfOnlyListedWordsListsNoPostAndIsNeverAListing()
    {
        var hits = LexicalSearch.Rank(ThreePosts(), new LexicalQuery(Text: "the"));

        Assert.True(hits.IsEmpty, $"Expected: \"the\" lists no post  Actual: lists {hits.Length} {Ids(hits)}");
    }

    /// <summary>KWC4. A query with a term left lists exactly the posts holding that term: <c>the pool</c> lists the two that hold <c>pool</c>, and not the one that only says <c>the</c>.</summary>
    [Fact]
    public void KWC4_ATermAmongListedWordsListsExactlyThePostsHoldingIt()
    {
        var hits = LexicalSearch.Rank(ThreePosts(), new LexicalQuery(Text: "the pool"));

        Assert.True(hits.Select(h => h.Post.PostId).Order().SequenceEqual(["p1", "p2"]), $"Expected: \"the pool\" lists [p1, p2]  Actual: {Ids(hits)}");
    }

    // ----- KWC5: the ranking moves where the arithmetic says -----

    /// <summary>
    /// KWC5, by hand. Post A says <c>the</c> thirty times and <c>pool</c> once in its body; post B says <c>pool</c> twice. Query <c>the pool</c>. Without the list: A 30 + 1 = 31, B 2, order A B.
    /// With the list, the terms are <c>pool</c> alone: B 2, A 1, order B A. A still holds <c>the</c> thirty times (the list is on the query, never on a post: FKWC9), and what it bought by saying
    /// it is gone.
    /// </summary>
    [Fact]
    public void KWC5_AListedWordNoLongerBuysAScoreAndThePostsOrderMoves()
    {
        var a = Post(0, string.Concat(Enumerable.Repeat("the ", 30)) + "pool");
        var b = Post(1, "pool pool");

        var hits = LexicalSearch.Rank([a, b], new LexicalQuery(Text: "the pool"));

        var shown = string.Join(" ", hits.Select(h => $"{h.Post.PostId}={h.Score}"));
        Assert.True(shown == "p1=2 p0=1", $"Expected: p1=2 p0=1 (B's two pools, then A's one)  Actual: {shown}");
    }

    // ----- KWC6: R9.7, the cursor -----

    /// <summary>
    /// KWC6, by hand. Four posts, one page at a time (limit 1), query <c>the pool</c>. Scores with the list: p0 <c>pool pool pool</c> 3, p2 <c>pool pool</c> 2, p3 <c>pool pool</c> and five <c>the</c> 2,
    /// p1 three <c>the</c> and one <c>pool</c> 1. The order is p0, p2, p3 (tied with p2 and later in sequence), p1; each post is returned once, and the first page's cursor holds the score 3 that
    /// the list gives p0 (without the list p3 would lead at 7). A continuation ranked with the full token set (FKWC11) repeats or skips a post.
    /// </summary>
    [Fact]
    public void KWC6_PagingOneAtATimeReturnsEachPostOnceInTheListsOrderAndTheCursorHoldsTheListsScore()
    {
        var corpus = (SearchablePost[])[
            Post(0, "pool pool pool"),
            Post(1, "the the the pool"),
            Post(2, "pool pool"),
            Post(3, "pool pool the the the the the"),
        ];

        var seen = new List<string>();
        SearchCursor? cursor = null;
        int? firstCursorScore = null;
        for (var page = 0; page < 6; page++)
        {
            var hits = LexicalSearch.Search(corpus, new LexicalQuery(Text: "the pool", Limit: 1, Cursor: cursor));
            if (hits.IsEmpty) break;
            seen.AddRange(hits.Select(h => h.Post.PostId));
            cursor = LexicalSearch.NextCursor(hits, 1);
            firstCursorScore ??= cursor?.AfterScore;
            if (cursor is null) break;
        }

        Assert.True(seen.SequenceEqual(["p0", "p2", "p3", "p1"]), $"Expected: pages p0 p2 p3 p1, each once  Actual: {Words(seen)}");
        Assert.True(firstCursorScore == 3, $"Expected: the first cursor's score is 3  Actual: {firstCursorScore}");
    }

    // ----- KWC7: the dedupe still sees function words -----

    /// <summary>KWC7, a control and green on the base, which must stay green: the dedupe's overlap is Jaccard over <c>Tokenize</c> and the list does not apply to it (R8.18 (rev.)). <c>the pool</c> against <c>pool</c> is 1/2.</summary>
    [Fact]
    public void KWC7_Control_TheDedupesOverlapStillCountsFunctionWords()
    {
        Assert.Equal(["the", "pool"], LexicalSearch.Tokenize("the pool"));
        Assert.Equal(0.5, LexicalOverlap.Jaccard("the pool", "pool"), 1e-12);
    }
}
