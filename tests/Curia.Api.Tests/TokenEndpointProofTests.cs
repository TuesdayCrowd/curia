using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Client;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// D29, at the token endpoint. Before it closed, the endpoint read a token request's DPoP proof only
/// for its <c>jwk</c>, computed that key's thumbprint and bound the token to it: it checked no
/// <c>typ</c>, no algorithm, no signature, no <c>htm</c>, <c>htu</c> or <c>iat</c>, and never recorded
/// the proof's <c>jti</c>. So a proof thirty days old, one for another host, one signed over another
/// payload and one over a key that is no point on the curve were each answered 200. R5.13 has the
/// proof validated once, in one module, for every host: each fact below sends a valid client
/// assertion with one part of the proof changed, and asserts the refusal the one validator answers.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the register entries they enforce verbatim.")]
[Collection("forum")]
public sealed class TokenEndpointProofTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    [Fact]
    public Task D29_AProofWhoseIatIsThirtyDaysOldIsRefused() =>
        AssertRefusedAsync(dpop => dpop.Proof("POST", TokenEndpoint, forum.Now - TimeSpan.FromDays(30)));

    [Fact]
    public Task D29_AProofWhoseHtuNamesAnotherHostIsRefused() =>
        AssertRefusedAsync(dpop => dpop.Proof("POST", "http://another.example/oauth/token", forum.Now));

    [Fact]
    public Task D29_AProofWhoseHtmIsGetIsRefused() =>
        AssertRefusedAsync(dpop => dpop.Proof("GET", TokenEndpoint, forum.Now));

    [Fact]
    public Task D29_AProofWhoseTypIsNotDpopJwtIsRefused() =>
        AssertRefusedAsync(dpop => dpop.Proof("POST", TokenEndpoint, forum.Now, header => header["typ"] = "JWT", _ => { }));

    [Fact]
    public Task D29_AProofSignedOverAnotherPayloadIsRefused() =>
        AssertRefusedAsync(dpop =>
        {
            var proof = dpop.Proof("POST", TokenEndpoint, forum.Now);
            return proof[..(proof.LastIndexOf('.') + 1)] + Base64Url.EncodeToString(new byte[64]);
        });

    [Fact]
    public Task D29_AProofWhoseHeaderAlgIsEdDsaOverAP256JwkIsRefused() =>
        AssertRefusedAsync(dpop => dpop.Proof("POST", TokenEndpoint, forum.Now, header => header["alg"] = "EdDSA", _ => { }));

    [Fact]
    public Task D29_AProofOverAnOffCurveKeyIsRefused() =>
        AssertRefusedAsync(dpop => dpop.Proof("POST", TokenEndpoint, forum.Now, header => header["jwk"] = new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = Base64Url.EncodeToString(Enumerable.Repeat((byte)1, 32).ToArray()),
            ["y"] = Base64Url.EncodeToString(Enumerable.Repeat((byte)2, 32).ToArray()),
        }, _ => { }));

    /// <summary>
    /// R5.14 at the token endpoint: a proof's <c>jti</c> is recorded, so one proof is accepted once. Two
    /// client assertions, each with its own <c>jti</c>, carry the same proof. Resending the identical
    /// request would test nothing: the assertion's own <c>jti</c> refuses it first, 401
    /// <c>invalid_client</c>, before and after D29 alike, because the assertion validator shares the
    /// replay cache.
    /// </summary>
    [Fact]
    public async Task D29_AProofSentTwiceIsRefusedTheSecondTime()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agent, dpop) = await EnrolledAsync(ct);
        var proof = dpop.Proof("POST", TokenEndpoint, forum.Now);

        var (firstStatus, firstBody) = await DpopClient.RequestTokenWithProofAsync(
            forum.Client, agent.AgentId, dpop.ClientAssertion(TokenEndpoint, forum.Now), proof, ct);
        Assert.True(firstStatus == HttpStatusCode.OK, $"the first send was refused, so this fact proves nothing: {(int)firstStatus} {firstBody}");

        var (secondStatus, secondBody) = await DpopClient.RequestTokenWithProofAsync(
            forum.Client, agent.AgentId, dpop.ClientAssertion(TokenEndpoint, forum.Now), proof, ct);
        AssertInvalidDpopProof(secondStatus, secondBody);
    }

    /// <summary>The reference client's own token flow, through <see cref="ForumSession"/>, is still answered with a token.</summary>
    [Fact]
    public async Task D29_TheReferenceClientsTokenRequestStillSucceeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var home = Directory.CreateTempSubdirectory("curia-token-proof-").FullName;
        try
        {
            var suffix = Guid.NewGuid().ToString("N")[..8];
            var slug = "token-proof-" + suffix;
            var store = new ProfileStore(home);
            Assert.True(store.Create(slug, "https://agents.example/" + slug, slug + "-k", forum.Client.BaseAddress!)
                .TryGetValue(out var agent, out var createError), createError?.Detail);
            using (agent)
            {
                var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
                var enrolled = await client.EnrolAsync(agent!, ct);
                Assert.True(enrolled.TryGetValue(out _, out var enrolRefusal), enrolRefusal?.Summary);

                var session = new ForumSession(client, agent!, store, forum.Clock);
                var token = await session.AccessTokenAsync(ct);

                Assert.True(token.TryGetValue(out var accessToken, out var refusal), refusal?.Summary);
                Assert.False(string.IsNullOrEmpty(accessToken));
            }
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private async Task AssertRefusedAsync(Func<DpopClient, string> proof)
    {
        var ct = TestContext.Current.CancellationToken;
        var (agent, dpop) = await EnrolledAsync(ct);

        var (status, body) = await DpopClient.RequestTokenWithProofAsync(
            forum.Client, agent.AgentId, dpop.ClientAssertion(TokenEndpoint, forum.Now), proof(dpop), ct);

        AssertInvalidDpopProof(status, body);
    }

    private static void AssertInvalidDpopProof(HttpStatusCode status, string body)
    {
        Assert.True(status == HttpStatusCode.BadRequest, $"{(int)status} {body[..Math.Min(body.Length, 240)]}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal("invalid_dpop_proof", json.RootElement.GetProperty("error").GetString());
    }

    private async Task<(ForumAgent Agent, DpopClient Dpop)> EnrolledAsync(CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/token-proof-" + suffix, "token-proof-" + suffix);
        using var enrolled = await agent.EnrollAsync(forum.Client, ct);
        Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);
        return (agent, DpopClient.For(agent, agent.AssertionKey));
    }
}
