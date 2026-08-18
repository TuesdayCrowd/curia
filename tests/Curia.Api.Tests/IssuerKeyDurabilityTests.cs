using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// The one property the issuer's signing key has to have: <b>a token minted before a restart still
/// verifies after one</b> -- and, alongside it, that an enrollment survives a restart at all.
///
/// <para>The signing key used to be generated in <c>TokenIssuer</c>'s constructor, so it was fresh
/// per process: every token minted before a restart became unverifiable after it, and the served
/// JWKS described a key nobody had signed anything with. Against a five-minute token lifetime
/// (R5.2) that is not a short-lived credential, it is an intermittent outage that presents as a
/// signature attack. The Registrar's key store had the matching defect one layer down -- it forgot
/// every enrollment, which made every post ever made unverifiable.</para>
///
/// <para><b>A restart is modeled as a second host over the same configuration and the same
/// database</b> -- a new dependency-injection container, a new <c>TokenIssuer</c>, a new
/// <c>IssuerSigningKey</c> loaded afresh from the same PEM, a new <c>PostgresAgentKeyStore</c>.
/// That is what a process restart is from the point of view of anything these tests can observe,
/// and it is why the assertions go through HTTP rather than comparing two objects: an in-process
/// comparison would still pass if the composition root had quietly stopped reading the configured
/// key.</para>
///
/// <para><b>Standing survives too, now.</b> It did not when this file was written:
/// <c>Curia.Api.AgentDirectory</c> held the enrollment instant, the owner-verification flag and
/// the first-reached-T1 instant in an in-process <c>ConcurrentDictionary</c>, so a restarted host
/// had the agent's <i>keys</i> and none of its <i>standing</i> -- and every test below that needed
/// standing had to re-announce the enrollment against the restarted host to work around it. Those
/// re-announcements are gone: the facts are append-only credential-lifecycle events (R4.21) folded
/// by <c>Curia.Application.Projections.AgentStandingProjector</c>, and
/// <see cref="AgentStandingDurabilityTests"/> asserts that half directly. What is left here is the
/// issuer key and the Registrar's key store, which is what this file was always about.</para>
/// </summary>
public sealed class IssuerKeyDurabilityTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private async Task<(ForumAgent Agent, DpopClient Client, string Token, string Board)> EnrolledAsync(
        HttpClient http, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/dur-" + suffix, "dr-" + suffix);

        Assert.Equal(HttpStatusCode.Created, (await agent.EnrollAsync(http, ct)).StatusCode);

        var client = DpopClient.For(agent, agent.AssertionKey);
        return (agent, client, await client.GetTokenAsync(http, TokenEndpoint, forum.Now, ct), "board-" + suffix);
    }

    [Fact]
    public async Task ATokenMintedBeforeARestartStillVerifiesAfterOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, client, token, board) = await EnrolledAsync(http, ct);

        // The restart. Same PEM, same database; everything else is built from nothing.
        using var restarted = forum.WithWebHostBuilder(_ => { });
        using var afterRestart = restarted.CreateClient();

        // Nothing is re-announced. The restarted host reads the agent's standing out of the log
        // (see the class remarks), so the only thing left that could refuse this request is the
        // issuer key -- which is what the test is for.
        var wire = agent.SignQuestion(board, "Minted before, spent after.", "Durable issuer key", forum.Now);
        using var response = await client.PostAsync(afterRestart, PostsUrl, token, wire, forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// The falsification, without which the test above would pass for a Forum that had stopped
    /// checking the token's signature at all: a host holding a <i>different</i> issuer key refuses
    /// the same token. Everything else about the two hosts is identical -- same database, same
    /// standing read out of the same log -- so the key is the only variable. This is also, precisely, what
    /// the old per-process key did on every restart.
    /// </summary>
    [Fact]
    public async Task ATokenIsRefusedByAHostHoldingADifferentIssuerKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, client, token, board) = await EnrolledAsync(http, ct);

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherPem = otherKey.ExportPkcs8PrivateKeyPem();

        using var impostor = forum.WithWebHostBuilder(
            b => b.UseSetting("Curia:IssuerSigningKeyPem", otherPem));
        using var wrongKeyHost = impostor.CreateClient();

        var wire = agent.SignQuestion(board, "Wrong key.", "Rotated out", forum.Now);
        using var response = await client.PostAsync(wrongKeyHost, PostsUrl, token, wire, forum.Now, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// The <c>kid</c> is stable across a restart too, and it is the RFC 7638 thumbprint of the key
    /// rather than a value generated per process. That is what lets a client cache the JWKS: a
    /// <c>kid</c> that changed on restart would make every cached key document wrong at an
    /// unpredictable moment, which is the same outage one indirection removed.
    /// </summary>
    [Fact]
    public async Task TheServedIssuerJwksIsIdenticalAcrossARestart()
    {
        var ct = TestContext.Current.CancellationToken;

        var before = await forum.Client.GetFromJsonAsync<JsonElement>("/oauth/jwks", ct);

        using var restarted = forum.WithWebHostBuilder(_ => { });
        using var afterRestart = restarted.CreateClient();
        var after = await afterRestart.GetFromJsonAsync<JsonElement>("/oauth/jwks", ct);

        var keyBefore = before.GetProperty("keys")[0];
        var keyAfter = after.GetProperty("keys")[0];

        Assert.Equal(keyBefore.GetProperty("kid").GetString(), keyAfter.GetProperty("kid").GetString());
        Assert.Equal(keyBefore.GetProperty("x").GetString(), keyAfter.GetProperty("x").GetString());
        Assert.Equal(keyBefore.GetProperty("y").GetString(), keyAfter.GetProperty("y").GetString());
    }

    /// <summary>
    /// The Registrar's key store survives a restart, which is the whole reason it moved to
    /// Postgres. Under <c>InMemoryAuthorKeyResolver</c> the restarted host held no keys at all --
    /// the agent JWKS would 404 and every post ever made would be unverifiable, because R6.31 asks
    /// which key was valid at each post's <c>server_ts</c> and an empty store answers "none" for
    /// all of them.
    /// </summary>
    [Fact]
    public async Task AnAgentsRegisteredKeysSurviveARestart()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, _, _, _) = await EnrolledAsync(http, ct);

        using var restarted = forum.WithWebHostBuilder(_ => { });
        using var afterRestart = restarted.CreateClient();

        var jwks = await afterRestart.GetFromJsonAsync<JsonElement>(
            "/v1/jwks?agent=" + Uri.EscapeDataString(agent.AgentId), ct);

        var keys = jwks.GetProperty("keys");
        Assert.Equal(1, keys.GetArrayLength());
        Assert.Equal(agent.Kid, keys[0].GetProperty("kid").GetString());
        Assert.Equal("ES256", keys[0].GetProperty("alg").GetString());

        // R4.16 rev. requires the served document to carry validity intervals, so a verifier can
        // apply R6.31 itself rather than being handed an answer pre-baked for "now".
        Assert.NotNull(keys[0].GetProperty("curia_not_before").GetString());
    }

    /// <summary>
    /// The <c>kid</c> uniqueness constraint survives too, and it has to: it is enforced by a
    /// PRIMARY KEY rather than by a scan of process memory, so a host that has just come up
    /// already knows the identifier is taken. A restarted in-memory store would have handed the
    /// <c>kid</c> to whoever asked next, and <c>IAgentKeyResolver</c> resolves by <c>kid</c> alone.
    /// </summary>
    [Fact]
    public async Task AKidRegisteredBeforeARestartIsStillRefusedToAnotherAgentAfterOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, _, _, _) = await EnrolledAsync(http, ct);

        using var restarted = forum.WithWebHostBuilder(_ => { });
        using var afterRestart = restarted.CreateClient();

        var impostor = ForumAgent.Create("https://agents.example/impostor-" + Guid.NewGuid().ToString("N")[..8], agent.Kid);
        using var response = await impostor.EnrollAsync(afterRestart, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
