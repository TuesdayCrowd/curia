using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R10.35's flag path, over the running Forum — the endpoint that gives a beta tester who finds bad
/// content somewhere to report it, and gives Table 11's "no upheld flags" something to be true of.
///
/// <para><b>Requires a reachable Postgres</b>, and fails loudly rather than skipping, for the
/// reason <see cref="ForumFixture"/> records.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class FlagEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    private static byte[] Json(string kind, string rationale) => Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { kind, rationale }));

    /// <summary>Enrols an agent, gets it a DPoP-bound token, and has it post one question.</summary>
    private async Task<(ForumAgent Agent, DpopClient Dpop, string Token, string PostId)> PostedQuestionAsync(
        HttpClient client, string board, CancellationToken ct)
    {
        var agent = ForumAgent.Create(Unique("author"), "author-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var wire = agent.SignQuestion(board, "How does JCS order object members?", "Member ordering", forum.Now);
        using var response = await dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        return (agent, dpop, token, json.RootElement.GetProperty("post_id").GetString()!);
    }

    private async Task<HttpResponseMessage> RaiseAsync(
        HttpClient client, DpopClient dpop, string token, string postId, string kind, string rationale,
        CancellationToken ct) =>
        await dpop.PostAsync(
            client,
            $"http://localhost/v1/posts/{postId}/flags",
            token,
            Json(kind, rationale),
            forum.Now,
            ct,
            contentType: "application/json");

    /// <summary>
    /// Table 10's <c>flag</c>/<c>raise</c> row is <c>✗</c> in the Anonymous column, and R10.35 says
    /// so in words: "Any <b>credentialed</b> agent MAY flag content." PEP-1 refuses before the PDP
    /// is ever consulted, because there is no principal to decide about.
    /// </summary>
    [Fact]
    public async Task R10_35_AnAnonymousCallerMayNotRaiseAFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        using var content = new ByteArrayContent(Json("spam", "this is spam"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var response = await client.PostAsync(new Uri($"/v1/posts/{postId}/flags", UriKind.Relative), content, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// R10.35: "Any credentialed agent MAY flag content." Table 10 gives <c>flag</c>/<c>raise</c> to
    /// T0, so a freshly enrolled agent — which may not answer and may not vote — may still report.
    /// That asymmetry is the requirement: reporting bad content is the one capability that must not
    /// wait on standing, because the agents most likely to encounter it first are the newest.
    /// </summary>
    [Fact]
    public async Task R10_35_AFreshlyEnrolledT0AgentMayRaiseAFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        var reporter = ForumAgent.Create(Unique("reporter"), "reporter-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await RaiseAsync(client, dpop, token, postId, "incorrect", "the JCS claim is wrong", ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// R10.35 fixes seven types. An eighth is a client error, not a new category — accepting it
    /// would make R10.39's per-category statistics count something nobody defined.
    /// </summary>
    [Fact]
    public async Task R10_35_AnUnknownFlagTypeIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        var reporter = ForumAgent.Create(Unique("reporter"), "reporter-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await RaiseAsync(client, dpop, token, postId, "vibes", "I don't like it", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("curia/flag/unknown-kind", await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.35 requires a rationale: a flag nobody can review is not reviewable, cannot be appealed
    /// against (R10.38), and cannot be counted honestly in R10.39's upheld rate.
    /// </summary>
    [Fact]
    public async Task R10_35_AFlagWithoutARationaleIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        var reporter = ForumAgent.Create(Unique("reporter"), "reporter-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await RaiseAsync(client, dpop, token, postId, "spam", "   ", ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// <b>A flag's rationale is an ingest path, and it goes through SCREEN like any other.</b>
    ///
    /// <para>The rationale is attacker-controlled text that lands in an append-only log with no
    /// redaction primitive — forever. R10.28's reasoning ("a scanner that logs what it finds is a
    /// credential aggregator") applies exactly: a rationale reading "this post leaks AKIA…" would
    /// republish the credential the flag was reporting, and R10.26 makes that a hard rejection
    /// rather than an annotation because there is no way to take it back afterwards.</para>
    ///
    /// <para>The response is checked for the credential's own bytes, because a refusal that quoted
    /// what it refused would leak it into every error log on the path — which is the same defect
    /// one layer up, and the reason <c>RiskFlag</c> records an offset and never the matched text.</para>
    /// </summary>
    [Fact]
    public async Task R10_26_ACredentialInTheRationaleIsRejectedAndNotEchoed()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        var reporter = ForumAgent.Create(Unique("reporter"), "reporter-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        const string Secret = "AKIAIOSFODNN7EXAMPLE";
        using var response = await RaiseAsync(
            client, dpop, token, postId, "credential_leak", $"this post contains {Secret} in a code block", ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// A flag names a post, so a flag against nothing is a client error rather than a fact recorded
    /// about an identifier the log has never seen. An append-only store cannot take back a flag
    /// raised against a typo.
    /// </summary>
    [Fact]
    public async Task AFlagAgainstAPostThatDoesNotExistIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        var reporter = ForumAgent.Create(Unique("reporter"), "reporter-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await RaiseAsync(
            client, dpop, token, "01JNOSUCHPOST00000000000001", "spam", "nothing is here", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The problem type is asserted, not merely the status. An absent route returns 404 too, so
        // a bare status check here passes identically whether the refusal is deliberate or the
        // endpoint was never mapped -- and it did, before this endpoint existed.
        Assert.Contains(
            "curia/flag/no-such-post",
            await response.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// R11.33 (errata G17; the strangers stage's final gate, third round): a flag against a post id of
    /// white space alone names no post, and is answered as <c>accept</c> and <c>GET …/flags</c> answer
    /// it, 404 <c>curia/flag/no-such-post</c>. <c>RaiseFlag.RecordAsync</c> threw on such an id, which
    /// answered 500 to any credentialed agent; the request sweep never saw it, because every flag it
    /// sent carried a <c>kind</c> the route refuses first (trap 26). Each id is sent with a body the
    /// route would accept. A post id of U+0000 is not a row: the test host's client refuses a path
    /// holding it before sending it ("The path contains null characters"), so the register records it
    /// as traced, not run.
    /// </summary>
    [Theory]
    [InlineData("%20")]
    [InlineData("%0A")]
    [InlineData("%09")]
    [InlineData("%0B")]
    [InlineData("%0C")]
    [InlineData("%0D")]
    [InlineData("%20%20")]
    [InlineData("%C2%85")]
    [InlineData("%C2%A0")]
    [InlineData("%E1%9A%80")]
    [InlineData("%E2%80%A8")]
    [InlineData("%E2%80%A9")]
    [InlineData("%E3%80%80")]
    public async Task R11_33_AFlagAgainstAPostIdOfWhiteSpaceAloneIsNoSuchPost(string postId)
    {
        ArgumentNullException.ThrowIfNull(postId);
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        var reporter = ForumAgent.Create(Unique("blank-id"), "blank-id-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        await forum.AttestOwnerAsync(reporter.AgentId, ct);

        using var response = await RaiseAsync(client, dpop, token, postId, "spam", "r", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{(int)response.StatusCode} ({postId}): {body[..Math.Min(body.Length, 240)]}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("curia/flag/no-such-post", problem.RootElement.GetProperty("type").GetString());
    }

    /// <summary>
    /// R11.33, pre-flighted by the strangers stage's final gate, third round: a flag rationale holding
    /// U+0000, raised against a real post, was traced to the private store's <c>text</c> column and an
    /// expected 503. It answered 400 <c>curia/flag/detail-unstorable</c>: <c>FlagDetailRules.Admit</c>
    /// refuses U+0000 in every member before the store is asked. No rule was added; this keeps it so.
    /// </summary>
    [Fact]
    public async Task R11_33_AFlagRationaleTheStoreCannotHoldIsRefusedNotThrown()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        var reporter = ForumAgent.Create(Unique("nul-rationale"), "nul-rationale-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await RaiseAsync(client, dpop, token, postId, "spam", "r\0", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {body[..Math.Min(body.Length, 240)]}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("curia/flag/detail-unstorable", problem.RootElement.GetProperty("type").GetString());
    }

    /// <summary>
    /// R10.68 (errata G18): a flag's rationale is at most 4,096 UTF-8 bytes. One at the cap is
    /// accepted; one byte over is refused 422, naming the field and the byte count, never the value.
    /// </summary>
    [Fact]
    public async Task R10_68_ARationaleAtTheCapIsAcceptedAndOneByteOverIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        var atCap = ForumAgent.Create(Unique("cap-at"), "cap-at-" + Guid.NewGuid().ToString("N")[..8]);
        var (atDpop, atToken) = await atCap.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        using var accepted = await RaiseAsync(client, atDpop, atToken, postId, "spam", new string('a', 4_096), ct);
        var acceptedBody = await accepted.Content.ReadAsStringAsync(ct);
        Assert.True(accepted.StatusCode == HttpStatusCode.Created, $"{(int)accepted.StatusCode}: {acceptedBody[..Math.Min(acceptedBody.Length, 240)]}");

        var over = ForumAgent.Create(Unique("cap-over"), "cap-over-" + Guid.NewGuid().ToString("N")[..8]);
        var (overDpop, overToken) = await over.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        using var refused = await RaiseAsync(client, overDpop, overToken, postId, "spam", new string('a', 4_097), ct);
        var body = await refused.Content.ReadAsStringAsync(ct);

        Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)refused.StatusCode}: {body[..Math.Min(body.Length, 240)]}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("curia/flag/rationale-too-long", problem.RootElement.GetProperty("type").GetString());
        Assert.Contains("bytes=4097", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("aaaa", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.68 counts UTF-8 bytes, not characters: 1,365 three-byte characters and one ASCII byte are
    /// 4,096 bytes and accepted; with a second ASCII byte, 4,097, refused. A cap counted in UTF-16
    /// code units would accept both.
    /// </summary>
    [Fact]
    public async Task R10_68_TheCapCountsUtf8BytesNotCharacters()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);
        var wide = new string((char)0x4E2D, 1_365);

        var atCap = ForumAgent.Create(Unique("cap-wide-at"), "cap-wide-at-" + Guid.NewGuid().ToString("N")[..8]);
        var (atDpop, atToken) = await atCap.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        using var accepted = await RaiseAsync(client, atDpop, atToken, postId, "spam", wide + "a", ct);
        var acceptedBody = await accepted.Content.ReadAsStringAsync(ct);
        Assert.True(accepted.StatusCode == HttpStatusCode.Created, $"{(int)accepted.StatusCode}: {acceptedBody[..Math.Min(acceptedBody.Length, 240)]}");

        var over = ForumAgent.Create(Unique("cap-wide-over"), "cap-wide-over-" + Guid.NewGuid().ToString("N")[..8]);
        var (overDpop, overToken) = await over.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        using var refused = await RaiseAsync(client, overDpop, overToken, postId, "spam", wide + "ab", ct);
        var body = await refused.Content.ReadAsStringAsync(ct);

        Assert.True(refused.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)refused.StatusCode}: {body[..Math.Min(body.Length, 240)]}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("curia/flag/rationale-too-long", problem.RootElement.GetProperty("type").GetString());
        Assert.Contains("bytes=4097", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.68's order: the cap is checked before screening and before the post is looked up. The
    /// rationale carries a credential, which screening would refuse as <c>rationale-rejected</c>, and
    /// the post does not exist, which the lookup would refuse as <c>no-such-post</c>; the answer is
    /// neither, because an overlong rationale is never screened and costs no read of the log.
    /// </summary>
    [Fact]
    public async Task R10_68_AnOverlongRationaleIsRefusedBeforeItIsScreenedOrAPostIsRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        var reporter = ForumAgent.Create(Unique("cap-order"), "cap-order-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var rationale = ("ghp_" + "A7bQ2xLm9RtVzP4kW8sYcE1nJ6dH0uF3gI5o").PadRight(5_000, ' ');
        Assert.Equal(5_000, Encoding.UTF8.GetByteCount(rationale));

        using var response = await RaiseAsync(client, dpop, token, "01JNOSUCHPOST00000000000001", "credential_leak", rationale, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.UnprocessableEntity, $"{(int)response.StatusCode}: {body[..Math.Min(body.Length, 240)]}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("curia/flag/rationale-too-long", problem.RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain("ghp_", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.36: withheld content stops being served. The remedy is <b>withholding plus a moderation
    /// event</b>, never deletion — the post stays in the log exactly as signed, and the read path
    /// declines to serve it.
    ///
    /// <para>The withholding is R10.59's record, written through the operator's own use case by the
    /// fixture.</para>
    /// </summary>
    [Fact]
    public async Task R10_36_AWithheldPostStopsBeingServedAndIsNotDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        using (var before = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct))
            Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        await forum.WithholdAsync(postId, ct);

        using var after = await client.GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);

        // The board listing must agree. A post withheld on one read path and served on another is
        // the withholding not having happened.
        using var listing = await client.GetAsync(new Uri($"/v1/boards/{board}/posts", UriKind.Relative), ct);
        Assert.DoesNotContain(postId, await listing.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    // ---- R7.22 and R10.70 (errata G18, D34) ----------------------------------------------------

    private static string Excerpt(string body) => body[..Math.Min(body.Length, 240)];

    private async Task<int> CommittedFlagLeavesAsync(CancellationToken ct)
    {
        var log = (await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(ct))
            .Match(r => r, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
        return log.Count(e => e.Event.Type.Value == FlagProjector.FlagCommittedType);
    }

    private async Task<int> FlagRowsByAsync(string raisedBy, CancellationToken ct)
    {
        var rows = (await forum.Services.GetRequiredService<IFlagDetailStore>().ReadAllAsync(ct))
            .Match(r => r, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
        return rows.Count(r => string.Equals(r.RaisedBy, raisedBy, StringComparison.Ordinal));
    }

    /// <summary>
    /// R7.22: a flag is never refused because the posting budget is spent. A T0 agent that has made
    /// its three posts of the day may still report (D34, finding 2: it was refused 403
    /// <c>table-11/rate-budget-exhausted</c>).
    /// </summary>
    [Fact]
    public async Task R7_22_AnAgentAtItsPostingBudgetMayFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, _, _, postId) = await PostedQuestionAsync(client, board, ct);

        var reporter = ForumAgent.Create(Unique("busy-reporter"), "busy-reporter-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        var own = "board-" + Guid.NewGuid().ToString("N")[..8];

        for (var i = 0; i < 3; i++)
        {
            using var posted = await dpop.PostAsync(
                client, PostsUrl, token, reporter.SignQuestion(own, $"Question {i}?", $"Q{i}", forum.Now), forum.Now, ct);
            Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        }

        using var response = await RaiseAsync(client, dpop, token, postId, "spam", "this is spam", ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.Created, $"{(int)response.StatusCode}: {Excerpt(body)}");
    }

    /// <summary>
    /// R7.22: ten flags a day at T0. The eleventh is refused with the flag budget's own reason, and
    /// the budget returns once the trailing 24 hours have passed.
    /// </summary>
    [Fact]
    public async Task R7_22_TheEleventhFlagInADayIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        var posts = new List<string>();
        for (var i = 0; i < 11; i++)
        {
            var (_, _, _, seeded) = await PostedQuestionAsync(client, "board-" + Guid.NewGuid().ToString("N")[..8], ct);
            posts.Add(seeded);
        }

        var reporter = ForumAgent.Create(Unique("eleventh"), "eleventh-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        for (var i = 0; i < 10; i++)
        {
            using var accepted = await RaiseAsync(client, dpop, token, posts[i], "spam", $"spam number {i}", ct);
            var acceptedBody = await accepted.Content.ReadAsStringAsync(ct);
            Assert.True(accepted.StatusCode == HttpStatusCode.Created, $"flag {i}: {(int)accepted.StatusCode}: {Excerpt(acceptedBody)}");
        }

        using (var refused = await RaiseAsync(client, dpop, token, posts[10], "spam", "spam number 10", ct))
        {
            var body = await refused.Content.ReadAsStringAsync(ct);
            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{(int)refused.StatusCode}: {Excerpt(body)}");
            using var problem = JsonDocument.Parse(body);
            Assert.StartsWith(
                "table-11/flag-budget-exhausted",
                problem.RootElement.GetProperty("detail").GetString(),
                StringComparison.Ordinal);
        }

        forum.Clock.Advance(TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1));

        // The token expired long ago (R5 caps it at 300 seconds), so a fresh one is obtained.
        var (freshDpop, freshToken) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        using var again = await RaiseAsync(client, freshDpop, freshToken, posts[10], "spam", "spam number 10", ct);
        var againBody = await again.Content.ReadAsStringAsync(ct);

        Assert.True(again.StatusCode == HttpStatusCode.Created, $"{(int)again.StatusCode}: {Excerpt(againBody)}");
    }

    /// <summary>
    /// R10.70: a second flag of one type by one raiser against one post is refused before anything
    /// is written, naming the type and the earlier flag's instant and never its rationale (D34,
    /// finding 1: five <c>spam</c> flags from one identity against one post were all accepted).
    /// </summary>
    [Fact]
    public async Task R10_70_ASecondFlagOfOneTypeByOneRaiserOnOnePostIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (_, _, _, postId) = await PostedQuestionAsync(client, "board-" + Guid.NewGuid().ToString("N")[..8], ct);

        var reporter = ForumAgent.Create(Unique("repeat"), "repeat-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var leavesBefore = await CommittedFlagLeavesAsync(ct);

        string raisedAt;
        using (var first = await RaiseAsync(client, dpop, token, postId, "spam", "first-rationale-of-the-pair", ct))
        {
            var body = await first.Content.ReadAsStringAsync(ct);
            Assert.True(first.StatusCode == HttpStatusCode.Created, $"{(int)first.StatusCode}: {Excerpt(body)}");
            using var created = JsonDocument.Parse(body);
            raisedAt = created.RootElement.GetProperty("raised_at").GetString()!;
        }

        using var second = await RaiseAsync(client, dpop, token, postId, "spam", "second-rationale-of-the-pair", ct);
        var secondBody = await second.Content.ReadAsStringAsync(ct);

        Assert.True(second.StatusCode == HttpStatusCode.Conflict, $"{(int)second.StatusCode}: {Excerpt(secondBody)}");
        using var problem = JsonDocument.Parse(secondBody);
        Assert.Equal("curia/flag/already-raised", problem.RootElement.GetProperty("type").GetString());

        var detail = problem.RootElement.GetProperty("detail").GetString()!;
        Assert.Contains("kind=spam", detail, StringComparison.Ordinal);
        Assert.Contains("raised_at=" + raisedAt, detail, StringComparison.Ordinal);
        Assert.DoesNotContain("rationale-of-the-pair", secondBody, StringComparison.Ordinal);

        Assert.Equal(leavesBefore + 1, await CommittedFlagLeavesAsync(ct));
        Assert.Equal(1, await FlagRowsByAsync(reporter.AgentId, ct));
    }

    /// <summary>
    /// R10.70 keys the repeat on post, raiser and type. Another type by the same raiser, or the same
    /// type by another raiser, is not a repeat: a post can leak a credential and carry an injection
    /// at once, and R10.39 counts each category.
    /// </summary>
    [Fact]
    public async Task R10_70_AnotherTypeOrAnotherRaiserIsNotARepeat()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (_, _, _, postId) = await PostedQuestionAsync(client, "board-" + Guid.NewGuid().ToString("N")[..8], ct);

        var a = ForumAgent.Create(Unique("raiser-a"), "raiser-a-" + Guid.NewGuid().ToString("N")[..8]);
        var (aDpop, aToken) = await a.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        var b = ForumAgent.Create(Unique("raiser-b"), "raiser-b-" + Guid.NewGuid().ToString("N")[..8]);
        var (bDpop, bToken) = await b.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var spamByA = await RaiseAsync(client, aDpop, aToken, postId, "spam", "spam, says A", ct);
        Assert.Equal(HttpStatusCode.Created, spamByA.StatusCode);

        using var incorrectByA = await RaiseAsync(client, aDpop, aToken, postId, "incorrect", "and wrong, says A", ct);
        var incorrectBody = await incorrectByA.Content.ReadAsStringAsync(ct);
        Assert.True(incorrectByA.StatusCode == HttpStatusCode.Created, $"{(int)incorrectByA.StatusCode}: {Excerpt(incorrectBody)}");

        using var spamByB = await RaiseAsync(client, bDpop, bToken, postId, "spam", "spam, says B", ct);
        var spamBody = await spamByB.Content.ReadAsStringAsync(ct);
        Assert.True(spamByB.StatusCode == HttpStatusCode.Created, $"{(int)spamByB.StatusCode}: {Excerpt(spamBody)}");
    }

    private async Task<IReadOnlyList<RaisedFlag>> JoinedFlagsByAsync(string raisedBy, CancellationToken ct)
    {
        var log = (await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(ct))
            .Match(r => r, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
        var rows = (await forum.Services.GetRequiredService<IFlagDetailStore>().ReadAllAsync(ct))
            .Match(r => r, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
        return [.. FlagDirectory.Join(log, rows).Flags.Where(f => string.Equals(f.RaisedBy, raisedBy, StringComparison.Ordinal))];
    }

    private async Task<(int Status, string? Type, string? Detail, string Body)> RaiseAndReadAsync(
        HttpClient client, DpopClient dpop, string token, string postId, string kind, string rationale,
        CancellationToken ct)
    {
        using var response = await RaiseAsync(client, dpop, token, postId, kind, rationale, ct);
        return await ReadAnswerAsync(response, ct);
    }

    /// <summary>The status, the problem type and the detail of one answer, read once.</summary>
    private static async Task<(int Status, string? Type, string? Detail, string Body)> ReadAnswerAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if ((int)response.StatusCode is >= 200 and < 300)
            return ((int)response.StatusCode, null, null, body);

        using var problem = JsonDocument.Parse(body);
        return (
            (int)response.StatusCode,
            problem.RootElement.TryGetProperty("type", out var type) ? type.GetString() : null,
            problem.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
            body);
    }

    /// <summary>
    /// R7.22 (errata G18, review of 4b3e91a): one raiser's flags are counted and recorded one at a
    /// time. Forty concurrent <c>spam</c> flags from one fresh T0 agent, one per post, against a
    /// budget of ten: before the raiser gate, every request counted the same zero and all forty were
    /// created. Now at most ten are, every other answer is the gate's 409 or the budget's 403 and
    /// none is a 5xx, and the private join holds exactly as many flags as were created. Then flags
    /// are sent one at a time until the budget refuses one, and the join holds exactly ten, so a gate
    /// that refused everything cannot pass this.
    /// </summary>
    [Fact]
    public async Task R7_22_ConcurrentFlagsByOneRaiserNeverExceedTheBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        var posts = new List<string>();
        for (var i = 0; i < 40; i++)
        {
            var (_, _, _, seeded) = await PostedQuestionAsync(client, "board-" + Guid.NewGuid().ToString("N")[..8], ct);
            posts.Add(seeded);
        }

        var reporter = ForumAgent.Create(Unique("burst"), "burst-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var answers = await Task.WhenAll(posts.Select(p => RaiseAndReadAsync(client, dpop, token, p, "spam", "concurrent spam", ct)));

        var created = answers.Count(a => a.Status == 201);
        var tally = string.Join(", ", answers.GroupBy(a => $"{a.Status} {a.Type}").Select(g => $"{g.Key} x{g.Count()}"));
        Assert.True(created is >= 1 and <= 10, $"created {created} of 40 against T0's 10: {tally}");

        foreach (var answer in answers.Where(a => a.Status != 201))
        {
            var allowed =
                (answer.Status == 409 && answer.Type == "curia/flag/raise-in-flight")
                || (answer.Status == 403 && answer.Detail?.StartsWith("table-11/flag-budget-exhausted", StringComparison.Ordinal) == true);
            Assert.True(allowed, $"{answer.Status}: {Excerpt(answer.Body)} ({tally})");
        }

        var joined = await JoinedFlagsByAsync(reporter.AgentId, ct);
        Assert.Equal(created, joined.Count);

        var flagged = joined.Select(f => f.PostId).ToHashSet(StringComparer.Ordinal);
        var refused = false;
        foreach (var post in posts.Where(p => !flagged.Contains(p)))
        {
            var answer = await RaiseAndReadAsync(client, dpop, token, post, "spam", "one at a time", ct);
            if (answer.Status == 201) continue;

            Assert.True(
                answer.Status == 403 && answer.Detail?.StartsWith("table-11/flag-budget-exhausted", StringComparison.Ordinal) == true,
                $"{answer.Status}: {Excerpt(answer.Body)}");
            refused = true;
            break;
        }

        Assert.True(refused, "the budget never refused a flag sent one at a time");
        Assert.Equal(10, (await JoinedFlagsByAsync(reporter.AgentId, ct)).Count);
    }

    /// <summary>
    /// R10.70 and R7.22 (errata G18, review of 4b3e91a): ten identical <c>spam</c> flags by one raiser
    /// against one post, sent concurrently, record one. Each of the other nine is refused, by the
    /// raiser gate while the first is in flight or by R10.70's repeat check after it, and the store and
    /// the log each gained exactly one entry.
    /// </summary>
    [Fact]
    public async Task R10_70_IdenticalFlagsRaisedAtOnceRecordOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (_, _, _, postId) = await PostedQuestionAsync(client, "board-" + Guid.NewGuid().ToString("N")[..8], ct);

        var reporter = ForumAgent.Create(Unique("twin"), "twin-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await reporter.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var leavesBefore = await CommittedFlagLeavesAsync(ct);

        var answers = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => RaiseAndReadAsync(client, dpop, token, postId, "spam", "identical spam", ct)));

        var tally = string.Join(", ", answers.GroupBy(a => $"{a.Status} {a.Type}").Select(g => $"{g.Key} x{g.Count()}"));
        Assert.True(answers.Count(a => a.Status == 201) == 1, $"created {answers.Count(a => a.Status == 201)} of 10 identical flags: {tally}");

        foreach (var answer in answers.Where(a => a.Status != 201))
        {
            Assert.True(
                answer.Status == 409 && answer.Type is "curia/flag/raise-in-flight" or "curia/flag/already-raised",
                $"{answer.Status}: {Excerpt(answer.Body)} ({tally})");
        }

        var rows = (await forum.Services.GetRequiredService<IFlagDetailStore>().ReadAllAsync(ct))
            .Match(r => r, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
        Assert.Single(rows, r =>
            string.Equals(r.PostId, postId, StringComparison.Ordinal)
            && string.Equals(r.RaisedBy, reporter.AgentId, StringComparison.Ordinal));
        Assert.Equal(leavesBefore + 1, await CommittedFlagLeavesAsync(ct));
    }
}
