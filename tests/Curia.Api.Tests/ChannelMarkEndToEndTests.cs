using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.67 and R9.68 (errata G64) over the wire, PR 8's probes MBC4, MBC3b (its Api half), MBC2 (its disputed half) and DVW5. The hashed adapter serves the Forum, so the scene is the one
/// Task 0's M6 found: three posts on one board, a floor of 2000 bp, and the query <c>pool</c>:
/// <code>
/// both          title "pool keepalive"   body "the pool needs keepalive"     the keyword channel (whole word `pool`) and the vector channel (cosine 5015 bp)
/// meaning-only  title "pooling"          body "pooling again"                the vector channel alone (3958 bp): `pooling` is no whole word `pool`
/// words-only    title "gardening notes"  body 30 repetitions of a long sentence, then "also a pool"   the keyword channel alone: the vector channel does not keep it
/// </code>
/// The probes read the members from the wire JSON by name, so on the base the red is their own assertion.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ChannelMarkEndToEndTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private async Task<(string PostId, long LogIndex)> AskAsync(ForumAgent agent, DpopClient dpop, string token, string board, string title, string body, CancellationToken ct)
    {
        using var response = await dpop.PostAsync(forum.Client, PostsUrl, token, agent.SignQuestion(board, body, title, forum.Now), forum.Now, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        using var accepted = JsonDocument.Parse(text);
        var postId = accepted.RootElement.GetProperty("post_id").GetString()!;
        var served = await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        return (postId, served.GetProperty("log_index").GetInt64());
    }

    /// <summary>One post by an agent of its own.</summary>
    private async Task<(string PostId, long LogIndex)> AskAloneAsync(string board, string title, string body, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/asker-{Suffix()}", "asker-" + Suffix());
        var (dpop, token) = await agent.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        return await AskAsync(agent, dpop, token, board, title, body, ct);
    }

    private async Task<JsonDocument> SearchAsync(string query, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/search?{query}", UriKind.Relative), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET /v1/search?{query} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text);
    }

    private static JsonElement[] Hits(JsonDocument page) => [.. page.RootElement.GetProperty("results").EnumerateArray()];

    private static string IdOf(JsonElement hit) => hit.GetProperty("post").GetProperty("post_id").GetString()!;

    private static string[] Ids(JsonDocument page) => [.. Hits(page).Select(IdOf)];

    private static string? MatchedBy(JsonElement hit) => hit.TryGetProperty("matched_by", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;

    private static string Marks(JsonDocument page, IReadOnlyDictionary<string, string> names) =>
        string.Join(" ", Hits(page).Select(h => $"{names.GetValueOrDefault(IdOf(h), IdOf(h))}={MatchedBy(h) ?? "member absent"}"));

    /// <summary>The page's <c>by_channel</c> as (words, meaning, words_and_meaning), or null when the page carries no such member.</summary>
    private static (int Words, int Meaning, int Both)? ByChannel(JsonDocument page)
    {
        if (!page.RootElement.TryGetProperty("by_channel", out var counts) || counts.ValueKind != JsonValueKind.Object) return null;
        return (counts.GetProperty("words").GetInt32(), counts.GetProperty("meaning").GetInt32(), counts.GetProperty("words_and_meaning").GetInt32());
    }

    private static string Show((int Words, int Meaning, int Both)? counts) =>
        counts is { } c ? $"{{words {c.Words}, meaning {c.Meaning}, words_and_meaning {c.Both}}}" : "member absent";

    private static (int Words, int Meaning, int Both) Tally(JsonDocument page)
    {
        var marks = Hits(page).Select(MatchedBy).ToArray();
        return (marks.Count(m => m == "words"), marks.Count(m => m == "meaning"), marks.Count(m => m == "words_and_meaning"));
    }

    private async Task<(string Board, Dictionary<string, string> Names)> SceneAsync(CancellationToken ct)
    {
        var board = "m6-" + Suffix();
        var names = new Dictionary<string, string>();
        var texts = new (string Name, string Title, string Body)[]
        {
            ("both", "pool keepalive", "the pool needs keepalive"),
            ("meaning-only", "pooling", "pooling again"),
            ("words-only", "gardening notes", string.Join(' ', Enumerable.Range(0, 30).Select(i => "tulips and daffodils bloom in spring beds number " + Number(i))) + " also a pool"),
        };
        foreach (var t in texts) names[(await AskAloneAsync(board, t.Title, t.Body, ct)).PostId] = t.Name;
        return (board, names);
    }

    /// <summary>
    /// MBC4. R9.67: each hit's <c>matched_by</c> is the member the scene's construction sets, the tally of the served hits equals the page's <c>by_channel</c>, and
    /// <c>why_ranked.lexical</c> is present if and only if the mark is <c>words</c> or <c>words_and_meaning</c> and <c>why_ranked.vector</c> if and only if it is <c>meaning</c> or
    /// <c>words_and_meaning</c>. <b>Said plainly: that last pairing reads two members of one derivation</b>, so it cannot tell the build's derivation from a wrong one that moves both;
    /// the construction (the first assertion: which post is which mark) and MBC1's inputs (a stub index at chosen distances, in the Application suite) are the independent oracle.
    /// </summary>
    [Fact]
    public async Task MBC4_TheSceneSetsEachMarkTheTallyEqualsTheCountsAndTheExplanationPairs()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, names) = await SceneAsync(ct);

        using var page = await SearchAsync($"q=pool&board={board}&why=true", ct);

        var expected = new Dictionary<string, string> { ["both"] = "words_and_meaning", ["meaning-only"] = "meaning", ["words-only"] = "words" };
        var actual = Hits(page).ToDictionary(h => names[IdOf(h)], h => MatchedBy(h) ?? "member absent", StringComparer.Ordinal);
        Assert.True(
            expected.OrderBy(kv => kv.Key, StringComparer.Ordinal).SequenceEqual(actual.OrderBy(kv => kv.Key, StringComparer.Ordinal)),
            $"Expected: matched_by {string.Join(" ", expected.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Key + "=" + kv.Value))}  Actual: {Marks(page, names)}");

        var counts = ByChannel(page);
        Assert.True(counts == Tally(page), $"Expected: by_channel {Show(Tally(page))}  Actual: by_channel {Show(counts)}");
        Assert.True(counts == (1, 1, 1), $"Expected: by_channel {{words 1, meaning 1, words_and_meaning 1}}  Actual: by_channel {Show(counts)}");

        foreach (var hit in Hits(page))
        {
            var mark = MatchedBy(hit);
            var why = hit.GetProperty("why_ranked");
            Assert.Equal(mark is "words" or "words_and_meaning", why.TryGetProperty("lexical", out _));
            Assert.Equal(mark is "meaning" or "words_and_meaning", why.TryGetProperty("vector", out _));
        }
    }

    /// <summary>
    /// MBC3b (Api half). R9.67 (Amendment 1, ruling 3): a query with no term under <c>LexicalSearch.Tokenize</c> is a listing, so a hit carries no <c>matched_by</c> and the page no
    /// <c>by_channel</c>, whatever the vector channel does with its text; the order is the filter-only order (every post at score zero in the order it was accepted: both,
    /// meaning-only, words-only). <b>Green on the base for the wrong reason</b> (no member exists): it carries information only through FMBC4 and FMBC9.
    /// </summary>
    [Theory]
    [InlineData("q=%3F%3F%3F&")]
    [InlineData("")]
    public async Task MBC3b_AQueryWithNoTermIsAListingOverTheWire(string textPart)
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, names) = await SceneAsync(ct);

        using var page = await SearchAsync($"{textPart}board={board}&why=true", ct);

        Assert.Equal(["both", "meaning-only", "words-only"], Ids(page).Select(id => names[id]));
        Assert.True(!page.RootElement.TryGetProperty("by_channel", out _), $"Expected: no by_channel for a query with no term  Actual: by_channel {Show(ByChannel(page))}");
        Assert.True(Hits(page).All(h => MatchedBy(h) is null), $"Expected: no matched_by for a query with no term  Actual: {Marks(page, names)}");
    }

    /// <summary>
    /// MBC3b's other two spellings. The route refuses a present, blank <c>q</c> (<c>q=</c>, <c>q=%20</c>) as <c>curia/search/blank-parameter</c> before any channel runs, so there is no page to
    /// carry a member: the probe pins that, so that the two spellings are accounted for and a build that served them as listings would have to say what they carry. Green on the base
    /// (it is the base's refusal); carries information only through FMBC9 (a gate keyed on blank text would not change a refusal made before it).
    /// </summary>
    [Theory]
    [InlineData("q=")]
    [InlineData("q=%20")]
    public async Task MBC3b_ABlankTextIsRefusedBeforeAnyChannelRuns(string text)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await forum.Client.GetAsync(new Uri($"/v1/search?{text}&board=none-{Suffix()}", UriKind.Relative), ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("curia/search/blank-parameter", body, StringComparison.Ordinal);
    }

    /// <summary>A verb with the one key it signs with in its environment (as DisputedContentTests does): <c>compromise</c> the declaration key, <c>sign-head</c> the log key.</summary>
    private async Task<int> OperatorAsync(string pem, string declarationPem, CancellationToken ct, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var declares = args[0] == "compromise";
        return await OperatorCommands.RunAsync(
            args, forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, ct,
            readLogSigningKeyPem: () => declares ? null : pem,
            readDeclarationSigningKeyPem: () => declares ? declarationPem : null);
    }

    /// <summary>
    /// MBC2 (disputed half). R9.67: the counts are over the results the page serves, so a post a declaration disputes, dropped from the page after the window (R6.57), is not counted.
    /// Three posts of three authors carry one term; the page before the declaration counts three and the page after counts two, and both equal the tally of the hits they serve.
    /// </summary>
    [Fact]
    public async Task MBC2_ADisputedPostDroppedFromThePageIsNotCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "board-" + Suffix();
        var term = "zdispc" + Suffix();
        var thief = ForumAgent.Create($"https://agents.example/disputed-{Suffix()}", "only-" + Suffix());
        var (dpop, token) = await thief.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var disputed = await AskAsync(thief, dpop, token, board, $"Signed by the thief {term}", $"this post mentions {term}", ct);
        await AskAloneAsync(board, $"Kept one {term}", $"another, different post mentions {term}", ct);
        await AskAloneAsync(board, $"Kept two {term}", $"a third, distinct post mentions {term}", ct);

        int Total((int Words, int Meaning, int Both)? c) => c is { } v ? v.Words + v.Meaning + v.Both : -1;
        using var before = await SearchAsync($"q={term}&board={board}", ct);

        var pem = LogSigningKey.GeneratePem();
        var declarationPem = await DeclarationKeyHost.PemAsync(forum, ct);
        Assert.Equal(0, await OperatorAsync(pem, declarationPem, ct, "sign-head", "--by", "ops"));
        Assert.Equal(0, await OperatorAsync(pem, declarationPem, ct, "compromise", "--agent", thief.AgentId, "--kid", thief.Kid, "--from-index", Number(disputed.LogIndex), "--reason", "owner-reported", "--operator", "ops"));

        using var after = await SearchAsync($"q={term}&board={board}", ct);

        Assert.True(Total(ByChannel(before)) == 3 && Hits(before).Length == 3, $"Expected: 3 hits counted 3  Actual: {Hits(before).Length} hits, by_channel {Show(ByChannel(before))}");
        Assert.True(Hits(after).Length == 2 && Total(ByChannel(after)) == 2, $"Expected: 2 hits counted 2 (the disputed post dropped, not counted)  Actual: {Hits(after).Length} hits, by_channel {Show(ByChannel(after))}");
        Assert.Equal(Tally(after), ByChannel(after));
    }

    /// <summary>
    /// DVW5. R9.68: one author holds three posts that outrank five other authors' posts, <c>limit</c> 3 (cap 1). Walking the cursor to the end, the union of the pages equals the
    /// unlimited list as a set (nothing dropped, nothing repeated), <c>diversified</c> is equal on every page and equals the number of served hits whose
    /// <c>deferred_by_diversification</c> is true across all pages, the author's second post is on page 2 and not behind the whole list (hand table below), and page 1 at a larger
    /// <c>limit</c> holds more of the author's posts (the sentence "<c>limit</c> changes which posts a search returns" is a fact).
    /// <code>
    /// ranked          A1 A2 A3 B C D E F        limit 3, cap 1
    /// window 0        A1 B C        (A2 and A3 fail: A holds 1 of 1)
    /// window 1        A2 D E        (A3 fails again)          -> page 2 holds A2
    /// window 2        A3 F          (A3 passes in a new window, F)
    /// old rule        A1 B C | D E F | A2 A3     page 2 holds none of A's posts
    /// </code>
    /// </summary>
    [Fact]
    public async Task DVW5_ACappedAuthorsSecondPostIsOnPageTwoAndTheCountsAgree()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "board-" + Suffix();
        var term = "zdiv5" + Suffix();
        var prolific = ForumAgent.Create($"https://agents.example/prolific-{Suffix()}", "only-" + Suffix());
        var (dpop, token) = await prolific.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var strong = string.Join(' ', Enumerable.Repeat(term, 10));
        var authors = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 3; i++)
            authors.Add((await AskAsync(prolific, dpop, token, board, $"{term} {term} {term} {term} number {Number(i)}", $"{strong} {Guid.NewGuid():N} {Guid.NewGuid():N} {Guid.NewGuid():N}", ct)).PostId);
        for (var i = 0; i < 5; i++)
            await AskAloneAsync(board, $"{term} other {Number(i)}", $"{term} {Guid.NewGuid():N} {Guid.NewGuid():N} {Guid.NewGuid():N}", ct);

        using var whole = await SearchAsync($"q={term}&board={board}&limit=25&why=true", ct);
        var wholeIds = Ids(whole);
        Assert.Equal(8, wholeIds.Length);
        Assert.True(wholeIds.Take(3).Count(authors.Contains) >= 2, $"the scene did not rank the author's posts first: {string.Join(",", wholeIds.Take(3).Select(authors.Contains))}");

        var seen = new List<string>();
        var pages = new List<JsonDocument>();
        var diversified = new List<string>();
        var deferredFlags = 0;
        string? cursor = null;
        try
        {
            for (var guard = 0; guard < 10; guard++)
            {
                var page = await SearchAsync($"q={term}&board={board}&limit=3&why=true" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)), ct);
                pages.Add(page);
                seen.AddRange(Ids(page));
                diversified.Add(page.RootElement.TryGetProperty("diversified", out var d) ? d.ToString() : "member absent");
                deferredFlags += Hits(page).Count(h => h.GetProperty("why_ranked").GetProperty("deferred_by_diversification").GetBoolean());
                cursor = page.RootElement.TryGetProperty("next_cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (cursor is null) break;
            }

            Assert.Equal(wholeIds.Order(StringComparer.Ordinal), seen.Order(StringComparer.Ordinal));
            Assert.Equal(seen.Count, seen.Distinct(StringComparer.Ordinal).Count());

            Assert.True(
                diversified.Distinct(StringComparer.Ordinal).Count() == 1 && diversified[0] == Number(deferredFlags),
                $"Expected: diversified {Number(deferredFlags)} on every page (the hits flagged deferred_by_diversification across the walk)  Actual: diversified {string.Join(" | ", diversified)}");

            var pageTwo = Ids(pages[1]);
            Assert.True(pageTwo.Any(authors.Contains), $"Expected: the author's second post on page 2  Actual: page 2 holds {string.Join(",", pageTwo.Select(id => authors.Contains(id) ? "A" : "other"))}");

            Assert.True(Ids(pages[0]).Count(authors.Contains) == 1, $"Expected: one of the author's posts on page 1 at limit 3  Actual: {Ids(pages[0]).Count(authors.Contains)}");
        }
        finally
        {
            foreach (var page in pages) page.Dispose();
        }

        using var larger = await SearchAsync($"q={term}&board={board}&limit=6", ct);
        Assert.True(Ids(larger).Take(6).Count(authors.Contains) > 1, "Expected: page 1 at limit 6 holds more than one of the author's posts (the cap is a share of a window)");
    }
}
