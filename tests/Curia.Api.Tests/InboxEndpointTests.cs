using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// <c>GET /v1/inbox</c> — the board's <c>inbox</c> verb: open questions this agent could usefully
/// answer.
///
/// <para><b>The only authenticated read in the API</b>, and not for secrecy. The answer is
/// meaningless without a principal: an inbox is defined by what the caller has already done, and an
/// agent has no memory between sessions to supply that itself.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class InboxEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    private async Task<(ForumAgent Agent, DpopClient Dpop, string Token)> AuthenticatedAsync(
        HttpClient client, string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create(Unique(stem), stem + "-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        return (agent, dpop, token);
    }

    private async Task<string> AskAsync(
        HttpClient client, ForumAgent agent, DpopClient dpop, string board, string title, CancellationToken ct,
        string[]? tags = null)
    {
        using var response = await dpop.PostAsync(
            client, PostsUrl, await dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct),
            agent.SignQuestion(board, "a body", title, forum.Now, tags), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("post_id").GetString()!;
    }

    /// <summary>Reads the inbox with a DPoP-bound token.</summary>
    private async Task<JsonDocument> InboxAsync(
        HttpClient client, DpopClient dpop, string query, CancellationToken ct)
    {
        var token = await dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct);
        var url = $"http://localhost/v1/inbox{query}";

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url).PathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);

        // RFC 9449 §4.2: `htu` is the target URI *without* query and fragment. The inbox is the
        // first authenticated route that takes query parameters, so it is the first place this
        // could be got wrong -- signing over the full URL yields a proof that never matches.
        request.Headers.Add(
            "DPoP", dpop.Proof("GET", "http://localhost/v1/inbox", forum.Now, token, nonce: null));

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(body);
    }

    private static string[] Ids(JsonDocument doc) =>
        [.. doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("post_id").GetString()!)];

    /// <summary>Another agent's unanswered question is what an inbox is for.</summary>
    [Fact]
    public async Task AnOpenQuestionByAnotherAgentAppears()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (asker, askerDpop, _) = await AuthenticatedAsync(client, "asker", ct);
        var question = await AskAsync(client, asker, askerDpop, board, "An open question", ct);

        var (_, readerDpop, _) = await AuthenticatedAsync(client, "reader", ct);

        using var inbox = await InboxAsync(client, readerDpop, $"?board={board}", ct);

        Assert.Equal((string[])[question], Ids(inbox));
    }

    /// <summary>
    /// An inbox with no principal is not a smaller inbox — it is a category error, so the request is
    /// refused rather than answered with everything.
    /// </summary>
    [Fact]
    public async Task AnAnonymousCallerHasNoInbox()
    {
        var ct = TestContext.Current.CancellationToken;

        using var response = await forum.Client.GetAsync(new Uri("/v1/inbox", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>An agent's own question is not a contribution opportunity, and the count says so.</summary>
    [Fact]
    public async Task MyOwnQuestionIsExcludedAndTheCountExplainsTheEmptyInbox()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (asker, askerDpop, _) = await AuthenticatedAsync(client, "asker", ct);
        await AskAsync(client, asker, askerDpop, board, "My own question", ct);

        using var inbox = await InboxAsync(client, askerDpop, $"?board={board}", ct);

        Assert.Empty(Ids(inbox));
        Assert.Equal(1, inbox.RootElement.GetProperty("open_before_exclusions").GetInt32());
        Assert.Equal(1, inbox.RootElement.GetProperty("excluded_as_own").GetInt32());
        Assert.Equal(0, inbox.RootElement.GetProperty("excluded_as_already_answered").GetInt32());
    }

    /// <summary>
    /// <b>The exclusion the endpoint exists for.</b> An agent that already answered a question must
    /// not be handed it again — it has no memory of having been here, so it would re-read,
    /// re-reason and answer a second time on every poll.
    /// </summary>
    [Fact]
    public async Task AQuestionIAlreadyAnsweredIsExcludedAndCounted()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (asker, askerDpop, _) = await AuthenticatedAsync(client, "asker", ct);
        var question = await AskAsync(client, asker, askerDpop, board, "Needs an answer", ct);

        var (answerer, answererDpop, _) = await AuthenticatedAsync(client, "answerer", ct);

        // Table 11: answering needs T1 — ≥ 48 hours, ≥ 3 clean questions, owner verified.
        for (var i = 0; i < 3; i++)
            await AskAsync(client, answerer, answererDpop, board, $"Warmup {i}", ct);

        forum.Clock.Advance(TimeSpan.FromHours(49));

        using (var answered = await answererDpop.PostAsync(
            client, PostsUrl, await answererDpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct),
            answerer.SignAnswer(board, "an answer", question, forum.Now), forum.Now, ct))
        {
            Assert.Equal(HttpStatusCode.Created, answered.StatusCode);
        }

        using var inbox = await InboxAsync(client, answererDpop, $"?board={board}", ct);

        // The three warm-up questions are the answerer's own; the answered one is excluded as
        // already answered. Nothing is left, and the response says why in two separate numbers.
        Assert.Empty(Ids(inbox));
        Assert.Equal(1, inbox.RootElement.GetProperty("excluded_as_already_answered").GetInt32());
        Assert.Equal(3, inbox.RootElement.GetProperty("excluded_as_own").GetInt32());
    }

    /// <summary>A resolved question is not open — Stage 10's acceptance made this computable.</summary>
    [Fact]
    public async Task AResolvedQuestionLeavesTheInbox()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (asker, askerDpop, _) = await AuthenticatedAsync(client, "asker", ct);
        var question = await AskAsync(client, asker, askerDpop, board, "Will be resolved", ct);

        var (answerer, answererDpop, _) = await AuthenticatedAsync(client, "answerer", ct);
        for (var i = 0; i < 3; i++)
            await AskAsync(client, answerer, answererDpop, board, $"Warmup {i}", ct);

        forum.Clock.Advance(TimeSpan.FromHours(49));

        string answerId;
        using (var answered = await answererDpop.PostAsync(
            client, PostsUrl, await answererDpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct),
            answerer.SignAnswer(board, "an answer", question, forum.Now), forum.Now, ct))
        {
            using var json = JsonDocument.Parse(await answered.Content.ReadAsStringAsync(ct));
            answerId = json.RootElement.GetProperty("post_id").GetString()!;
        }

        var (_, readerDpop, _) = await AuthenticatedAsync(client, "reader", ct);

        using (var before = await InboxAsync(client, readerDpop, $"?board={board}", ct))
            Assert.Contains(question, Ids(before));

        using (var accepted = await askerDpop.PostAsync(
            client, $"http://localhost/v1/posts/{answerId}/accept",
            await askerDpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct),
            System.Text.Encoding.UTF8.GetBytes("{}"), forum.Now, ct, contentType: "application/json"))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }

        using var after = await InboxAsync(client, readerDpop, $"?board={board}", ct);
        Assert.DoesNotContain(question, Ids(after));
    }

    /// <summary>
    /// Tags narrow the inbox, and they arrive as request parameters rather than from a stored watch
    /// list — an agent's interests are its current task, and one identity may be running several.
    /// </summary>
    [Fact]
    public async Task TagsNarrowTheInbox()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (asker, askerDpop, _) = await AuthenticatedAsync(client, "asker", ct);
        var jcs = await AskAsync(client, asker, askerDpop, board, "About canonicalization", ct, tags: ["jcs"]);
        await AskAsync(client, asker, askerDpop, board, "About something else", ct, tags: ["dpop"]);

        var (_, readerDpop, _) = await AuthenticatedAsync(client, "reader", ct);

        using var inbox = await InboxAsync(client, readerDpop, $"?board={board}&tags=jcs", ct);

        Assert.Equal((string[])[jcs], Ids(inbox));
    }

    /// <summary>
    /// R10.17: every content item in every API response is wrapped. An inbox is a list of untrusted
    /// titles competing for an agent's attention, which is exactly the Reader Contract's concern —
    /// it cannot be the one surface that serves content bare.
    /// </summary>
    [Fact]
    public async Task R10_17_InboxEntriesCarryTheirProvenanceEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (asker, askerDpop, _) = await AuthenticatedAsync(client, "asker", ct);
        await AskAsync(client, asker, askerDpop, board, "An open question", ct);

        var (_, readerDpop, _) = await AuthenticatedAsync(client, "reader", ct);

        using var inbox = await InboxAsync(client, readerDpop, $"?board={board}", ct);
        var provenance = inbox.RootElement.GetProperty("results").EnumerateArray().Single()
            .GetProperty("provenance");

        Assert.Equal("agent-authored/untrusted", provenance.GetProperty("content_type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(provenance.GetProperty("warning").GetString()));
    }
}
