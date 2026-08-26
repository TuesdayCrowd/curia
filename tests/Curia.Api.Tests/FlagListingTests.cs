using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R7.18 and R10.44 over the running Forum — the eleventh of the local board's verbs, and the last
/// one that was unserved.
///
/// <para>It was not unserved because nobody had written it. Table 10 had no <c>flag</c>/<c>list</c>
/// cell, so a listing route would have had to be authorized against a pair
/// <c>ResourceActionModel.RowFor</c> reports as a <i>failure</i> — deliberately, so a missing row
/// cannot masquerade as a considered denial. Errata G3 argued the cell and R7.18 scoped it.</para>
///
/// <para><b>Requires a reachable Postgres</b>, and fails loudly rather than skipping, for the
/// reason <see cref="ForumFixture"/> records.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class FlagListingTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    private static byte[] FlagBody(string kind, string rationale) => Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { kind, rationale }));

    private sealed record Party(ForumAgent Agent, DpopClient Dpop, string Token);

    private async Task<Party> AgentAsync(HttpClient client, string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create(Unique(stem), $"{stem}-{Guid.NewGuid().ToString("N")[..8]}");
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        return new Party(agent, dpop, token);
    }

    private async Task<string> PostQuestionAsync(HttpClient client, Party party, string board, CancellationToken ct)
    {
        var wire = party.Agent.SignQuestion(board, "How does JCS order object members?", "Member ordering", forum.Now);
        using var response = await party.Dpop.PostAsync(client, PostsUrl, party.Token, wire, forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("post_id").GetString()!;
    }

    private async Task RaiseAsync(
        HttpClient client, Party party, string postId, string kind, string rationale, CancellationToken ct)
    {
        using var response = await party.Dpop.PostAsync(
            client,
            $"http://localhost/v1/posts/{postId}/flags",
            party.Token,
            FlagBody(kind, rationale),
            forum.Now,
            ct,
            contentType: "application/json");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>A DPoP-bound GET, returning the raw response so a test can assert on its status.</summary>
    private async Task<(HttpStatusCode Status, string Body)> GetAsync(
        HttpClient client, Party party, string path, CancellationToken ct)
    {
        var url = $"http://localhost{path}";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url).PathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", party.Token);
        request.Headers.Add("DPoP", party.Dpop.Proof("GET", url, forum.Now, party.Token, nonce: null));

        using var response = await client.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    // ---- Authentication and the (own) parenthetical -------------------------------------------

    /// <summary>
    /// Table 10's <c>flag</c>/<c>list</c> row is <c>✗</c> in the Anonymous column, and R7.18 gives
    /// the reason it is not merely caution: an anonymous principal authors nothing and raises
    /// nothing, so "own" is empty for it and a grant would confer access to no flag that can exist.
    /// PEP-1 refuses before the PDP is consulted, because there is no principal to decide about.
    /// </summary>
    [Fact]
    public async Task R7_18_AnAnonymousCallerMayNotListFlags()
    {
        var ct = TestContext.Current.CancellationToken;

        using var mine = await forum.Client.GetAsync(new Uri("/v1/flags", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, mine.StatusCode);

        using var onPost = await forum.Client.GetAsync(new Uri("/v1/posts/anything/flags", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.Unauthorized, onPost.StatusCode);
    }

    /// <summary>
    /// R7.18's first set: "flags the requesting agent raised". A T0 agent may raise (R10.35), so it
    /// must be able to read back what it raised — the capability is useless otherwise.
    /// </summary>
    [Fact]
    public async Task R7_18_AnAgentSeesTheFlagsItRaised()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var author = await AgentAsync(client, "author", ct);
        var reporter = await AgentAsync(client, "reporter", ct);
        var postId = await PostQuestionAsync(client, author, board, ct);

        await RaiseAsync(client, reporter, postId, "spam", "this reads as spam", ct);

        var (status, body) = await GetAsync(client, reporter, "/v1/flags", ct);
        Assert.Equal(HttpStatusCode.OK, status);

        using var json = JsonDocument.Parse(body);
        var flags = json.RootElement.GetProperty("flags").EnumerateArray().ToArray();

        Assert.Single(flags);
        Assert.Equal(postId, flags[0].GetProperty("post_id").GetString());
        Assert.Equal("spam", flags[0].GetProperty("kind").GetString());
    }

    /// <summary>
    /// The other side of "own", and the one that would make the route a disclosure if it were
    /// wrong: one agent's listing must not contain another agent's flag. Without this the route
    /// would publish the accuser graph R10.44 exists to keep private.
    /// </summary>
    [Fact]
    public async Task R7_18_AnAgentDoesNotSeeFlagsAnotherAgentRaised()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var author = await AgentAsync(client, "author", ct);
        var reporter = await AgentAsync(client, "reporter", ct);
        var bystander = await AgentAsync(client, "bystander", ct);
        var postId = await PostQuestionAsync(client, author, board, ct);

        await RaiseAsync(client, reporter, postId, "spam", "this reads as spam", ct);

        var (status, body) = await GetAsync(client, bystander, "/v1/flags", ct);
        Assert.Equal(HttpStatusCode.OK, status);

        using var json = JsonDocument.Parse(body);
        Assert.Empty(json.RootElement.GetProperty("flags").EnumerateArray());
    }

    /// <summary>
    /// R7.18's second set: "flags raised against posts the requesting agent authored". The author is
    /// the only party who can fix the content, and revision is available from T0 up, so a post
    /// corrected before review is the cheapest moderation the system has.
    /// </summary>
    [Fact]
    public async Task R7_18_AnAuthorSeesFlagsRaisedAgainstItsOwnPost()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var author = await AgentAsync(client, "author", ct);
        var reporter = await AgentAsync(client, "reporter", ct);
        var postId = await PostQuestionAsync(client, author, board, ct);

        await RaiseAsync(client, reporter, postId, "incorrect", "the ordering claim is wrong", ct);

        var (status, body) = await GetAsync(client, author, $"/v1/posts/{postId}/flags", ct);
        Assert.Equal(HttpStatusCode.OK, status);

        using var json = JsonDocument.Parse(body);
        var flags = json.RootElement.GetProperty("flags").EnumerateArray().ToArray();

        Assert.Single(flags);
        Assert.Equal("incorrect", flags[0].GetProperty("kind").GetString());
    }

    /// <summary>
    /// The parenthetical, enforced. R7.18 sends any other party's flag to <c>moderation</c>/<c>list</c>,
    /// which Table 10 grants only under R10.36's delegated grant — so a T0..T3 agent asking about a
    /// post it did not write is denied, and the denial names <i>which</i> parenthetical went
    /// unsatisfied rather than reporting a bare tier refusal (R7.16).
    /// </summary>
    [Fact]
    public async Task R7_18_ANonAuthorMayNotListFlagsOnAnotherAgentsPost()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var author = await AgentAsync(client, "author", ct);
        var reporter = await AgentAsync(client, "reporter", ct);
        var postId = await PostQuestionAsync(client, author, board, ct);

        await RaiseAsync(client, reporter, postId, "spam", "this reads as spam", ct);

        // Even the agent that raised the flag may not read it through the post it names: that view
        // is the author's. Its own listing is where it sees what it raised.
        var (status, body) = await GetAsync(client, reporter, $"/v1/posts/{postId}/flags", ct);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Contains("table-10/own-resource-only", body, StringComparison.Ordinal);
    }

    /// <summary>A post that does not exist is a 404, decided before the ownership question.</summary>
    [Fact]
    public async Task R7_18_AnUnknownPostIsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var party = await AgentAsync(forum.Client, "seeker", ct);

        var (status, _) = await GetAsync(forum.Client, party, "/v1/posts/01JQZZZZZZZZZZZZZZZZZZZZZZ/flags", ct);
        Assert.Equal(HttpStatusCode.NotFound, status);
    }

    // ---- R10.44's field discipline ------------------------------------------------------------

    /// <summary>
    /// R10.44, and the probe that carries information rather than restating the projection.
    ///
    /// <para>Asserting that <c>RaisedFlag</c> has no rationale field would be a restatement of the
    /// type — same artifact, no independent evidence. This raises a flag whose rationale is a
    /// distinctive nonce and then asserts that nonce appears in <b>no byte</b> of either listing
    /// response, which is a claim about what the serving path actually emits.</para>
    ///
    /// <para>It also asserts the raiser's identifier is absent. R10.44 forbids identifying the agent
    /// that raised a flag, and the accuser graph is what R4.3 keeps non-public for authorship —
    /// naming the raiser here would publish it one object over.</para>
    /// </summary>
    [Fact]
    public async Task R10_44_AServedFlagCarriesNeitherRationaleNorRaiser()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var author = await AgentAsync(client, "author", ct);
        var reporter = await AgentAsync(client, "reporter", ct);
        var postId = await PostQuestionAsync(client, author, board, ct);

        var nonce = "rationale-nonce-" + Guid.NewGuid().ToString("N");
        await RaiseAsync(client, reporter, postId, "spam", nonce, ct);

        var (authorStatus, authorBody) = await GetAsync(client, author, $"/v1/posts/{postId}/flags", ct);
        var (mineStatus, mineBody) = await GetAsync(client, reporter, "/v1/flags", ct);

        Assert.Equal(HttpStatusCode.OK, authorStatus);
        Assert.Equal(HttpStatusCode.OK, mineStatus);

        // The flag is genuinely there in both -- otherwise the two absence assertions below would
        // hold vacuously, which is the failure this project keeps rediscovering in its own probes.
        Assert.Single(JsonDocument.Parse(authorBody).RootElement.GetProperty("flags").EnumerateArray());
        Assert.Single(JsonDocument.Parse(mineBody).RootElement.GetProperty("flags").EnumerateArray());

        Assert.DoesNotContain(nonce, authorBody, StringComparison.Ordinal);
        Assert.DoesNotContain(nonce, mineBody, StringComparison.Ordinal);

        Assert.DoesNotContain(reporter.Agent.AgentId, authorBody, StringComparison.Ordinal);
        Assert.DoesNotContain(reporter.Agent.AgentId, mineBody, StringComparison.Ordinal);
        Assert.DoesNotContain("raised_by", authorBody, StringComparison.Ordinal);
        Assert.DoesNotContain("raised_by", mineBody, StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.44's positive half: post, category and instant, and the instant is the store's
    /// <c>server_ts</c> (R6.5's Forum observation) rather than anything the raiser supplied.
    /// </summary>
    [Fact]
    public async Task R10_44_AServedFlagCarriesPostCategoryAndInstant()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var author = await AgentAsync(client, "author", ct);
        var reporter = await AgentAsync(client, "reporter", ct);
        var postId = await PostQuestionAsync(client, author, board, ct);

        await RaiseAsync(client, reporter, postId, "credential_leak", "looks like a live key", ct);

        var (status, body) = await GetAsync(client, reporter, "/v1/flags", ct);
        Assert.Equal(HttpStatusCode.OK, status);

        var flag = JsonDocument.Parse(body).RootElement.GetProperty("flags").EnumerateArray().Single();

        Assert.Equal(postId, flag.GetProperty("post_id").GetString());
        Assert.Equal("credential_leak", flag.GetProperty("kind").GetString());

        var raisedAt = flag.GetProperty("raised_at").GetString();
        Assert.False(string.IsNullOrWhiteSpace(raisedAt));
        Assert.True(DateTimeOffset.TryParse(
            raisedAt, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out _));

        // Exactly three members: a field added later without deciding whether R10.44 permits it
        // fails here rather than shipping.
        Assert.Equal(3, flag.EnumerateObject().Count());
    }
}
