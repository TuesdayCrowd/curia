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

    /// <summary>
    /// SEQ6 (errata G69, R11.6 (add.)): the catalogue check reads the grant strictly. At rest on a migrated database it is empty; after <c>REVOKE INSERT</c> it names the columns
    /// the Forum supplies; after a table-level <c>GRANT INSERT</c> (the shape <c>db/0001</c> had and a harness that heals at table level re-creates) it names <c>seq</c>, so a
    /// predicate that reads a table-level grant as fine is red here. The grant is put back as the fact found it.
    /// </summary>
    [Fact]
    public async Task R11_6_SEQ6_TheCatalogueCheckIsStrict()
    {
        var found = await forum.HoldsTableLevelInsertOnEventsAsync(Ct);
        var problems = new List<string>();
        try
        {
            await forum.RestoreInsertOnEventsAsync(tableLevel: false, Ct);
            var rest = await forum.RestorationProblemsAsync(Ct);
            if (rest.Length != 0) problems.Add($"at rest on the migrated grant: expected \"\", actual \"{rest}\"");

            await forum.RevokeInsertOnEventsAsync(Ct);
            var revoked = await forum.RestorationProblemsAsync(Ct);
            if (!revoked.Contains("INSERT on the columns the Forum supplies is not granted", StringComparison.Ordinal))
                problems.Add($"after REVOKE INSERT: expected 'INSERT on the columns the Forum supplies is not granted', actual \"{revoked}\"");

            await forum.GrantInsertOnEventsTableLevelAsync(Ct);
            var wide = await forum.RestorationProblemsAsync(Ct);
            if (!wide.Contains("INSERT on events.seq is granted", StringComparison.Ordinal))
                problems.Add($"after a table-level GRANT INSERT: expected 'INSERT on events.seq is granted', actual \"{wide}\"");
        }
        finally
        {
            await forum.RestoreInsertOnEventsAsync(found, CancellationToken.None);
        }

        Assert.True(problems.Count == 0, $"Expected: the check strict in both directions{System.Environment.NewLine}Actual:   {string.Join(" | ", problems)}");
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
    /// the thread, a board listing, search and <c>POST /v1/agents</c> are <c>503</c> (enrolment decides a kid's binding from the honoured rows of the whole log, R5.24, errata G68); the routes that read no store
    /// answer as they do healthy, which is the control that the host is up and the fault whole-log.
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

            Judged(swept, FaultSweep.ReadsNoStore);
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
    /// ULG18 (register item 282, errata G72, R5.25, R5.23 (rev.)). A fault at a read of the log after the assertion verifies follows the consumption of the client assertion's and the proof's <c>jti</c>
    /// and spends both: the token endpoint verifies under the key the store holds, consumes, and only then reads the log to bind the key. The thrown form of an unreadable log (a real reader over
    /// real grants; the returned form is <c>StoreFaultTokenTests</c>' ULG18 (d)). For (a) a whole-log fault (the poison policy) and (b) <c>SELECT</c> revoked, in one scene each: the first call is <c>503</c>
    /// (asserted first: <c>NOT REACHED</c> if the fault did not fire); the same assertion and proof sent again <b>while the fault stands</b> is <c>401</c> <c>invalid_client</c> (a replay), not <c>503</c>;
    /// with the fault gone the same assertion is <c>401</c>, not <c>200</c>. In a scene of its own (so that the leg before it, a <c>200</c> before the fix, cannot spend the proof for it) a
    /// <b>fresh assertion with the proof the faulted call used</b> is <c>400</c> <c>invalid_dpop_proof</c>: the fault spent the proof as well. A fresh assertion and a fresh proof is <c>200</c> (the control).
    /// Before the fix this test asserted the reverse: the fault spent neither <c>jti</c> and the same assertion was <c>200</c> once the fault was gone.
    /// </summary>
    [Fact]
    public async Task R5_23_ULG18_AFaultAtTheLogReadAfterTheAssertionVerifiesSpendsBothJtis()
    {
        await forum.AssertRestoredAsync(Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "jti", Ct);
        var problems = new List<string>();

        foreach (var (label, inject, remove) in new (string, Func<Task>, Func<Task>)[]
        {
            ("(a) the whole-log fault", () => forum.CreatePoisonPolicyAsync(Ct), () => forum.DropPoisonPolicyAsync(Ct)),
            ("(b) a revoked SELECT", () => forum.RevokeSelectOnEventsAsync(Ct), () => forum.GrantSelectOnEventsAsync(Ct)),
        })
        {
            // Scene 1: the same assertion and proof, three times.
            var assertion = actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now);
            var proof = actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now);
            Reply first;
            Reply whileItStands;
            try
            {
                await inject();
                first = await TokenAsync(actor, assertion, proof);
                whileItStands = await TokenAsync(actor, assertion, proof);
            }
            finally
            {
                await remove();
            }

            Assert.True(Unavailable(first), $"NOT REACHED: {label}: the first call was {first.Says}, expected {PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode}");
            if (whileItStands.Status != 401 || whileItStands.Error != "invalid_client")
                problems.Add($"{label}: the same assertion and proof again while the fault stands: expected 401 invalid_client (a replay), actual {whileItStands.Says}");
            var gone = await TokenAsync(actor, assertion, proof);
            if (gone.Status != 401 || gone.Error != "invalid_client")
                problems.Add($"{label}: the same assertion once the fault is gone: expected 401 invalid_client (its jti was spent), actual {gone.Says}");

            // Scene 2, its own: the fault, then a fresh assertion with the proof the faulted call used.
            var proof2 = actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now);
            Reply first2;
            try
            {
                await inject();
                first2 = await TokenAsync(actor, actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), proof2);
            }
            finally
            {
                await remove();
            }

            Assert.True(Unavailable(first2), $"NOT REACHED: {label}, the proof scene: the first call was {first2.Says}, expected {PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode}");
            var sameProof = await TokenAsync(actor, actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), proof2);
            if (sameProof.Status != 400 || sameProof.Error != "invalid_dpop_proof")
                problems.Add($"{label}: a fresh assertion with the proof the faulted call used: expected 400 invalid_dpop_proof (the proof's jti was spent), actual {sameProof.Says}");

            // The control: nothing spent, nothing faulted.
            var control = await TokenAsync(actor, actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now));
            if (control.Status != 200) problems.Add($"{label}: a fresh assertion and a fresh proof once the fault is gone: expected 200, actual {control.Says}");
        }

        Assert.True(problems.Count == 0, string.Join('\n', problems));
        await forum.AssertRestoredAsync(Ct);
    }
}
