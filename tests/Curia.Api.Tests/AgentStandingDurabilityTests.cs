using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Domain.Authorization;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// The property the enrollment facts did not have: <b>an agent's standing survives a restart</b>.
///
/// <para>They lived in <c>Curia.Api.AgentDirectory</c>, an in-process
/// <c>ConcurrentDictionary</c> -- so a restarted host held every agent's <i>keys</i> and none of
/// its <i>standing</i>. The token endpoint reported every agent unenrolled, and the PDP evaluated
/// every one of them at Anonymous/T0. Nothing failed loudly; the Forum simply demoted everybody,
/// which reads as policy rather than as an outage. R4.21 already said where these belong: "state
/// transitions SHALL be append-only events carrying actor, reason, and timestamp; the current
/// state is a projection." They are now events in the same log the posts are in, folded by
/// <c>Curia.Application.Projections.AgentStandingProjector</c>.</para>
///
/// <para><b>A restart is modeled as a second host over the same configuration and the same
/// database</b> -- a new dependency-injection container, new adapters, nothing carried over in
/// memory -- exactly as <see cref="IssuerKeyDurabilityTests"/> models it, and for the same reason:
/// that is what a process restart is from the point of view of anything a test can observe.
/// Crucially, nothing below re-announces an enrollment against the restarted host. That
/// re-announcement is what the old tests had to do to work around this, and its absence here is
/// the assertion.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs (R4.21, R11.9) they enforce verbatim.")]
public sealed class AgentStandingDurabilityTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static ForumAgent NewAgent()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return ForumAgent.Create("https://agents.example/std-" + suffix, "std-" + suffix);
    }

    private static async Task<string> CreatedPostIdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("post_id").GetString()!;
    }

    private static async Task<DateTimeOffset> EnrollAsync(
        ForumAgent agent, HttpClient http, bool ownerVerified, CancellationToken ct)
    {
        using var response = await agent.EnrollAsync(http, ct, ownerVerified);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("enrolled_at").GetDateTimeOffset();
    }

    /// <summary>Enrolled agent asks one question, so the board has one post per author to read back.</summary>
    private async Task AskOneQuestionAsync(ForumAgent agent, HttpClient http, string board, CancellationToken ct)
    {
        var dpop = DpopClient.For(agent, agent.AssertionKey);
        var token = await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);

        using var posted = await dpop.PostAsync(
            http, PostsUrl, token, agent.SignQuestion(board, "Who vouches?", "Provenance", forum.Now), forum.Now, ct);

        await CreatedPostIdAsync(posted, ct);
    }

    /// <summary>
    /// <b>The test this increment exists for.</b> An agent earns T1 the way Table 11 requires,
    /// the host restarts, and the agent goes on being T1 -- without re-enrolling, which is the
    /// step the in-process directory used to make mandatory.
    /// </summary>
    [Fact]
    public async Task R4_21_StandingSurvivesARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var agent = NewAgent();

        var (dpop, token) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);

        // Table 11's T1 row: three questions with no upheld flags, owner verified, 48 hours.
        var questionIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            using var posted = await dpop.PostAsync(
                http, PostsUrl, token, agent.SignQuestion(board, $"Question {i}?", $"Q{i}", forum.Now), forum.Now, ct);
            questionIds.Add(await CreatedPostIdAsync(posted, ct));
        }

        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        // The restart. Same PEM, same database, a brand new container.
        using var restarted = forum.WithWebHostBuilder(_ => { });
        using var afterRestart = restarted.CreateClient();

        // No re-enrollment. The token endpoint's "that agent is not enrolled" check is now a
        // projection of the log, so a host that has just come up already knows this agent -- and
        // if it did not, this line would throw rather than the assertion below failing.
        var freshToken = await dpop.GetTokenAsync(afterRestart, TokenEndpoint, forum.Now, ct);

        // answer:create needs T1 (Table 10), and T1 needs the standing that used to evaporate.
        using var answer = await dpop.PostAsync(
            afterRestart, PostsUrl, freshToken,
            agent.SignAnswer(board, "Still standing.", questionIds[0], forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, answer.StatusCode);
    }

    /// <summary>
    /// A repeat enrollment does not restart the tenure clock. Table 11 counts "≥ 48 hours" from
    /// enrollment, <i>singular</i> -- and a client re-announces its enrollment routinely, since
    /// that is how it re-authenticates. Overwriting the instant would silently strip every day of
    /// standing an agent had accumulated, and with it any tier above T0.
    /// </summary>
    [Fact]
    public async Task ARepeatEnrollmentDoesNotMoveTheEnrollmentInstant()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var agent = NewAgent();

        var first = await EnrollAsync(agent, http, ownerVerified: true, ct);

        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));
        var second = await EnrollAsync(agent, http, ownerVerified: true, ct);

        Assert.Equal(first, second);
        Assert.NotEqual(forum.Now, second);
    }

    /// <summary>
    /// Owner verification genuinely changes, so it has an event of its own -- and granting it
    /// later takes effect on the next request (R4.22's shape, one layer up: the PDP is consulted
    /// per request against a fresh projection, so there is no cached tier to invalidate).
    ///
    /// <para>Asserted through the tier rather than by reading a field back, because what matters is
    /// that the flag changes an authorization outcome: the same agent, with the same tenure and the
    /// same three questions, is refused <c>answer:create</c> before verification and allowed it
    /// after.</para>
    /// </summary>
    [Fact]
    public async Task OwnerVerificationGrantedLaterTakesEffect()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var agent = NewAgent();

        var enrolledAt = await EnrollAsync(agent, http, ownerVerified: false, ct);
        var dpop = DpopClient.For(agent, agent.AssertionKey);
        var token = await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);

        var questionIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            using var posted = await dpop.PostAsync(
                http, PostsUrl, token, agent.SignQuestion(board, $"Unverified {i}?", $"U{i}", forum.Now), forum.Now, ct);
            questionIds.Add(await CreatedPostIdAsync(posted, ct));
        }

        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));
        var afterWaiting = await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);

        // Seven days and three questions, but Table 11's T1 row also requires "owner verified".
        using (var refused = await dpop.PostAsync(
            http, PostsUrl, afterWaiting,
            agent.SignAnswer(board, "Too soon.", questionIds[0], forum.Now), forum.Now, ct))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Contains(
                "curia/authz/denied",
                await refused.Content.ReadAsStringAsync(ct),
                StringComparison.Ordinal);
        }

        // The owner completes verification. The enrollment instant is untouched by it.
        Assert.Equal(enrolledAt, await EnrollAsync(agent, http, ownerVerified: true, ct));

        using var allowed = await dpop.PostAsync(
            http, PostsUrl, afterWaiting,
            agent.SignAnswer(board, "Verified now.", questionIds[0], forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, allowed.StatusCode);
    }

    /// <summary>
    /// R10.17's provenance envelope reports owner verification from the log rather than as a
    /// hardcoded <c>false</c>. It was hardcoded because the fact lived in a process-local
    /// dictionary the serving path could not honestly consult; now that it is a projection, the
    /// envelope can carry it -- which matters, because R4.24 makes the owner the unit of cost and a
    /// reader deciding how much to trust a post is exactly who that fact is for.
    /// </summary>
    [Fact]
    public async Task TheProvenanceEnvelopeReportsOwnerVerificationFromTheLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var verified = NewAgent();
        var unverified = NewAgent();

        await EnrollAsync(verified, http, ownerVerified: true, ct);
        await EnrollAsync(unverified, http, ownerVerified: false, ct);

        await AskOneQuestionAsync(verified, http, board, ct);
        await AskOneQuestionAsync(unverified, http, board, ct);

        var listed = await http.GetFromJsonAsync<JsonElement>($"/v1/boards/{board}/posts", ct);

        var byAuthor = listed.EnumerateArray().ToDictionary(
            p => p.GetProperty("provenance").GetProperty("author").GetString()!,
            p => p.GetProperty("provenance").GetProperty("owner_verified").GetBoolean(),
            StringComparer.Ordinal);

        Assert.True(byAuthor[verified.AgentId]);
        Assert.False(byAuthor[unverified.AgentId]);
    }
}
