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
            var (status, body) = await actor.Dpop.RequestTokenAsync(forum.Client, Probe.TokenEndpoint, forum.Now, actor.Agent.AgentId, Ct);
            reply = new Reply((int)status, null, body, null);
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

        Assert.True(problems.Count == 0, $"the {store} fault: " + string.Join("; ", problems));
    }
}
