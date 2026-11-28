using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// ULG19 (errata G52, R5.23): the token endpoint answers a fault of any store it touches <c>503</c> RFC 6749's <c>temporarily_unavailable</c> with the fixed
/// description and no <c>detail</c>, and mints nothing. Each store the endpoint touches (the replay cache, the Registrar's key store, the log) faults in turn by a
/// driver fault, and the log also by a reader that reports a failed <c>Result</c> in each of the two reads the endpoint makes (the binding read, which follows
/// the assertion's verification and both <c>jti</c>s, and the standings read), the legs Postgres never takes but a reporting reader does. On the in-process host with the decorators, not the role host (ruling 13).
/// The plan's control (a posture that will not fold stays <c>server_error</c>) is not built here: a log the posture fold refuses needs a credential history
/// Table 6 forbids, which neither ingest nor any fixture can write.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class StoreFaultTokenTests(FaultableForumFixture forum) : IClassFixture<FaultableForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("replay")]
    [InlineData("keys")]
    [InlineData("log-driver")]
    [InlineData("log-report-every-read")]
    [InlineData("log-report-whole-log")]
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A request the host threw out of is a reply that says what it threw.")]
    public async Task R5_23_ULG19_AFaultOfAnyStoreTheTokenTouchesIsTemporarilyUnavailable(string store)
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "token", Ct);
        switch (store)
        {
            case "replay": forum.Replay.Arm(); break;
            case "keys": forum.Keys.Arm(); break;
            case "log-driver": forum.Store.Arm(EventStoreFault.ThrowReads); break;
            case "log-report-every-read": forum.Store.Arm(EventStoreFault.ReportAll); break;
            default: forum.Store.Arm(EventStoreFault.ReportForward); break;
        }

        Reply reply;
        try
        {
            reply = await DpopClient.RequestTokenReplyAsync(
                forum.Client, actor.Agent.AgentId, actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now), Ct);
        }
        catch (Exception e) when (!Ct.IsCancellationRequested)
        {
            reply = new Reply(500, null, string.Empty, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            forum.Replay.Disarm();
            forum.Keys.Disarm();
            forum.Store.Disarm();
        }

        var problems = new List<string>();
        if (!FaultSweep.IsOAuthError(reply, PublishedStoreFaults.TokenStatus, PublishedStoreFaults.TokenCode))
            problems.Add($"expected {PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode}, actual {reply.Says}");
        if (!reply.Body.Contains(PublishedStoreFaults.TokenDescription, StringComparison.Ordinal))
            problems.Add($"the answer does not carry the published description \"{PublishedStoreFaults.TokenDescription}\"");
        if (reply.Body.Contains("access_token", StringComparison.Ordinal)) problems.Add("a token was minted");
        if (reply.HasHeader("Retry-After")) problems.Add("the answer carries a Retry-After header (R9.52: none is sent)");

        Assert.True(problems.Count == 0, $"the {store} fault: " + string.Join("; ", problems));
    }

    /// <summary>
    /// ULG18 (c) (errata G87, R5.28; register item 314). A token request makes one read set, so a reader that serves the first whole-log read and reports every one from the second (the old
    /// shape: a binding read, then a standings read) is never asked for a second, and the request is <c>200</c>. Before errata G87 the second read, the agent's standing, was where this fault
    /// fell, after both <c>jti</c> consumptions, and spent both; the one read set is now the read that does, and ULG18 (d) holds that. Falsified by a second read set in the endpoint (FRC10):
    /// the fault is then reached and this answers <c>503</c>.
    /// </summary>
    [Fact]
    public async Task R5_28_ULG18c_ATokenRequestMakesOneReadSetSoAFaultFromTheSecondReadIsNeverMet()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "token-standing", Ct);
        var assertion = actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now);
        var proof = actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now);

        Reply first;
        forum.Store.Arm(EventStoreFault.ReportWholeLogFromSecondRead);
        try
        {
            first = await DpopClient.RequestTokenReplyAsync(forum.Client, actor.Agent.AgentId, assertion, proof, Ct);
        }
        finally
        {
            forum.Store.Disarm();
        }

        Assert.True(first.Status == 200, $"expected 200 (one read set; the second read the fault waits for is never made), actual {first.Says}");
    }

    private async Task<Reply> TokenAsync(SceneActor actor, string assertion, string proof) =>
        await DpopClient.RequestTokenReplyAsync(forum.Client, actor.Agent.AgentId, assertion, proof, Ct);

    private static void AssertFaultReached(Reply first) =>
        Assert.True(
            FaultSweep.IsOAuthError(first, PublishedStoreFaults.TokenStatus, PublishedStoreFaults.TokenCode),
            $"NOT REACHED: the first call was {first.Says}, expected {PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode} (the fault did not fire, so nothing below it says anything)");

    /// <summary>
    /// ULG18 (d), (i) (register item 282, errata G72, R5.25, R5.23 (rev.)). A fault at the read that binds the key follows the consumption of the assertion's <c>jti</c> (the token endpoint
    /// verifies under the key the store holds and consumes first, and reads the log after), so the fault spends the assertion. The first call is <c>503</c> <c>temporarily_unavailable</c>
    /// (asserted first: a fault that did not fire proves nothing); the same assertion sent with a <b>proof from another key</b> is then <c>401</c> <c>invalid_client</c>, not <c>200</c>.
    /// Before the fix this was the finding F-L1 (N-F-M3): a party that held the assertion alone obtained a token for the victim's identity bound to its own key, and a fault of the read
    /// lengthened the window by its own duration; this test pinned that <c>200</c> as today's behaviour until the register item that reverses it landed, and now says the reverse.
    /// The returned form of an unreadable log (the in-process host's <c>Result</c>); the thrown form is <c>StoreFaultRoleTests</c>' ULG18.
    /// </summary>
    [Fact]
    public async Task R5_23_ULG18d_ARequestWhoseBindingReadFaultedIsSpentAndAProofFromAnotherKeyIsRefused()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "token-captor", Ct);
        var captor = ForumAgent.Create(actor.Agent.AgentId, "captor-" + Guid.NewGuid().ToString("N")[..8]);
        var captorDpop = DpopClient.For(captor, captor.AssertionKey);
        var assertion = actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now);

        Reply first;
        forum.Store.Arm(EventStoreFault.ReportAll);
        try
        {
            first = await TokenAsync(actor, assertion, actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now));
        }
        finally
        {
            forum.Store.Disarm();
        }

        AssertFaultReached(first);
        var withTheCaptorsProof = await TokenAsync(actor, assertion, captorDpop.Proof("POST", Probe.TokenEndpoint, forum.Now));

        Assert.True(
            withTheCaptorsProof.Status == 401 && withTheCaptorsProof.Error == "invalid_client",
            $"the same assertion with a proof from another key after the fault: expected 401 invalid_client (the fault spent the assertion), actual {withTheCaptorsProof.Says}");
        Assert.DoesNotContain("access_token", withTheCaptorsProof.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ULG18 (d), (ii) (register item 282, errata G72, R5.25). Its own scene, its own victim, assertion and fault, because the leg is order-contaminated if it follows (i): (i)'s <c>200</c> before the
    /// fix consumes the assertion, so a later leg would pass for the replay it is meant to catch. The first call is <c>503</c> (asserted first); the same assertion sent with a <b>new proof of
    /// the victim's own key</b> is then <c>401</c> <c>invalid_client</c>: the fault spent the assertion for its holder as well as for a captor, so no fault of the log leaves a verified assertion reusable.
    /// </summary>
    [Fact]
    public async Task R5_23_ULG18e_TheVictimsOwnNewProofOnAnAssertionWhoseBindingReadFaultedIsRefused()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "token-victim-proof", Ct);
        var assertion = actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now);

        Reply first;
        forum.Store.Arm(EventStoreFault.ReportAll);
        try
        {
            first = await TokenAsync(actor, assertion, actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now));
        }
        finally
        {
            forum.Store.Disarm();
        }

        AssertFaultReached(first);
        var again = await TokenAsync(actor, assertion, actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now));

        Assert.True(
            again.Status == 401 && again.Error == "invalid_client",
            $"the same assertion with a new proof of the victim's own key after the fault: expected 401 invalid_client (the fault spent the assertion), actual {again.Says}");
        Assert.DoesNotContain("access_token", again.Body, StringComparison.Ordinal);
    }

    /// <summary>
    /// ULG18 (d), the controls (register item 282), green before and after the fix, so that (i) and (ii) are not an observer that always answers one thing. (S2) With no fault an assertion is used once
    /// (<c>200</c>) and presented again with a captor's proof (<c>401</c> <c>invalid_client</c>): a spent assertion is refused by the endpoint on both trees. (iii) After the fault, a <b>fresh</b>
    /// assertion of the victim with the captor's proof is <c>200</c>: the fault spends the one assertion it met and not the victim's ability to ask again, and the captor's proof was never consumed.
    /// </summary>
    [Fact]
    public async Task R5_23_ULG18f_ControlsASpentAssertionIsRefusedWithoutAFaultAndAFreshOneIsServedAfterOne()
    {
        var spent = await AcceptedAnswerScene.EnrolAsync(forum, "token-s2", Ct);
        var spentCaptor = ForumAgent.Create(spent.Agent.AgentId, "captor-" + Guid.NewGuid().ToString("N")[..8]);
        var spentCaptorDpop = DpopClient.For(spentCaptor, spentCaptor.AssertionKey);
        var used = spent.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now);
        var s2First = await TokenAsync(spent, used, spent.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now));
        var s2Captor = await TokenAsync(spent, used, spentCaptorDpop.Proof("POST", Probe.TokenEndpoint, forum.Now));

        var victim = await AcceptedAnswerScene.EnrolAsync(forum, "token-iii", Ct);
        var captor = ForumAgent.Create(victim.Agent.AgentId, "captor-" + Guid.NewGuid().ToString("N")[..8]);
        var captorDpop = DpopClient.For(captor, captor.AssertionKey);
        Reply faulted;
        forum.Store.Arm(EventStoreFault.ReportAll);
        try
        {
            faulted = await TokenAsync(victim, victim.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), victim.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now));
        }
        finally
        {
            forum.Store.Disarm();
        }

        AssertFaultReached(faulted);
        var fresh = await TokenAsync(victim, victim.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), captorDpop.Proof("POST", Probe.TokenEndpoint, forum.Now));

        var problems = new List<string>();
        if (s2First.Status != 200) problems.Add($"(S2) the first use with no fault: expected 200, actual {s2First.Says}");
        if (s2Captor.Status != 401 || s2Captor.Error != "invalid_client")
            problems.Add($"(S2) the spent assertion with a captor's proof, no fault: expected 401 invalid_client, actual {s2Captor.Says}");
        if (fresh.Status != 200) problems.Add($"(iii) a fresh assertion with the captor's proof after the fault: expected 200, actual {fresh.Says}");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// VBR4 (register item 282, errata G72, R5.23 (rev.)), a control green before and after the fix. A fault of the Registrar's key store's own lookup precedes the consumption of both <c>jti</c>s
    /// and spends neither: the first call is <c>503</c> (asserted first), and with the fault gone the same assertion and proof are <c>200</c>.
    /// </summary>
    [Fact]
    public async Task R5_23_VBR4_AFaultOfTheKeyStoreSpendsNeitherJti()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "token-keystore", Ct);
        var assertion = actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now);
        var proof = actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now);

        Reply first;
        forum.Keys.Arm();
        try
        {
            first = await TokenAsync(actor, assertion, proof);
        }
        finally
        {
            forum.Keys.Disarm();
        }

        AssertFaultReached(first);
        var same = await TokenAsync(actor, assertion, proof);

        Assert.True(same.Status == 200, $"the same assertion and proof with the key store restored: expected 200 (neither jti was spent), actual {same.Says}");
    }

    /// <summary>
    /// ULG43 (R5.23's second sentence). An exception nothing names, thrown from a store the token endpoint reads, is the Forum's own fault: <c>500</c>
    /// <c>server_error</c> with the published description, no <c>detail</c>, none of the exception's text and no <c>Retry-After</c>; it is not
    /// <c>temporarily_unavailable</c>. (The posture that will not fold is the other own fault and cannot be built here, above.)
    /// </summary>
    [Fact]
    public async Task R5_23_ULG43_AnExceptionNothingNamesAtTheTokenEndpointIsServerError()
    {
        const string Sentinel = "an-unclassified-bug-text";
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "token-own", Ct);
        forum.Store.Arm(EventStoreFault.ThrowReads, () => StoreFaultKinds.Unclassified(Sentinel));
        Reply reply;
        try
        {
            reply = await DpopClient.RequestTokenReplyAsync(
                forum.Client, actor.Agent.AgentId, actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now), Ct);
        }
        finally
        {
            forum.Store.Disarm();
        }

        var problems = new List<string>();
        if (!FaultSweep.IsOAuthError(reply, 500, PublishedStoreFaults.TokenOwnFaultCode))
            problems.Add($"expected 500 {PublishedStoreFaults.TokenOwnFaultCode}, actual {reply.Says}");
        if (!reply.Body.Contains(PublishedStoreFaults.TokenOwnFaultDescription, StringComparison.Ordinal))
            problems.Add($"the answer does not carry the published description \"{PublishedStoreFaults.TokenOwnFaultDescription}\"");
        if (reply.Body.Contains(Sentinel, StringComparison.Ordinal)) problems.Add("the body carries the exception's text");
        if (reply.HasHeader("Retry-After")) problems.Add("the answer carries a Retry-After header");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
