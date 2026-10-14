using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curia.Api;
using Curia.AuthN.Ports;
using Curia.Application.Ports;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// What the two key-act routes do over HTTP (errata G21, spec section 4.7): the same requests and the same
/// signatures the use cases' own facts build, sent to the running Forum behind the permissive lifecycle gate
/// (<see cref="ForumFixture"/>'s default). The guard's facts run on <see cref="GuardedForumFixture"/> in
/// <see cref="KeyRotationGuardedEndpointTests"/>.
/// </summary>
internal static class KeyActRequests
{
    internal const string TokenEndpoint = "http://localhost/oauth/token";

    internal static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>
    /// The route for an agent whose identifier is a URL. A slash is escaped, since a segment cannot hold one,
    /// and a colon is not: the Forum reads its own request URL back from the path the server hands it (the
    /// colon decoded, <c>%2F</c> kept), and a proof's <c>htu</c> must equal that string.
    /// </summary>
    internal static string RotateUrl(string agentId) =>
        $"http://localhost/v1/agents/{Segment(agentId)}/keys";

    private static string Segment(string value) => Uri.EscapeDataString(value).Replace("%3A", ":", StringComparison.Ordinal);

    internal static string RetireUrl(string agentId, string kid) =>
        $"{RotateUrl(agentId)}/{Segment(kid)}/retire";

    internal static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} {e.Detail}"));

    /// <summary>A key the agent signs with: a P-256 pair under a <c>kid</c>.</summary>
    internal sealed class KeyPair(string kid, ECDsa key)
    {
        internal string Kid { get; } = kid;

        internal PublicKeyMaterial Material => new("ES256", Kid, key.ExportSubjectPublicKeyInfo());

        internal SigningKey Signing => new("ES256", Kid, key.ExportPkcs8PrivateKey());

        [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The key lives as long as the test that made it.")]
        internal static KeyPair New(string kid) => new(kid, ECDsa.Create(ECCurve.NamedCurves.nistP256));
    }

    internal static string Canonical(JsonValue.Object statement) =>
        Encoding.UTF8.GetString(Require(CanonicalJson.CanonicalizeWithNfc(statement)).Span);

    internal static string RotationStatement(string agent, string authorizedBy, KeyPair next, long prev) =>
        Canonical(new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent)),
            new("authorized_by", new JsonValue.String(authorizedBy)),
            new("jwk", Require(PublicJwk.Of(next.Material))),
            new("kid", new JsonValue.String(next.Kid)),
            new("prev", new JsonValue.Number(prev)),
            new("typ", new JsonValue.String(KeyStatements.RotationStatementTyp)),
        ]));

    internal static string RetirementStatement(string agent, string kid, long prev) =>
        Canonical(new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent)),
            new("kid", new JsonValue.String(kid)),
            new("prev", new JsonValue.Number(prev)),
            new("typ", new JsonValue.String(KeyStatements.RetirementStatementTyp)),
        ]));

    /// <summary>A detached JWS over <paramref name="statement"/> under header <c>typ</c> <paramref name="typ"/>, signed with <paramref name="key"/>.</summary>
    internal static string Sign(string statement, string typ, SigningKey key)
    {
        var parsed = Require(JsonReader.Parse(Encoding.UTF8.GetBytes(statement), AdmitLimits.Default));
        var canonical = Require(CanonicalJson.CanonicalizeWithNfc(parsed));
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner> { ["ES256"] = new Es256Signer() },
            new Dictionary<string, IContentVerifier>(),
            typ);
        return Require(jws.Sign(canonical, key)).Compact;
    }

    /// <summary>A rotation's three strings: the authorizer is the agent's enrolled key, the possession is the new key's.</summary>
    internal static (string Statement, string Authorization, string Possession) RotationParts(ForumAgent agent, KeyPair next, long prev)
    {
        var statement = RotationStatement(agent.AgentId, agent.Kid, next, prev);
        var authorizer = new SigningKey("ES256", agent.Kid, agent.AssertionKey.ExportPkcs8PrivateKey());
        return (
            statement,
            Sign(statement, KeyStatements.RotationHeaderTyp, authorizer),
            Sign(statement, KeyStatements.RotationHeaderTyp, next.Signing));
    }

    /// <summary>A retirement's two strings: <paramref name="victim"/> signs its own.</summary>
    internal static (string Statement, string Authorization) RetirementParts(string agentId, KeyPair victim, long prev)
    {
        var statement = RetirementStatement(agentId, victim.Kid, prev);
        return (statement, Sign(statement, KeyStatements.RetirementHeaderTyp, victim.Signing));
    }

    /// <summary>The body of a rotation.</summary>
    internal static byte[] RotationBody(ForumAgent agent, KeyPair next, long prev)
    {
        var (statement, authorization, possession) = RotationParts(agent, next, prev);
        return Body(("statement", statement), ("authorization", authorization), ("possession", possession));
    }

    /// <summary>The body of a retirement of <paramref name="victim"/>, signed by itself.</summary>
    internal static byte[] RetirementBody(string agentId, KeyPair victim, long prev)
    {
        var (statement, authorization) = RetirementParts(agentId, victim, prev);
        return Body(("statement", statement), ("authorization", authorization));
    }

    internal static byte[] Body(params (string Name, string Value)[] members) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(members.ToDictionary(m => m.Name, m => m.Value)));

    /// <summary>The key set the Forum publishes for <paramref name="agentId"/>.</summary>
    internal static async Task<JsonDocument> KeySetAsync(HttpClient client, string agentId, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    internal static long[] LifecycleOf(JsonDocument keySet) =>
        keySet.RootElement.GetProperty("curia_lifecycle").EnumerateArray().Select(i => i.GetInt64()).ToArray();

    internal static async Task<long> LatestLifecycleAsync(HttpClient client, string agentId, CancellationToken ct)
    {
        using var keySet = await KeySetAsync(client, agentId, ct);
        return LifecycleOf(keySet)[^1];
    }

    internal static async Task<(string Type, string? Detail, string Body)> ProblemOfAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(body);
        var detail = json.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        return (json.RootElement.GetProperty("type").GetString()!, detail, body);
    }

    /// <summary>ES256 over the BCL, in JWS's fixed-width r||s form.</summary>
    private sealed class Es256Signer : IContentSigner
    {
        public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key)
        {
            using var signer = ECDsa.Create();
            signer.ImportPkcs8PrivateKey(key.Private.Span, out _);
            return signer.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }
}

/// <summary>
/// Replaces the PDP with one that evaluates every request as a quarantined credential's. Nothing in the log
/// produces a quarantined credential yet (a posture trip has no event), so the policy is asked, over HTTP,
/// as it would be when one did.
/// </summary>
internal sealed class QuarantinedPolicyDecisionPoint(IPolicyDecisionPoint inner) : IPolicyDecisionPoint
{
    public ValueTask<Result<AuthorizationDecision>> EvaluateAsync(
        AuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return inner.EvaluateAsync(request with { CredentialState = CredentialState.Quarantined }, cancellationToken);
    }
}

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class KeyRotationEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static async Task<HttpResponseMessage> PostAsync(
        HttpClient client, DpopClient dpop, string token, string url, byte[] body, DateTimeOffset now, CancellationToken ct) =>
        await dpop.PostAsync(client, url, token, body, now, ct, contentType: "application/json");

    private async Task<(ForumAgent Agent, DpopClient Dpop, string Token)> EnrolledAsync(HttpClient client, string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create(KeyActRequests.Unique(stem), stem + "-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        return (agent, dpop, token);
    }

    [Fact]
    public async Task R4_18_RotationOverHttpBindsAndAnswersItsIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await EnrolledAsync(client, "rotator", ct);
        var next = KeyActRequests.KeyPair.New("next-" + Guid.NewGuid().ToString("N")[..8]);
        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);

        using var response = await PostAsync(
            client, dpop, token, KeyActRequests.RotateUrl(agent.AgentId), KeyActRequests.RotationBody(agent, next, prev), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(agent.AgentId, json.RootElement.GetProperty("agent_id").GetString());
        Assert.Equal(next.Kid, json.RootElement.GetProperty("kid").GetString());
        Assert.Equal(agent.Kid, json.RootElement.GetProperty("authorized_by").GetString());
        Assert.True(json.RootElement.GetProperty("log_index").GetInt64() > prev);
        Assert.Equal(json.RootElement.GetProperty("log_index").GetInt64(), await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct));
    }

    [Fact]
    public async Task R4_18_RotationForAnotherAgentIsNotOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (victim, _, _) = await EnrolledAsync(client, "victim", ct);
        var (thief, thiefDpop, thiefToken) = await EnrolledAsync(client, "thief", ct);
        var next = KeyActRequests.KeyPair.New("stolen-" + Guid.NewGuid().ToString("N")[..8]);
        var prev = await KeyActRequests.LatestLifecycleAsync(client, victim.AgentId, ct);

        // The thief signs a rotation of the victim's identity with its own key and its own token.
        using var response = await PostAsync(
            client, thiefDpop, thiefToken, KeyActRequests.RotateUrl(victim.AgentId), KeyActRequests.RotationBody(thief, next, prev), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("curia/keys/not-own", (await KeyActRequests.ProblemOfAsync(response, ct)).Type);
    }

    /// <summary>The rotation route's 409 for a kid another identity holds; red when <c>KidTakenType</c> leaves the 409 list (F1-94).</summary>
    [Fact]
    public async Task R4_18_RotationToAKidAnotherIdentityHoldsAnswers409KidTaken()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (holder, _, _) = await EnrolledAsync(client, "holder", ct);
        var (agent, dpop, token) = await EnrolledAsync(client, "taker", ct);
        var next = KeyActRequests.KeyPair.New(holder.Kid);
        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);

        using var response = await PostAsync(
            client, dpop, token, KeyActRequests.RotateUrl(agent.AgentId), KeyActRequests.RotationBody(agent, next, prev), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var (type, detail, body) = await KeyActRequests.ProblemOfAsync(response, ct);
        Assert.Equal("curia/keys/kid-taken", type);
        Assert.Equal($"kid={holder.Kid}: nothing was registered.", detail);
        Assert.DoesNotContain(holder.AgentId, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R4_39_RetireOverHttp()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await EnrolledAsync(client, "retirer", ct);
        var next = KeyActRequests.KeyPair.New("second-" + Guid.NewGuid().ToString("N")[..8]);
        using var rotated = await PostAsync(
            client, dpop, token, KeyActRequests.RotateUrl(agent.AgentId),
            KeyActRequests.RotationBody(agent, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);

        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        using var response = await PostAsync(
            client, dpop, token, KeyActRequests.RetireUrl(agent.AgentId, next.Kid),
            KeyActRequests.RetirementBody(agent.AgentId, next, prev), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(agent.AgentId, json.RootElement.GetProperty("agent_id").GetString());
        Assert.Equal(next.Kid, json.RootElement.GetProperty("kid").GetString());
        Assert.True(json.RootElement.GetProperty("log_index").GetInt64() > prev);
    }

    [Fact]
    public async Task R4_35_TheKeySetPublishesStatusAndLifecycle()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await EnrolledAsync(client, "published", ct);

        using (var enrolled = await KeyActRequests.KeySetAsync(client, agent.AgentId, ct))
        {
            var only = Assert.Single(enrolled.RootElement.GetProperty("keys").EnumerateArray());
            Assert.Equal("active", only.GetProperty("curia_status").GetString());
            Assert.False(only.TryGetProperty("curia_retired_log_index", out _));
            Assert.Equal(2, KeyActRequests.LifecycleOf(enrolled).Length);
        }

        var next = KeyActRequests.KeyPair.New("later-" + Guid.NewGuid().ToString("N")[..8]);
        using var rotated = await PostAsync(
            client, dpop, token, KeyActRequests.RotateUrl(agent.AgentId),
            KeyActRequests.RotationBody(agent, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);

        using var retired = await PostAsync(
            client, dpop, token, KeyActRequests.RetireUrl(agent.AgentId, next.Kid),
            KeyActRequests.RetirementBody(agent.AgentId, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, retired.StatusCode);
        using var retiredJson = JsonDocument.Parse(await retired.Content.ReadAsStringAsync(ct));

        using var after = await KeyActRequests.KeySetAsync(client, agent.AgentId, ct);
        var lifecycle = KeyActRequests.LifecycleOf(after);
        Assert.Equal(4, lifecycle.Length);
        Assert.Equal(lifecycle.Order().ToArray(), lifecycle);
        Assert.Equal(retiredJson.RootElement.GetProperty("log_index").GetInt64(), lifecycle[^1]);

        var keys = after.RootElement.GetProperty("keys").EnumerateArray().ToDictionary(k => k.GetProperty("kid").GetString()!);
        Assert.Equal("active", keys[agent.Kid].GetProperty("curia_status").GetString());
        Assert.Equal("retired", keys[next.Kid].GetProperty("curia_status").GetString());
        Assert.Equal(lifecycle[^1], keys[next.Kid].GetProperty("curia_retired_log_index").GetInt64());
        Assert.False(keys[agent.Kid].TryGetProperty("curia_retired_log_index", out _));
    }

    [Fact]
    public async Task Table10_KeyRotateAndRetireAreDeniedToAnonymous()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        foreach (var url in new[] { KeyActRequests.RotateUrl("anyone"), KeyActRequests.RetireUrl("anyone", "k") })
        {
            using var content = new ByteArrayContent(KeyActRequests.Body(("statement", "{}"), ("authorization", "a"), ("possession", "p")));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            using var response = await client.PostAsync(new Uri(new Uri(url).PathAndQuery, UriKind.Relative), content, ct);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var pdp = forum.Services.GetRequiredService<IPolicyDecisionPoint>();
        foreach (var action in new[] { ActionKind.Rotate, ActionKind.Retire })
        {
            var decision = await pdp.EvaluateAsync(
                new AuthorizationRequest(EvaluatedTier.Anonymous(forum.Now), CredentialState.Active, ResourceKind.Key, action), ct);

            Assert.True(decision.TryGetValue(out var d, out _));
            Assert.Equal(DecisionEffect.Deny, d!.Effect);
            Assert.Equal("table-10/denied", d.Reason);
        }
    }

    /// <summary>R4.39: the PDP, asked as a quarantined credential: rotate denied, retire permitted. Replaces <c>Table10_QuarantineRefusesBoth</c>.</summary>
    [Fact]
    public async Task Table10_QuarantineRefusesRotationAndPermitsRetirement()
    {
        var ct = TestContext.Current.CancellationToken;
        using var quarantined = QuarantinedHost();
        var client = quarantined.CreateClient();
        var (agent, dpop, token) = await EnrolledAsync(client, "quarantined", ct);
        var next = KeyActRequests.KeyPair.New("q-" + Guid.NewGuid().ToString("N")[..8]);

        using var response = await PostAsync(
            client, dpop, token, KeyActRequests.RotateUrl(agent.AgentId),
            KeyActRequests.RotationBody(agent, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var (type, detail, _) = await KeyActRequests.ProblemOfAsync(response, ct);
        Assert.Equal("curia/authz/denied", type);
        Assert.Contains("table-11/quarantined-read-only", detail, StringComparison.Ordinal);
    }

    /// <summary>R4.39 over HTTP: red when quarantine's intersection drops <c>retire</c> (F1-37).</summary>
    [Fact]
    public async Task R4_39_AQuarantinedAgentRetires()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await EnrolledAsync(client, "quarantine-retire", ct);
        var next = KeyActRequests.KeyPair.New("keep-" + Guid.NewGuid().ToString("N")[..8]);
        using var rotated = await PostAsync(
            client, dpop, token, KeyActRequests.RotateUrl(agent.AgentId),
            KeyActRequests.RotationBody(agent, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);

        using var quarantined = QuarantinedHost();
        var quarantinedClient = quarantined.CreateClient();
        using var response = await PostAsync(
            quarantinedClient, dpop, token, KeyActRequests.RetireUrl(agent.AgentId, next.Kid),
            KeyActRequests.RetirementBody(agent.AgentId, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>R7.23: a T0 agent that spent its posting budget still rotates and still retires. Red when <c>IsWrite</c>'s exclusion is removed (F1-49).</summary>
    [Fact]
    public async Task R7_23_KeyActsSpendNoPostingBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await EnrolledAsync(client, "budget", ct);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        for (var i = 0; i < TierPolicy.PostsPerDay(PrincipalTier.T0); i++)
        {
            using var ok = await dpop.PostAsync(
                client, "http://localhost/v1/posts", token, agent.SignQuestion(board, $"Budget question {i}?", $"B{i}", forum.Now), forum.Now, ct);
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        using var spent = await dpop.PostAsync(
            client, "http://localhost/v1/posts", token, agent.SignQuestion(board, "One too many?", "Over", forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Forbidden, spent.StatusCode);

        var next = KeyActRequests.KeyPair.New("after-budget-" + Guid.NewGuid().ToString("N")[..8]);
        using var rotated = await PostAsync(
            client, dpop, token, KeyActRequests.RotateUrl(agent.AgentId),
            KeyActRequests.RotationBody(agent, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);

        using var retired = await PostAsync(
            client, dpop, token, KeyActRequests.RetireUrl(agent.AgentId, next.Kid),
            KeyActRequests.RetirementBody(agent.AgentId, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, retired.StatusCode);
    }

    /// <summary>GF1's positive control: the requests the guarded Forum refuses are 201 behind the permissive gate.</summary>
    [Fact]
    public async Task R4_38_EachLifecycleRouteSucceedsUnderThePermissiveGate()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await EnrolledAsync(client, "control", ct);
        var next = KeyActRequests.KeyPair.New("control-" + Guid.NewGuid().ToString("N")[..8]);

        using var rotated = await PostAsync(
            client, dpop, token, KeyActRequests.RotateUrl(agent.AgentId),
            KeyActRequests.RotationBody(agent, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);

        using var retired = await PostAsync(
            client, dpop, token, KeyActRequests.RetireUrl(agent.AgentId, next.Kid),
            KeyActRequests.RetirementBody(agent.AgentId, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, retired.StatusCode);
    }

    /// <summary>The permissive gate enables every kind and the record approves none: the key set says both.</summary>
    [Fact]
    public async Task R4_38_ThePermissiveKeySetEnablesEveryKindAndApprovesNone()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, _, _) = await EnrolledAsync(client, "kinds", ct);

        using var keySet = await KeyActRequests.KeySetAsync(client, agent.AgentId, ct);
        var kinds = keySet.RootElement.GetProperty("curia_lifecycle_kinds");

        Assert.Empty(kinds.GetProperty("approved").EnumerateArray());
        Assert.Equal(
            ["rotation", "retirement", "compromise"],
            kinds.GetProperty("enabled").EnumerateArray().Select(k => k.GetString()!).ToArray());
    }

    private WebApplicationFactoryHost QuarantinedHost() =>
        new(forum.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPolicyDecisionPoint>();
            services.AddSingleton<IPolicyDecisionPoint>(new QuarantinedPolicyDecisionPoint(new DomainPolicyDecisionPoint()));
        })));

    /// <summary>A derived host and its clients, disposed together.</summary>
    private sealed class WebApplicationFactoryHost(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> factory) : IDisposable
    {
        public HttpClient CreateClient() => factory.CreateClient();

        public IServiceProvider Services => factory.Services;

        public void Dispose() => factory.Dispose();
    }
}

/// <summary>
/// GF1 (R4.38 (add.), spec section 4.13): on the Forum as <c>Program.cs</c> composes it, each lifecycle
/// route refuses first. Valid requests, valid signatures, a valid token and proof: the answer is 403
/// <c>curia/keys/lifecycle-not-enabled</c> with a fixed detail, and nothing was consumed, written or read
/// beyond it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class KeyRotationGuardedEndpointTests(GuardedForumFixture forum) : IClassFixture<GuardedForumFixture>
{
    [Fact]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The response is disposed by the using declaration.")]
    public async Task R4_38_EachLifecycleRouteRefusesBeforeAnythingElse()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var agent = ForumAgent.Create(KeyActRequests.Unique("guarded"), "guarded-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var next = KeyActRequests.KeyPair.New("guarded-next-" + Guid.NewGuid().ToString("N")[..8]);
        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);

        var rotate = (Url: KeyActRequests.RotateUrl(agent.AgentId), Body: KeyActRequests.RotationBody(agent, next, prev),
            Detail: "key rotation is not enabled on this Forum");
        var enrolled = new KeyActRequests.KeyPair(agent.Kid, agent.AssertionKey);
        var retire = (Url: KeyActRequests.RetireUrl(agent.AgentId, agent.Kid), Body: KeyActRequests.RetirementBody(agent.AgentId, enrolled, prev),
            Detail: "key retirement is not enabled on this Forum");

        foreach (var route in new[] { rotate, retire })
        {
            var eventsBefore = await EventCountAsync(ct);
            var keysBefore = await KeyRowsAsync(agent.AgentId, ct);
            var proof = dpop.Proof("POST", route.Url, forum.Now, token);

            using var response = await SendOnceAsync(client, route.Url, route.Body, token, proof, ct);

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            var (type, detail, _) = await KeyActRequests.ProblemOfAsync(response, ct);
            Assert.Equal("curia/keys/lifecycle-not-enabled", type);
            Assert.Equal(route.Detail, detail);

            // Nothing was consumed: the proof's jti is still absent from the replay cache (a first insert succeeds).
            var jti = JsonDocument.Parse(System.Buffers.Text.Base64Url.DecodeFromChars(proof.Split('.')[1])).RootElement.GetProperty("jti").GetString()!;
            var cache = forum.Services.GetRequiredService<IReplayCache>();
            var inserted = await cache.TryInsertAsync(KeyActRequests.Require(Jti.Parse(jti)), forum.Now.AddMinutes(5), ct);
            Assert.True(inserted.TryGetValue(out var wasAbsent, out _) && wasAbsent, "the refused request consumed the proof's jti");

            // Nothing was written: no key row, no log entry.
            Assert.Equal(keysBefore, await KeyRowsAsync(agent.AgentId, ct));
            Assert.Equal(eventsBefore, await EventCountAsync(ct));
        }
    }

    /// <summary>CP-A's key-set half: the guarded Forum's key set states no kind approved or enabled.</summary>
    [Fact]
    public async Task R4_38_TheKeySetStatesNoKindApprovedOrEnabled()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var agent = ForumAgent.Create(KeyActRequests.Unique("guarded-kinds"), "gk-" + Guid.NewGuid().ToString("N")[..8]);
        using var enrolled = await agent.EnrollAsync(client, ct);
        Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        using var keySet = await KeyActRequests.KeySetAsync(client, agent.AgentId, ct);

        Assert.Equal("""{"approved":[],"enabled":[]}""", keySet.RootElement.GetProperty("curia_lifecycle_kinds").GetRawText());
    }

    /// <summary>One request with the proof as given, sent without the nonce retry the client adds, so the Forum sees exactly this proof once.</summary>
    private static async Task<HttpResponseMessage> SendOnceAsync(
        HttpClient client, string url, byte[] body, string token, string proof, CancellationToken ct)
    {
        using var content = new ByteArrayContent(body);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(url).PathAndQuery) { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("DPoP", token);
        request.Headers.Add("DPoP", proof);

        // Read the body before the request is disposed, as DpopClient does: an unbuffered response outlives its request.
        using var sent = await client.SendAsync(request, ct);
        return new HttpResponseMessage(sent.StatusCode) { Content = new StringContent(await sent.Content.ReadAsStringAsync(ct)) };
    }

    private async Task<int> EventCountAsync(CancellationToken ct)
    {
        var read = await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(ct);
        Assert.True(read.TryGetValue(out var all, out _));
        return all!.Count;
    }

    private async Task<long> KeyRowsAsync(string agentId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM agent_keys WHERE agent_id = @agent;", connection);
        count.Parameters.AddWithValue("agent", agentId);
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }
}

/// <summary>
/// A fact for every refusal type a key act's use case can return (spec section 4.7; the Task 1.3 defect was a
/// refusal the enrollment route left unmapped): each reachable one over HTTP with its status and type, and
/// the rest, which no request reaches cheaply, by the route's own map.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class KeyRotationRefusalTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private async Task<(ForumAgent Agent, DpopClient Dpop, string Token, KeyActRequests.KeyPair Enrolled)> EnrolledAsync(
        HttpClient client, CancellationToken ct)
    {
        var stem = "refusal";
        var agent = ForumAgent.Create(KeyActRequests.Unique(stem), stem + "-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        return (agent, dpop, token, new KeyActRequests.KeyPair(agent.Kid, agent.AssertionKey));
    }

    private static string NewKid() => "k-" + Guid.NewGuid().ToString("N")[..10];

    private async Task<HttpResponseMessage> RotateAsync(
        HttpClient client, DpopClient dpop, string token, string agentId, byte[] body, CancellationToken ct) =>
        await dpop.PostAsync(client, KeyActRequests.RotateUrl(agentId), token, body, forum.Now, ct, contentType: "application/json");

    private async Task<HttpResponseMessage> RetireAsync(
        HttpClient client, DpopClient dpop, string token, string agentId, string kid, byte[] body, CancellationToken ct) =>
        await dpop.PostAsync(client, KeyActRequests.RetireUrl(agentId, kid), token, body, forum.Now, ct, contentType: "application/json");

    /// <summary>A well-formed rotation body, with the statement's members and the signatures' typs and keys as the case needs them.</summary>
    private static byte[] RotationBodyOf(
        string statement, KeyActRequests.KeyPair authorizer, KeyActRequests.KeyPair possessor,
        string authorizationTyp = KeyStatements.RotationHeaderTyp)
    {
        return KeyActRequests.Body(
            ("statement", statement),
            ("authorization", KeyActRequests.Sign(statement, authorizationTyp, authorizer.Signing)),
            ("possession", KeyActRequests.Sign(statement, KeyStatements.RotationHeaderTyp, possessor.Signing)));
    }

    private static async Task AssertRefusedAsync(HttpResponseMessage response, HttpStatusCode status, string type, CancellationToken ct, string? detail = null)
    {
        var problem = await KeyActRequests.ProblemOfAsync(response, ct);
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(type, problem.Type);
        if (detail is not null) Assert.Equal(detail, problem.Detail);
    }

    [Fact]
    public async Task R4_18_AStatementThatIsNotJsonIsUnreadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, _) = await EnrolledAsync(client, ct);

        using var response = await RotateAsync(client, dpop, token, agent.AgentId,
            KeyActRequests.Body(("statement", "not json"), ("authorization", "x"), ("possession", "y")), ct);

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, "curia/keys/statement-unreadable", ct);
    }

    [Fact]
    public async Task R4_38_AStatementThatIsNotInItsCanonicalFormIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);
        var next = KeyActRequests.KeyPair.New(NewKid());
        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var statement = " " + KeyActRequests.RotationStatement(agent.AgentId, agent.Kid, next, prev);

        using var response = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(statement, enrolled, next), ct);

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, "curia/keys/statement-not-canonical", ct);
    }

    [Fact]
    public async Task R4_18_AStatementNamingAnotherAgentIsAMismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);
        var next = KeyActRequests.KeyPair.New(NewKid());
        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var statement = KeyActRequests.RotationStatement("https://agents.example/someone-else", agent.Kid, next, prev);

        using var response = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(statement, enrolled, next), ct);

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, "curia/keys/statement-mismatch", ct);
    }

    [Fact]
    public async Task R4_18_AJwkWhoseAlgIsNotItsKeyTypesIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);
        var next = KeyActRequests.KeyPair.New(NewKid());
        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var jwk = KeyActRequests.Require(PublicJwk.Of(next.Material));
        var wrongAlg = new JsonValue.Object([.. jwk.Members.Select(m => m.Key == "alg" ? new KeyValuePair<string, JsonValue>("alg", new JsonValue.String("EdDSA")) : m)]);
        var statement = KeyActRequests.Canonical(new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent.AgentId)),
            new("authorized_by", new JsonValue.String(agent.Kid)),
            new("jwk", wrongAlg),
            new("kid", new JsonValue.String(next.Kid)),
            new("prev", new JsonValue.Number(prev)),
            new("typ", new JsonValue.String(KeyStatements.RotationStatementTyp)),
        ]));

        using var response = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(statement, enrolled, next), ct);

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, "curia/keys/jwk-alg-mismatch", ct);
    }

    [Fact]
    public async Task R4_18_AKeyThatAuthorizesItsOwnBindingIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, _) = await EnrolledAsync(client, ct);
        var next = KeyActRequests.KeyPair.New(NewKid());
        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var statement = KeyActRequests.RotationStatement(agent.AgentId, next.Kid, next, prev);

        using var response = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(statement, next, next), ct);

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, "curia/keys/self-authorized", ct);
    }

    [Fact]
    public async Task R4_18_AnAuthorizationUnderAnotherKindsTypIsASignatureFailureNamingTheRole()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);
        var next = KeyActRequests.KeyPair.New(NewKid());
        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var statement = KeyActRequests.RotationStatement(agent.AgentId, agent.Kid, next, prev);

        using var response = await RotateAsync(
            client, dpop, token, agent.AgentId, RotationBodyOf(statement, enrolled, next, KeyStatements.RetirementHeaderTyp), ct);

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, "curia/keys/signature-invalid", ct, detail: "field=authorization");
    }

    [Fact]
    public async Task R4_18_AnAuthorizingKeyThatIsNotBoundIsNotLive()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, _) = await EnrolledAsync(client, ct);
        var ghost = KeyActRequests.KeyPair.New(NewKid());
        var next = KeyActRequests.KeyPair.New(NewKid());
        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var statement = KeyActRequests.RotationStatement(agent.AgentId, ghost.Kid, next, prev);

        using var response = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(statement, ghost, next), ct);

        await AssertRefusedAsync(response, HttpStatusCode.Forbidden, "curia/keys/authorizing-key-not-live", ct);
    }

    [Fact]
    public async Task R4_38_ARotationThatLinksFromAStaleEntryAnswers409WithTheCurrentIndex()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);
        var next = KeyActRequests.KeyPair.New(NewKid());
        var latest = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var statement = KeyActRequests.RotationStatement(agent.AgentId, agent.Kid, next, latest - 1);

        using var response = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(statement, enrolled, next), ct);

        await AssertRefusedAsync(response, HttpStatusCode.Conflict, "curia/keys/lifecycle-moved", ct,
            detail: latest.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>R4.37's refusal of a <c>kid</c> that holds a control character, at the rotation route as at enrollment: 400.</summary>
    [Fact]
    public async Task R4_37_ARotationToAKidHoldingAControlCharacterIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);
        var next = KeyActRequests.KeyPair.New("tab\there-" + Guid.NewGuid().ToString("N")[..6]);
        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var statement = KeyActRequests.RotationStatement(agent.AgentId, agent.Kid, next, prev);

        using var response = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(statement, enrolled, next), ct);

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, "curia/enroll/identifier-control-character", ct);
    }

    /// <summary>R7.23: four live keys at most. The fourth rotation after three is 409.</summary>
    [Fact]
    public async Task R7_23_AFifthLiveKeyIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);

        for (var i = 0; i < 3; i++)
        {
            var key = KeyActRequests.KeyPair.New(NewKid());
            var statement = KeyActRequests.RotationStatement(
                agent.AgentId, agent.Kid, key, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct));
            using var ok = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(statement, enrolled, key), ct);
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
        }

        var fifth = KeyActRequests.KeyPair.New(NewKid());
        var refused = KeyActRequests.RotationStatement(
            agent.AgentId, agent.Kid, fifth, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct));
        using var response = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(refused, enrolled, fifth), ct);

        await AssertRefusedAsync(response, HttpStatusCode.Conflict, "curia/keys/too-many-live-keys", ct, detail: "limit=4");
    }

    /// <summary>R7.23: three rotations in a trailing day. With a slot free again, the fourth is 429.</summary>
    [Fact]
    public async Task R7_23_AFourthRotationInADayIsRefused429()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);
        var last = enrolled;

        for (var i = 0; i < 3; i++)
        {
            var key = KeyActRequests.KeyPair.New(NewKid());
            var statement = KeyActRequests.RotationStatement(
                agent.AgentId, agent.Kid, key, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct));
            using var ok = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(statement, enrolled, key), ct);
            Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
            last = key;
        }

        using (var retired = await RetireAsync(
            client, dpop, token, agent.AgentId, last.Kid,
            KeyActRequests.RetirementBody(agent.AgentId, last, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), ct))
            Assert.Equal(HttpStatusCode.Created, retired.StatusCode);

        var fourth = KeyActRequests.KeyPair.New(NewKid());
        var refused = KeyActRequests.RotationStatement(
            agent.AgentId, agent.Kid, fourth, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct));
        using var response = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(refused, enrolled, fourth), ct);

        await AssertRefusedAsync(response, HttpStatusCode.TooManyRequests, "curia/keys/rotation-budget-exhausted", ct, detail: "limit=3 per 24 hours");
    }

    [Fact]
    public async Task R4_39_RetiringTheLastLiveKeyIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);

        using var response = await RetireAsync(
            client, dpop, token, agent.AgentId, agent.Kid,
            KeyActRequests.RetirementBody(agent.AgentId, enrolled, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), ct);

        await AssertRefusedAsync(response, HttpStatusCode.Conflict, "curia/keys/last-live-key", ct);
    }

    [Fact]
    public async Task R4_39_RetiringAKeyTwiceIsRefusedAsNotLive()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);
        var second = KeyActRequests.KeyPair.New(NewKid());
        var statement = KeyActRequests.RotationStatement(
            agent.AgentId, agent.Kid, second, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct));
        using (var rotated = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(statement, enrolled, second), ct))
            Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);

        using (var first = await RetireAsync(
            client, dpop, token, agent.AgentId, second.Kid,
            KeyActRequests.RetirementBody(agent.AgentId, second, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), ct))
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var again = await RetireAsync(
            client, dpop, token, agent.AgentId, second.Kid,
            KeyActRequests.RetirementBody(agent.AgentId, second, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), ct);

        await AssertRefusedAsync(again, HttpStatusCode.Conflict, "curia/keys/not-live", ct);
    }

    [Fact]
    public async Task R4_39_RetiringAKeyTheIdentityNeverBoundIs404()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, _) = await EnrolledAsync(client, ct);
        var ghost = KeyActRequests.KeyPair.New(NewKid());

        using var response = await RetireAsync(
            client, dpop, token, agent.AgentId, ghost.Kid,
            KeyActRequests.RetirementBody(agent.AgentId, ghost, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), ct);

        await AssertRefusedAsync(response, HttpStatusCode.NotFound, "curia/keys/unknown-key", ct);
    }

    [Fact]
    public async Task R4_39_ARetirementThatLinksFromAStaleEntryAnswers409()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);
        var latest = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);

        using var response = await RetireAsync(
            client, dpop, token, agent.AgentId, agent.Kid, KeyActRequests.RetirementBody(agent.AgentId, enrolled, latest - 1), ct);

        await AssertRefusedAsync(response, HttpStatusCode.Conflict, "curia/keys/lifecycle-moved", ct,
            detail: latest.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task R4_39_ARetirementWhoseStatementNamesAnotherKeyThanTheRouteIsAMismatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);
        var other = KeyActRequests.KeyPair.New(NewKid());

        using var response = await RetireAsync(
            client, dpop, token, agent.AgentId, other.Kid,
            KeyActRequests.RetirementBody(agent.AgentId, enrolled, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct)), ct);

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, "curia/keys/statement-mismatch", ct);
    }

    /// <summary>R4.39: a sibling key cannot retire another. The retirement is signed by the other live key, under the right typ.</summary>
    [Fact]
    public async Task R4_39_ASiblingKeyCannotRetireAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, enrolled) = await EnrolledAsync(client, ct);
        var second = KeyActRequests.KeyPair.New(NewKid());
        var statement = KeyActRequests.RotationStatement(
            agent.AgentId, agent.Kid, second, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct));
        using (var rotated = await RotateAsync(client, dpop, token, agent.AgentId, RotationBodyOf(statement, enrolled, second), ct))
            Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);

        var retirement = KeyActRequests.RetirementStatement(agent.AgentId, second.Kid, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct));
        var body = KeyActRequests.Body(
            ("statement", retirement),
            ("authorization", KeyActRequests.Sign(retirement, KeyStatements.RetirementHeaderTyp, enrolled.Signing)));
        using var response = await RetireAsync(client, dpop, token, agent.AgentId, second.Kid, body, ct);

        await AssertRefusedAsync(response, HttpStatusCode.BadRequest, "curia/keys/signature-invalid", ct, detail: "field=authorization");
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The response is disposed by the using declaration.")]
    public async Task R11_34_ABodyThatIsNotTheRoutesObjectIsUnreadable()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token, _) = await EnrolledAsync(client, ct);

        foreach (var body in new[] { "[]", "{}", """{"statement":1,"authorization":"a","possession":"p"}""", """{"statement":"s","authorization":"a","possession":"p","extra":"e"}""" })
        {
            using var response = await RotateAsync(client, dpop, token, agent.AgentId, Encoding.UTF8.GetBytes(body), ct);

            await AssertRefusedAsync(response, HttpStatusCode.BadRequest, "curia/request/unreadable", ct);
        }
    }

    /// <summary>
    /// Every refusal the two use cases can return has a status in the routes' map other than 500, the three
    /// the routes raise themselves aside. The ceiling and the unreadable log, which no request reaches
    /// cheaply, are held here; the rest are held over HTTP above.
    /// </summary>
    [Fact]
    public void R4_18_EveryRefusalTheUseCasesReturnIsMappedByTheRoutes()
    {
        var map = typeof(ForumEndpoints).GetMethod("StatusForKeyAct", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        int Status(string type) => (int)map.Invoke(null, [new Error(type, "t")])!;

        var raisedByTheRoutes = new HashSet<string>(StringComparer.Ordinal)
        {
            Curia.Application.Credentials.KeyActErrors.NotOwnType,
            Curia.Application.Credentials.KeyActErrors.LifecycleNotEnabledType,
        };

        var types = new[] { typeof(Curia.Application.Credentials.KeyActErrors), typeof(KeyStatementErrors) }
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            .Where(f => f.IsLiteral && f.Name.EndsWith("Type", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Concat([
                Curia.Application.Ports.AuthorKeyErrors.KidTakenType,
                Curia.Application.Credentials.LogBoundKeys.LogUnreadableType,
                Curia.Application.Credentials.EnrollmentErrors.IdentifierControlCharacterType,
            ])
            .ToList();

        Assert.True(types.Count >= 15, "the reflection found too few refusal types to be a check");
        foreach (var type in types.Where(t => !raisedByTheRoutes.Contains(t)))
            Assert.True(Status(type) != 500, $"{type} answers 500");

        Assert.Equal(409, Status(Curia.Application.Credentials.KeyActErrors.RotationCeilingReachedType));
        Assert.Equal(503, Status(Curia.Application.Credentials.LogBoundKeys.LogUnreadableType));
        Assert.Equal(500, Status("curia/keys/something-unlisted"));
    }
}
