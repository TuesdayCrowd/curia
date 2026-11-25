using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Api.Tests.EnrollProofProbe;

namespace Curia.Api.Tests;

/// <summary>
/// EPP12 and EPP14 (R4.46's order, and what a refusal carries) over the real Forum with each store faultable. <b>EPP12</b>: a proof that does not verify never reaches the log (so a faulted log is not seen);
/// a verified proof whose log read faults answers <c>503 curia/log/unreadable</c> and its <c>jti</c> is spent; a faulted nonce store or replay cache answers <c>503 curia/store/unavailable</c> and spends nothing.
/// <b>EPP14</b>: a refusal's <c>title</c>, <c>detail</c> and <c>type</c>, and every line the Forum logs for the request, carry no value the sender supplied (the <c>RiskFlag</c> shape's lesson: a thing that
/// observes content carries no content). Red on the tree: the route reads no nonce and consumes no <c>jti</c>, and refuses nothing.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class EnrollmentProofFaultTests(FaultableForumFixture forum) : IClassFixture<FaultableForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => forum.Client;

    private static void AssertProblem(Answer answer, HttpStatusCode status, string type) =>
        Assert.True(answer.Status == status && answer.Type == type, $"expected {(int)status} {type}; the Forum answered {answer.Says}");

    /// <summary>
    /// EPP12, the log. A proof that does not verify answers <c>proof-not-possessed</c> while the log is faulted: the log is never reached. A verified proof answers <c>503 log/unreadable</c> and spends its
    /// <c>jti</c>: the same proof, once the fault is lifted, is <c>proof-replayed</c> and not <c>201</c> (R5.23's rule, which G79 copies).
    /// </summary>
    [Fact]
    public async Task R4_46_EPP12_AFaultOfTheLogIsNotSeenByAProofThatFailsAndSpendsOneThatVerifies()
    {
        var agent = NewAgent("epp12-log");
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var nonce = await NonceAsync(Client, Ct);
        var forged = Proof(agent, Audience(Client), nonce, forum.Now, signer: stranger);
        var genuine = Proof(agent, Audience(Client), nonce, forum.Now);

        forum.Store.Arm(EventStoreFault.ThrowUnreadable);
        try
        {
            AssertRefused(await PostAsync(Client, agent, forged, Ct), "proof-not-possessed");
            AssertProblem(await PostAsync(Client, agent, genuine, Ct), HttpStatusCode.ServiceUnavailable, PublishedStoreFaults.LogUnreadableType);
        }
        finally
        {
            forum.Store.Disarm();
        }

        AssertRefused(await PostAsync(Client, agent, genuine, Ct), "proof-replayed");
    }

    /// <summary>EPP12, the nonce store, at the nonce route: a driver fault answers <c>503 store/unavailable</c>.</summary>
    [Fact]
    public async Task R4_47_EPP12_AFaultOfTheNonceStoreAnswers503AtTheNonceRoute()
    {
        forum.Nonces.Arm();
        try
        {
            using var response = await Client.GetAsync(new Uri(NonceRoute, UriKind.Relative), Ct);
            AssertProblem(new Answer(response.StatusCode, await response.Content.ReadAsStringAsync(Ct)), HttpStatusCode.ServiceUnavailable, PublishedStoreFaults.StoreUnavailableType);
        }
        finally
        {
            forum.Nonces.Disarm();
        }
    }

    /// <summary>EPP12, the nonce store, at the enrollment route: a valid proof answers <c>503 store/unavailable</c> and spends nothing, so the same proof is accepted once the fault is lifted.</summary>
    [Fact]
    public async Task R4_46_EPP12_AFaultOfTheNonceStoreAnswers503AtEnrollmentAndSpendsNothing()
    {
        var agent = NewAgent("epp12-nonce");
        var proof = Proof(agent, Audience(Client), await NonceAsync(Client, Ct), forum.Now);

        forum.Nonces.Arm();
        try
        {
            AssertProblem(await PostAsync(Client, agent, proof, Ct), HttpStatusCode.ServiceUnavailable, PublishedStoreFaults.StoreUnavailableType);
        }
        finally
        {
            forum.Nonces.Disarm();
        }

        var after = await PostAsync(Client, agent, proof, Ct);
        Assert.True(after.Status == HttpStatusCode.Created, $"a refusal for a faulted nonce store spent the proof: {after.Says}");
    }

    /// <summary>EPP12, the replay cache: a valid proof answers <c>503 store/unavailable</c> and, the cache never having recorded it, is accepted once the fault is lifted.</summary>
    [Fact]
    public async Task R4_46_EPP12_AFaultOfTheReplayCacheAnswers503AndSpendsNothing()
    {
        var agent = NewAgent("epp12-replay");
        var proof = Proof(agent, Audience(Client), await NonceAsync(Client, Ct), forum.Now);

        forum.Replay.Arm();
        try
        {
            AssertProblem(await PostAsync(Client, agent, proof, Ct), HttpStatusCode.ServiceUnavailable, PublishedStoreFaults.StoreUnavailableType);
        }
        finally
        {
            forum.Replay.Disarm();
        }

        var after = await PostAsync(Client, agent, proof, Ct);
        Assert.True(after.Status == HttpStatusCode.Created, $"a refusal for a faulted replay cache spent the proof: {after.Says}");
    }

    private const string Marker = "EPPMARK";

    /// <summary>
    /// EPP14: a request whose identifier, kid, proof, nonce, <c>jti</c> and header kid each carry the marker is refused by name, and neither the refusal nor any line the Forum logged for it carries the marker.
    /// Each variant is held first to the refusal it should be (a request that was accepted logs and says nothing about what a refusal echoes).
    /// </summary>
    [Fact]
    public async Task R4_46_EPP14_ARefusalAndTheLogCarryNoValueTheSenderSupplied()
    {
        var audience = Audience(Client);
        var nonce = await NonceAsync(Client, Ct);

        async Task CheckAsync(string label, ForumAgent agent, string proof, string slug)
        {
            var mark = forum.Log.Mark();
            var answer = await PostAsync(Client, agent, proof, Ct);
            AssertRefused(answer, slug);
            Assert.DoesNotContain(Marker, answer.Body, StringComparison.Ordinal);
            var logged = forum.Log.Since(mark);
            foreach (var line in logged)
            {
                Assert.True(!line.Message.Contains(Marker, StringComparison.Ordinal), $"{label}: a log line carries the sender's value: {line.Message}");
                Assert.All(line.Values, v => Assert.DoesNotContain(Marker, v.Value?.ToString() ?? string.Empty, StringComparison.Ordinal));
            }
        }

        ForumAgent Marked(string stem) => ForumAgent.Create($"https://agents.example/{Marker}-{stem}-{Guid.NewGuid():N}"[..60], $"{Marker}-{stem}-{Guid.NewGuid():N}"[..40]);

        await CheckAsync("proof", Marked("p"), $"{Marker}-proof", "proof-malformed");

        var staleNonce = Marked("n");
        await CheckAsync("nonce", staleNonce, Proof(staleNonce, audience, $"{Marker}-nonce", forum.Now), "proof-nonce-stale");

        var headerKid = Marked("k");
        await CheckAsync("header kid", headerKid, Proof(headerKid, audience, nonce, forum.Now, kid: $"{Marker}-header-kid"), "proof-mismatch");

        var claimAgent = Marked("a");
        await CheckAsync("agent_id claim", claimAgent, Proof(claimAgent, audience, nonce, forum.Now, agentId: $"https://agents.example/{Marker}-claim"), "proof-mismatch");

        var spent = Marked("j");
        var proof = Proof(spent, audience, nonce, forum.Now, jti: $"{Marker}-jti");
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(Client, spent, proof, Ct)).Status);
        await CheckAsync("jti", spent, proof, "proof-replayed");
    }
}
