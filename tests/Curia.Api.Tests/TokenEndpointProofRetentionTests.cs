using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R5.14 at the token endpoint: a proof's <c>jti</c> is held for as long as the proof could be
/// accepted, which is until its <c>iat</c> plus the permitted skew, not until the instant it was
/// consumed plus that skew. A proof dated ahead of the time it was consumed would otherwise be
/// accepted again once its <c>jti</c> lapsed. These advance the Forum's clock, so they run on a fixture
/// of their own and cannot age another fact's token, nonce or assertion.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TokenEndpointProofRetentionTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    [Fact]
    public Task R5_14_AProofDatedAheadIsNotAcceptedAgainOnceTheOldRetentionLapses() =>
        AssertHeldAsync(ahead: TimeSpan.FromSeconds(20), advance: TimeSpan.FromSeconds(35));

    [Fact]
    public Task R5_14_AProofAtTheEdgeOfItsWindowIsNotAcceptedAgain() =>
        AssertHeldAsync(ahead: TimeSpan.FromSeconds(30), advance: TimeSpan.FromSeconds(60));

    private async Task AssertHeldAsync(TimeSpan ahead, TimeSpan advance)
    {
        var ct = TestContext.Current.CancellationToken;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/proof-retention-" + suffix, "proof-retention-" + suffix);
        using (var enrolled = await agent.EnrollAsync(forum.Client, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);
        var dpop = DpopClient.For(agent, agent.AssertionKey);

        var proof = dpop.Proof("POST", TokenEndpoint, forum.Now + ahead);

        var (firstStatus, firstBody) = await DpopClient.RequestTokenWithProofAsync(
            forum.Client, agent.AgentId, dpop.ClientAssertion(TokenEndpoint, forum.Now), proof, ct);
        Assert.True(firstStatus == HttpStatusCode.OK, $"the first send was refused, so this fact proves nothing: {(int)firstStatus} {firstBody}");

        forum.Clock.Advance(advance);

        var (secondStatus, secondBody) = await DpopClient.RequestTokenWithProofAsync(
            forum.Client, agent.AgentId, dpop.ClientAssertion(TokenEndpoint, forum.Now), proof, ct);
        Assert.True(secondStatus == HttpStatusCode.BadRequest, $"{(int)secondStatus} {secondBody[..Math.Min(secondBody.Length, 240)]}");
        using var json = JsonDocument.Parse(secondBody);
        Assert.Equal("invalid_dpop_proof", json.RootElement.GetProperty("error").GetString());
    }
}
