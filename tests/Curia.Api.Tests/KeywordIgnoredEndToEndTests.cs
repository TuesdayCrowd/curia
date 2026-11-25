using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G81 (R9.72) over the wire, plan KWC9 and KWC10. The hashed adapter serves the Forum. The scene is two posts on one board:
/// <code>
/// pool    title "pool keepalive"   body "the pool needs keepalive"        tags it, ops
/// garden  title "garden notes"     body "the weather and the garden"
/// </code>
/// The probes read <c>keyword_ignored</c> from the wire JSON by name, so on the base the red is their own assertion. The list the member must stay inside is read from
/// <c>conformance/retrieval/stopwords.txt</c>, never from the build.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class KeywordIgnoredEndToEndTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static HashSet<string> List()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance"))) dir = dir.Parent;
        return [.. File.ReadAllText(Path.Combine(dir!.FullName, "conformance", "retrieval", "stopwords.txt")).Split('\n')];
    }

    private async Task<string> AskAloneAsync(string board, string title, string body, string[]? tags, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/asker-{Suffix()}", "asker-" + Suffix());
        var (dpop, token) = await agent.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        using var response = await dpop.PostAsync(forum.Client, PostsUrl, token, agent.SignQuestion(board, body, title, forum.Now, tags), forum.Now, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        using var accepted = JsonDocument.Parse(text);
        return accepted.RootElement.GetProperty("post_id").GetString()!;
    }

    private async Task<(string Board, string Pool, string Garden)> SceneAsync(CancellationToken ct)
    {
        var board = "kwc-" + Suffix();
        var pool = await AskAloneAsync(board, "pool keepalive", "the pool needs keepalive", ["it", "ops"], ct);
        var garden = await AskAloneAsync(board, "garden notes", "the weather and the garden", null, ct);
        return (board, pool, garden);
    }

    private async Task<JsonDocument> SearchAsync(string query, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/search?{query}", UriKind.Relative), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET /v1/search?{query} answered {(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text);
    }

    private static string[] Ids(JsonDocument page) =>
        [.. page.RootElement.GetProperty("results").EnumerateArray().Select(h => h.GetProperty("post").GetProperty("post_id").GetString()!)];

    private static string[]? Ignored(JsonDocument page) =>
        page.RootElement.TryGetProperty("keyword_ignored", out var member) && member.ValueKind == JsonValueKind.Array
            ? [.. member.EnumerateArray().Select(e => e.GetString()!)]
            : null;

    private static string Show(string[]? words) => words is null ? "member absent" : "[" + string.Join(", ", words) + "]";

    /// <summary>
    /// KWC9. <c>q=the</c> is a valid query of only listed words: <c>200</c>, <c>keyword_ignored</c> is <c>["the"]</c>, no result is marked <c>words</c> (the two posts that say <c>the</c> are not
    /// results), and <c>by_channel</c> is present (the request had a token: R9.67) with <c>words</c> 0.
    /// </summary>
    [Fact]
    public async Task KWC9_AQueryOfOnlyListedWordsIsServedEmptyByWordsAndStatesWhatItIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, _, _) = await SceneAsync(ct);

        using var page = await SearchAsync($"q=the&board={board}", ct);

        Assert.True(Ignored(page)?.SequenceEqual(["the"]) == true, $"Expected: keyword_ignored [the]  Actual: {Show(Ignored(page))}");
        Assert.True(page.RootElement.TryGetProperty("by_channel", out var counts) && counts.GetProperty("words").GetInt32() == 0, $"Expected: by_channel present with words 0  Actual: {page.RootElement.GetProperty("results").GetArrayLength()} results, by_channel {(page.RootElement.TryGetProperty("by_channel", out var c) ? c.ToString() : "absent")}");
        Assert.True(Ids(page).Length == 0, $"Expected: no result by words for \"the\"  Actual: {Ids(page).Length} results");
    }

    /// <summary>KWC9. <c>q=the+pool</c> ranks over <c>pool</c> alone: the pool post is the one result and the garden post, which only says <c>the</c>, is not; the ignored word is stated once, in the query's order.</summary>
    [Fact]
    public async Task KWC9_ATermAmongListedWordsListsOnlyThePostsHoldingItAndStatesTheIgnoredWordOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, pool, _) = await SceneAsync(ct);

        using var page = await SearchAsync($"q=the+pool+the&board={board}", ct);

        Assert.True(Ignored(page)?.SequenceEqual(["the"]) == true, $"Expected: keyword_ignored [the]  Actual: {Show(Ignored(page))}");
        Assert.True(Ids(page).SequenceEqual([pool]), $"Expected: exactly the pool post  Actual: {Ids(page).Length} results");
    }

    /// <summary>KWC9. A listing (no <c>q</c>) and a query that ignored nothing state <c>[]</c>: the member is present on every page of a Forum that applies the list, so absent means it does not.</summary>
    [Fact]
    public async Task KWC9_AListingAndAQueryThatIgnoredNothingStateAnEmptyArray()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, _, _) = await SceneAsync(ct);

        using var listing = await SearchAsync($"board={board}", ct);
        using var plain = await SearchAsync($"q=pool&board={board}", ct);

        Assert.True(Ignored(listing)?.Length == 0, $"Expected: a listing states keyword_ignored []  Actual: {Show(Ignored(listing))}");
        Assert.True(Ignored(plain)?.Length == 0, $"Expected: \"pool\" states keyword_ignored []  Actual: {Show(Ignored(plain))}");
    }

    /// <summary>KWC9. The member cannot carry content: whatever words a query holds, every word served is a member of the published list, lower-case, in the query's order.</summary>
    [Fact]
    public async Task KWC9_EveryWordServedIsAMemberOfThePublishedListInTheQuerysOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, _, _) = await SceneAsync(ct);

        using var page = await SearchAsync($"q=How+do+I+rotate+THE+key+how&board={board}", ct);

        var ignored = Ignored(page);
        Assert.True(ignored is not null, "Expected: keyword_ignored present  Actual: member absent");
        Assert.True(ignored.SequenceEqual(["how", "do", "i", "the"]), $"Expected: [how, do, i, the] (lower-case, first occurrence, query order)  Actual: {Show(ignored)}");
        Assert.All(ignored, w => Assert.Contains(w, List()));
    }

    /// <summary>
    /// KWC9. A filter is not a query term: a post tagged <c>it</c> is still found by <c>tags=it</c>, alone and beside a term (FKWC8). Green on the base for the wrong reason (nothing is filtered
    /// yet): it carries information only through FKWC8.
    /// </summary>
    [Fact]
    public async Task KWC9_APostTaggedWithAListedWordIsStillFoundByThatTag()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, pool, _) = await SceneAsync(ct);

        using var alone = await SearchAsync($"tags=it&board={board}", ct);
        using var beside = await SearchAsync($"q=pool&tags=it&board={board}", ct);

        Assert.True(Ids(alone).SequenceEqual([pool]), $"Expected: tags=it finds the pool post  Actual: {Ids(alone).Length} results");
        Assert.True(Ids(beside).SequenceEqual([pool]), $"Expected: q=pool with tags=it finds the pool post  Actual: {Ids(beside).Length} results");
    }

    /// <summary>
    /// KWC10, a control, green on the base and to stay green: the inbox passes no text, so a board filter lists the open question as it did (the search over the inbox is a listing before the stage and after).
    /// </summary>
    [Fact]
    public async Task KWC10_Control_TheInboxWithABoardFilterStillListsTheOpenQuestion()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, pool, garden) = await SceneAsync(ct);
        var reader = await AcceptedAnswerScene.EnrolAsync(forum, "inbox", ct);

        var token = await reader.Dpop.GetTokenAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/inbox?board={board}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("DPoP", token);
        request.Headers.Add("DPoP", reader.Dpop.Proof("GET", "http://localhost/v1/inbox", forum.Now, token, nonce: null));
        using var response = await forum.Client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);

        var ids = JsonDocument.Parse(text).RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post_id").GetString()).Order().ToArray();
        Assert.True(ids.SequenceEqual(new[] { pool, garden }.Order()), $"Expected: both open questions of the board  Actual: {ids.Length} rows");
    }
}
