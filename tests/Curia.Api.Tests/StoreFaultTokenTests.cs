using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// ULG19 (errata G52, R5.23): the token endpoint answers a fault of any store it touches <c>503</c> RFC 6749's <c>temporarily_unavailable</c> with the fixed
/// description and no <c>detail</c>, and mints nothing. Each store the endpoint touches (the replay cache, the Registrar's key store, the log) faults in turn by a
/// driver fault, and the log also by a reader that reports a failed <c>Result</c> in each of the two reads the endpoint makes (the assertion's key read, the
/// whole-log read), the legs Postgres never takes but a reporting reader does. On the in-process host with the decorators, not the role host (ruling 13).
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
    /// ULG18 (c) (errata G68, R5.23 (rev.), ruling 64). A fault at the second whole-log read of the request, the read of the agent's standing, follows the consumption of the
    /// assertion's and the proof's <c>jti</c> (the first whole-log read, the key's, precedes it) and so spends both. The first call is <c>503</c>
    /// <c>temporarily_unavailable</c> and mints nothing; the same assertion and proof sent again with the fault gone is not <c>200</c> (a replay, which the run names below);
    /// a fresh pair is <c>200</c>. Falsified by moving the standings read above the validator (Task 7): this goes red and (a) of the role host's ULG18 stays green.
    /// </summary>
    [Fact]
    public async Task R5_23_ULG18c_AFaultAtTheStandingsReadFollowsTheConsumptionAndSpendsTheJti()
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

        var again = await DpopClient.RequestTokenReplyAsync(forum.Client, actor.Agent.AgentId, assertion, proof, Ct);
        var fresh = await DpopClient.RequestTokenReplyAsync(
            forum.Client, actor.Agent.AgentId, actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now), actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now), Ct);

        var problems = new List<string>();
        if (!FaultSweep.IsOAuthError(first, PublishedStoreFaults.TokenStatus, PublishedStoreFaults.TokenCode))
            problems.Add($"the first call: expected {PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode}, actual {first.Says}");
        if (first.Body.Contains("access_token", StringComparison.Ordinal)) problems.Add("the first call minted a token");
        if (again.Status != 401 || again.Error != "invalid_client")
            problems.Add($"the same assertion and proof with the fault gone: expected 401 invalid_client (a replay: the assertion's jti was spent), actual {again.Says}");
        if (fresh.Status != 200) problems.Add($"a fresh assertion and proof: expected 200, actual {fresh.Says}");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// ULG18 (d) (Amendment 7, ruling 85): a pin of today's behaviour, so that the lead is a decision and not a drift (the pattern of vector 48). A fault at the first whole-log read, the read that resolves the
    /// assertion's key, precedes the consumption of the assertion's <c>jti</c> and spends nothing. The same assertion, sent with a <b>new proof from another key</b>, answers <c>200</c>: a token bound to that other
    /// key, for the identity the assertion names. That is finding F-L1: a party that captured the assertion alone obtains a token for the victim's identity bound to its own key, for as long as the assertion is unspent,
    /// and a read-1 fault lengthens that window by the fault's duration. Register entry N-F-M3 is the entry that reverses it (the token endpoint verifies the assertion against the store's key, consumes the
    /// <c>jti</c>, and only then reads the log); when it lands this flips to <c>invalid_client</c> and becomes the probe of the fix.
    /// </summary>
    [Fact]
    public async Task R5_23_ULG18d_AnUnspentAssertionAfterAReadOneFaultAnswers200ToAProofFromAnotherKey_Pin()
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "token-captor", Ct);
        var captor = ForumAgent.Create(actor.Agent.AgentId, "captor-" + Guid.NewGuid().ToString("N")[..8]);
        var captorDpop = DpopClient.For(captor, captor.AssertionKey);
        var assertion = actor.Dpop.ClientAssertion(Probe.TokenEndpoint, forum.Now);

        Reply first;
        forum.Store.Arm(EventStoreFault.ReportAll);
        try
        {
            first = await DpopClient.RequestTokenReplyAsync(forum.Client, actor.Agent.AgentId, assertion, actor.Dpop.Proof("POST", Probe.TokenEndpoint, forum.Now), Ct);
        }
        finally
        {
            forum.Store.Disarm();
        }

        var withTheCaptorsProof = await DpopClient.RequestTokenReplyAsync(forum.Client, actor.Agent.AgentId, assertion, captorDpop.Proof("POST", Probe.TokenEndpoint, forum.Now), Ct);

        var problems = new List<string>();
        if (!FaultSweep.IsOAuthError(first, PublishedStoreFaults.TokenStatus, PublishedStoreFaults.TokenCode))
            problems.Add($"the first call: expected {PublishedStoreFaults.TokenStatus} {PublishedStoreFaults.TokenCode}, actual {first.Says}");
        if (withTheCaptorsProof.Status != 200)
            problems.Add($"the same assertion with a proof from another key: expected 200 (today's behaviour, F-L1), actual {withTheCaptorsProof.Says}");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
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
