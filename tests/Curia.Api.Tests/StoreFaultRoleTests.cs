using System.Diagnostics.CodeAnalysis;
using System.Net;
using Curia.Api.Tests.Fuzz;
using Curia.Domain.Moderation;
using Curia.Tests.Shared;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// ULG15 to ULG18, ULG40 (errata G52, R9.52, R5.23, ruling 13): the real store. The Forum is hosted in the Production environment and connects as the
/// application role, so a grant revoked from the role reaches the host and an unhandled exception is the body-less <c>500</c> the deployed Forum answers. Every
/// fault is made on the fixture's own database and restored in a <c>finally</c>, and every fact ends by asserting the restoration from the catalogue. If the
/// application role lacks a privilege the host needs, ULG40 says so: it is reported and never granted around (open risk 11).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("role-forum")]
public sealed class StoreFaultRoleTests(RoleForumFixture forum) : IClassFixture<RoleForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// ULG40, a control. The role-connected host is healthy: enrolment, a question, a read, the budget readout and a flag answer <c>2xx</c>; the database reports
    /// connections of the application role (so the host is not the administrator's); the role holds its grants before the fact.
    /// </summary>
    [Fact]
    public async Task R11_6_ULG40_TheHostThatConnectsAsTheApplicationRoleIsHealthy()
    {
        await forum.AssertRestoredAsync(Ct);
        var board = Probe.Board();
        var poster = await AcceptedAnswerScene.EnrolAsync(forum, "poster", Ct);
        var flagger = await AcceptedAnswerScene.EnrolAsync(forum, "flagger", Ct);

        var asked = await BudgetReadouts.AskAsync(forum, poster, board);
        var problems = new List<string>();
        if (asked.Status != 201) problems.Add($"a question: expected 201, actual {asked.Says}");

        var postId = System.Text.Json.JsonDocument.Parse(asked.Raw).RootElement.TryGetProperty("post_id", out var id) ? id.GetString() : null;
        using var read = new HttpRequestMessage(HttpMethod.Get, $"/v1/posts/{postId}");
        var readReply = await Reply.SendAsync(forum.Client, read, Ct);
        if (readReply.Status != 200) problems.Add($"a read of the post: expected 200, actual {readReply.Says}");

        var budget = await FaultActs.GetAsync(forum, poster, "/v1/budget", Ct);
        if (budget.Status != 200) problems.Add($"the budget readout: expected 200, actual {budget.Says}");

        var flag = await BudgetReadouts.FlagAsync(forum, flagger, postId ?? "missing", FlagKind.Spam);
        if (flag.Status != 201) problems.Add($"a flag: expected 201, actual {flag.Says}");

        var users = await forum.ConnectedUsersAsync(Ct);
        if (!users.Contains(forum.RoleName, StringComparer.Ordinal)) problems.Add($"no connection of the role {forum.RoleName} is open: the host connects as {string.Join(", ", users)}");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
        await forum.AssertRestoredAsync(Ct);
    }

    private static string Expected(SweptReply s, Func<SweptReply, bool> healthy) =>
        healthy(s)
            ? "2xx"
            : FaultSweep.IsToken(s)
                ? $"{PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode}"
                : $"{PublishedStoreFaults.UnavailableStatus} {PublishedStoreFaults.LogUnreadableType} (a problem document with a body)";

    private static bool Meets(SweptReply s, Func<SweptReply, bool> healthy) =>
        healthy(s)
            ? s.Reply.Threw is null && s.Reply.Status is >= 200 and <= 299
            : FaultSweep.IsToken(s)
                ? FaultSweep.IsOAuthError(s.Reply, PublishedStoreFaults.TokenStatus, PublishedStoreFaults.TokenCode)
                : FaultSweep.IsProblem(s.Reply, PublishedStoreFaults.UnavailableStatus, PublishedStoreFaults.LogUnreadableType);

    private static void Judged(IReadOnlyList<SweptReply> swept, Func<SweptReply, bool> healthy)
    {
        var problems = swept.Where(s => !Meets(s, healthy)).Select(s => $"expected {Expected(s, healthy)}, actual {s.Says}").ToList();
        Assert.True(swept.Count > 0, "the sweep sent nothing");
        Assert.True(problems.Count == 0, $"{problems.Count} of {swept.Count} exemplars:\n" + FaultSweep.Lines(problems));
    }

    /// <summary>
    /// ULG15. <c>SELECT</c> revoked from the application role on <c>events</c>, tokens minted before: every route that reads the log answers <c>503</c>
    /// <c>curia/log/unreadable</c> with a problem document, not the body-less <c>500</c> the deployed Forum answers today. The grant is restored in a
    /// <c>finally</c> and asserted restored.
    /// </summary>
    [Fact]
    public async Task R9_52_ULG15_ARevokedSelectOnTheLogIsAnUnreadableLogOnEveryRoute()
    {
        await forum.AssertRestoredAsync(Ct);
        try
        {
            using var context = await FuzzContext.SeedAsync(forum, Ct);
            var swept = await FaultSweep.RunAsync(
                forum, context, _ => true, () => forum.RevokeSelectOnEventsAsync(Ct), () => forum.GrantSelectOnEventsAsync(Ct), Ct);

            Judged(swept, FaultSweep.ReadsNoStore);
        }
        finally
        {
            await forum.GrantSelectOnEventsAsync(Ct);
        }

        await forum.AssertRestoredAsync(Ct);
    }

    /// <summary>
    /// ULG16. The whole-log-only fault (a row-level-security policy raising on a poison row only a scan reaches): the readout, the flag listing, a single post,
    /// the thread, a board listing and search are <c>503</c>; enrolment, which reads the agent's own stream only, is <c>201</c> and is the control that the host is
    /// healthy and the fault whole-log.
    /// </summary>
    [Fact]
    public async Task R9_52_ULG16_AFaultOnlyAWholeLogScanReachesIsAnUnreadableLogOnEveryRouteThatScans()
    {
        await forum.AssertRestoredAsync(Ct);
        try
        {
            using var context = await FuzzContext.SeedAsync(forum, Ct);
            var swept = await FaultSweep.RunAsync(
                forum, context, _ => true, () => forum.CreatePoisonPolicyAsync(Ct), () => forum.DropPoisonPolicyAsync(Ct), Ct);

            Judged(swept, s => FaultSweep.ReadsNoStore(s) || FaultSweep.IsEnrolment(s));
        }
        finally
        {
            await forum.DropPoisonPolicyAsync(Ct);
        }

        await forum.AssertRestoredAsync(Ct);
    }

    private async Task<Reply> TokenAsync(SceneActor actor, string assertion, string proof)
    {
        return await DpopClient.RequestTokenReplyAsync(forum.Client, actor.Agent.AgentId, assertion, proof, Ct);
    }

    private static bool Unavailable(Reply reply) => FaultSweep.IsOAuthError(reply, PublishedStoreFaults.TokenStatus, PublishedStoreFaults.TokenCode);

    /// <summary>ULG17. <c>POST /oauth/token</c> with the log unreadable: <c>503</c> <c>temporarily_unavailable</c> with the published description, no <c>detail</c>, no token.</summary>
    [Fact]
    public async Task R5_23_ULG17_TheTokenEndpointWithTheLogUnreadableIsTemporarilyUnavailable()
    {
        await forum.AssertRestoredAsync(Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "token", Ct);
        Reply reply;
        try
        {
            await forum.RevokeSelectOnEventsAsync(Ct);
            reply = await TokenAsync(actor, actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now));
        }
        finally
        {
            await forum.GrantSelectOnEventsAsync(Ct);
        }

        var problems = new List<string>();
        if (!Unavailable(reply)) problems.Add($"expected {PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode}, actual {reply.Says}");
        else if (!reply.Body.Contains(PublishedStoreFaults.TokenDescription, StringComparison.Ordinal)) problems.Add($"the description is not the published one: {reply.Says}");
        if (reply.Body.Contains("access_token", StringComparison.Ordinal)) problems.Add("a token was minted");
        if (reply.HasHeader("Retry-After")) problems.Add("the answer carries a Retry-After header (R9.52: none is sent)");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
        await forum.AssertRestoredAsync(Ct);
    }

    /// <summary>
    /// ULG18. The token's <c>jti</c> is spent by a whole-log fault and not by a key-read fault (Task 0's M3). (a) A whole-log fault: <c>503</c>, then the same
    /// assertion sent again is <c>401</c> <c>invalid_client</c> (a replay), then a new assertion is <c>200</c>. (b) <c>SELECT</c> revoked, a fault at the key read
    /// before anything is consumed: <c>503</c>, and with the grant restored the same assertion is <c>200</c>.
    /// </summary>
    [Fact]
    public async Task R5_23_ULG18_TheJtiIsSpentByAWholeLogFaultAndNotByAKeyReadFault()
    {
        await forum.AssertRestoredAsync(Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "jti", Ct);
        var problems = new List<string>();

        // (a) the whole-log fault.
        var assertionA = actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now);
        var proofA = actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now);
        Reply firstA;
        Reply replayA;
        try
        {
            await forum.CreatePoisonPolicyAsync(Ct);
            firstA = await TokenAsync(actor, assertionA, proofA);
            replayA = await TokenAsync(actor, assertionA, proofA);
        }
        finally
        {
            await forum.DropPoisonPolicyAsync(Ct);
        }

        if (!Unavailable(firstA)) problems.Add($"(a) the first call: expected {PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode}, actual {firstA.Says}");
        if (replayA.Status != (int)HttpStatusCode.Unauthorized || replayA.Error != "invalid_client")
            problems.Add($"(a) the same assertion again: expected 401 invalid_client (a replay), actual {replayA.Says}");
        var freshA = await TokenAsync(actor, actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now));
        if (freshA.Status != 200) problems.Add($"(a) a new assertion: expected 200, actual {freshA.Says}");

        // (b) the key-read fault.
        var assertionB = actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now);
        var proofB = actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now);
        Reply firstB;
        try
        {
            await forum.RevokeSelectOnEventsAsync(Ct);
            firstB = await TokenAsync(actor, assertionB, proofB);
        }
        finally
        {
            await forum.GrantSelectOnEventsAsync(Ct);
        }

        if (!Unavailable(firstB)) problems.Add($"(b) the first call: expected {PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode}, actual {firstB.Says}");
        var restoredB = await TokenAsync(actor, assertionB, proofB);
        if (restoredB.Status != 200) problems.Add($"(b) the same assertion with the grant restored: expected 200, actual {restoredB.Says}");

        Assert.True(problems.Count == 0, string.Join('\n', problems));
        await forum.AssertRestoredAsync(Ct);
    }
}
