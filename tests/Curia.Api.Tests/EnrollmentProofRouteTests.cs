using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Curia.Domain.Primitives;
using Xunit;
using static Curia.Api.Tests.EnrollProofProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R4.46, R4.47 and R4.31 (add.) (errata G79) over the real Forum, with proofs built by the test project's own builder from the errata's sentence. <b>Written before the route is changed</b>: at the
/// Task 2 tree <c>POST /v1/agents</c> reads no proof and <c>GET /v1/enroll/nonce</c> does not exist, so every probe whose verdict is a refusal is red by its own assertion, and the controls (a valid
/// proof is accepted) and the pins (EPP13, EPP23's pin) are green. Each refusal is held to its <b>type</b>, never to <c>400</c> alone (a <c>400</c> also answers a text check), and is paired with
/// the valid control in the same method.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class EnrollmentProofRouteTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HttpClient Client => forum.Client;

    /// <summary>
    /// EPP5, in the shape of <c>R4_18_EveryRefusalTheUseCasesReturnIsMappedByTheRoutes</c>: the validator's refusal types are exactly seven, every one answers <b>exactly</b> <c>400</c> through the route's map
    /// (<c>StatusForEnrollmentProof</c>), and a type the map does not list is a fault of the Forum, <c>500</c>. A map the route never calls is the 404 test an unmapped route also satisfied; EPP9b drives each type over HTTP.
    /// </summary>
    [Fact]
    public void R4_46_EPP5_EveryRefusalTypeOfTheProofIsMappedTo400()
    {
        var errors = typeof(Curia.AuthN.EnrollmentProofValidator).Assembly.GetType("Curia.AuthN.EnrollmentProofErrors");
        Assert.True(errors is not null, "Curia.AuthN.EnrollmentProofErrors does not exist: the seven refusal types of R4.46 are not defined");

        var types = errors!.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Type", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.Equal(7, types.Count);

        var map = typeof(ForumEndpoints).GetMethod("StatusForEnrollmentProof", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(map is not null, "ForumEndpoints.StatusForEnrollmentProof does not exist: the route has no status map for the proof's refusals");
        int Status(string type) => (int)map!.Invoke(null, [new Error(type, "t")])!;

        foreach (var type in types) Assert.True(Status(type) == 400, $"{type} answers {Status(type)}, not 400");
        Assert.Equal(500, Status("curia/enroll/proof-unlisted"));
    }

    /// <summary>
    /// EPP6, R4.31 (add.), a repeat. The control first: an enrolled identity re-announcing with a proof its key signed answers <c>201</c> and registers nothing (the log and the key rows as they were). Then
    /// the refusals: no proof, <c>proof-required</c>; a proof signed by another key presenting the same bytes, <c>proof-not-possessed</c>.
    /// </summary>
    [Fact]
    public async Task R4_31_EPP6_ARepeatCarriesTheProofAndOnlyTheHolderMakesIt()
    {
        var victim = NewAgent("epp6");
        using (var first = await victim.EnrollAsync(Client, Ct)) Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var events = await EventsAsync(forum, Ct);
        var rows = await KeyRowsAsync(forum, victim.AgentId, Ct);

        var control = await PostValidAsync(Client, victim, forum.Now, Ct);
        Assert.True(control.Status == HttpStatusCode.Created, $"the holder's repeat with a valid proof was refused, so nothing below means anything: {control.Says}");
        Assert.Equal(events, await EventsAsync(forum, Ct));
        Assert.Equal(rows, await KeyRowsAsync(forum, victim.AgentId, Ct));

        AssertRefused(await PostAsync(Client, victim, null, Ct), "proof-required");

        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = Proof(victim, Audience(Client), await NonceAsync(Client, Ct), forum.Now, signer: stranger);
        AssertRefused(await PostAsync(Client, victim, forged, Ct), "proof-not-possessed");
        Assert.Equal(events, await EventsAsync(forum, Ct));
    }

    /// <summary>
    /// EPP7, R4.31 (add.), a restoration, over the lost-row scene of <c>EnrollmentBindingTests</c>: the key row is lost, the log binds the key. A presenter with the bound <b>public</b> bytes and a proof by
    /// another key is refused <c>proof-not-possessed</c> and the store gains no row; the holder, with a proof, restores it.
    /// </summary>
    [Fact]
    public async Task R4_31_EPP7_ALostRowIsRestoredOnlyForTheHolderOfTheKey()
    {
        var victim = NewAgent("epp7");
        using (var first = await victim.EnrollAsync(Client, Ct)) Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        await using (var admin = new Npgsql.NpgsqlConnection(forum.ConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var lose = new Npgsql.NpgsqlCommand("DELETE FROM agent_keys WHERE agent_id = @agent;", admin);
            lose.Parameters.AddWithValue("agent", victim.AgentId);
            Assert.Equal(1, await lose.ExecuteNonQueryAsync(Ct));
        }

        using var presenter = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var forged = Proof(victim, Audience(Client), await NonceAsync(Client, Ct), forum.Now, signer: presenter);
        AssertRefused(await PostAsync(Client, victim, forged, Ct), "proof-not-possessed");
        Assert.Equal(0, await KeyRowsAsync(forum, victim.AgentId, Ct));

        var restored = await PostValidAsync(Client, victim, forum.Now, Ct);
        Assert.True(restored.Status == HttpStatusCode.Created, $"the holder's restoration was refused: {restored.Says}");
        Assert.Equal(1, await KeyRowsAsync(forum, victim.AgentId, Ct));
    }

    /// <summary>
    /// EPP8, the squat: another identity's public key (read from <c>GET /v1/jwks?agent=</c>) enrolled under a new identifier with a proof signed by the sender's own key is refused
    /// <c>proof-not-possessed</c>; nothing is registered and nothing appended. The same sender with its own key is the control.
    /// </summary>
    [Fact]
    public async Task R4_46_EPP8_AnotherIdentitysPublicKeyCannotBeEnrolledWithoutItsPrivateHalf()
    {
        var owner = NewAgent("epp8-owner");
        using (var first = await owner.EnrollAsync(Client, Ct)) Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var served = await JwksAsync(Client, owner.AgentId, Ct);
        Assert.True(served is not null, "the owner's key set is not served");

        var sender = NewAgent("epp8-sender");
        using var senders = sender.AssertionKey;
        var events = await EventsAsync(forum, Ct);
        var forged = Proof(sender, Audience(Client), await NonceAsync(Client, Ct), forum.Now, signer: senders, publicKey: Convert.FromBase64String(owner.PublicKeyBase64));
        var refused = await PostAsync(Client, sender, forged, Ct, publicKey: owner.PublicKeyBase64);

        AssertRefused(refused, "proof-not-possessed");
        Assert.Equal(events, await EventsAsync(forum, Ct));
        Assert.Null(await JwksAsync(Client, sender.AgentId, Ct));

        var own = await PostValidAsync(Client, sender, forum.Now, Ct);
        Assert.True(own.Status == HttpStatusCode.Created, $"the same sender with its own key was refused: {own.Says}");
    }

    /// <summary>
    /// EPP9, a proof over a different body: one valid proof, five requests that each change one member. Each is refused by its own name, none <c>201</c>, none <c>500</c>; and the valid request
    /// still succeeds after them, because the refusals spent nothing. The <c>alg</c> row is refused by the route's own text check before any proof is read (<c>public-key-not-of-its-algorithm</c>), which is
    /// ruling 5 step 1's order, stated here.
    /// </summary>
    [Fact]
    public async Task R4_46_EPP9_AProofIsForOneRequestAndNoOther()
    {
        var agent = NewAgent("epp9");
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var otherKey = Convert.ToBase64String(other.ExportSubjectPublicKeyInfo());
        var proof = Proof(agent, Audience(Client), await NonceAsync(Client, Ct), forum.Now);

        AssertRefused(await PostAsync(Client, agent, proof, Ct, agentId: agent.AgentId + "-b"), "proof-mismatch", "claim=agent_id");
        AssertRefused(await PostAsync(Client, agent, proof, Ct, kid: agent.Kid + "-b"), "proof-mismatch", "claim=kid");
        var algRefused = await PostAsync(Client, agent, proof, Ct, alg: "EdDSA");
        Assert.True(algRefused.Status == HttpStatusCode.BadRequest && algRefused.Type == "curia/enroll/public-key-not-of-its-algorithm", $"alg row: {algRefused.Says}");
        AssertRefused(await PostAsync(Client, agent, proof, Ct, publicKey: otherKey), "proof-not-possessed");

        var elsewhere = Proof(agent, "http://elsewhere.example/v1/agents", await NonceAsync(Client, Ct), forum.Now);
        AssertRefused(await PostAsync(Client, agent, elsewhere, Ct), "proof-mismatch", "claim=aud");

        var valid = await PostAsync(Client, agent, proof, Ct);
        Assert.True(valid.Status == HttpStatusCode.Created, $"the valid request was refused after the refusals (a refusal spent the proof, or the control is wrong): {valid.Says}");
    }

    /// <summary>
    /// EPP9b: every one of the seven refusal types is reached over HTTP through the real route and answers its own type with <c>400</c>, so a map the route never calls cannot satisfy EPP5.
    /// </summary>
    [Fact]
    public async Task R4_46_EPP9b_EachOfTheSevenTypesIsReachedOverHttpAnswering400()
    {
        var agent = NewAgent("epp9b");
        var audience = Audience(Client);
        var nonce = await NonceAsync(Client, Ct);
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        AssertRefused(await PostAsync(Client, agent, null, Ct), "proof-required");
        AssertRefused(await PostAsync(Client, agent, "not-a-proof", Ct), "proof-malformed");
        AssertRefused(await PostAsync(Client, agent, Proof(agent, audience, nonce, forum.Now, agentId: agent.AgentId + "-b"), Ct), "proof-mismatch");
        AssertRefused(await PostAsync(Client, agent, Proof(agent, audience, nonce, forum.Now, signer: stranger), Ct), "proof-not-possessed");
        AssertRefused(await PostAsync(Client, agent, Proof(agent, audience, nonce, forum.Now - TimeSpan.FromSeconds(60)), Ct), "proof-window");
        AssertRefused(await PostAsync(Client, agent, Proof(agent, audience, "a-nonce-the-forum-never-chose", forum.Now), Ct), "proof-nonce-stale");

        var spent = Proof(agent, audience, nonce, forum.Now);
        var first = await PostAsync(Client, agent, spent, Ct);
        Assert.True(first.Status == HttpStatusCode.Created, $"the proof was refused when first sent: {first.Says}");
        AssertRefused(await PostAsync(Client, agent, spent, Ct), "proof-replayed");
    }

    /// <summary>
    /// EPP10: <c>GET /v1/enroll/nonce</c> answers <c>200</c> anonymously, <c>Cache-Control: no-store</c>, a body of exactly <c>nonce</c> and <c>expires_at</c>, no cookie and no <c>DPoP-Nonce</c> header; a query
    /// string is ignored (R4.47: the route takes no parameter that one could be taken for).
    /// </summary>
    [Fact]
    public async Task R4_47_EPP10_TheNonceRouteIsAnonymousNoStoreAndTakesNoParameter()
    {
        using var response = await Client.GetAsync(new Uri(NonceRoute, UriKind.Relative), Ct);
        var body = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {NonceRoute} answered {(int)response.StatusCode}: {body}");

        Assert.Contains("no-store", response.Headers.CacheControl?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        Assert.False(response.Headers.Contains("DPoP-Nonce"));
        var members = JsonDocument.Parse(body).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["expires_at", "nonce"], members);
        Assert.False(string.IsNullOrEmpty(JsonDocument.Parse(body).RootElement.GetProperty("nonce").GetString()));

        foreach (var query in new[] { "?frobnicate=1", "?epoch=0" })
        {
            using var ignoring = await Client.GetAsync(new Uri(NonceRoute + query, UriKind.Relative), Ct);
            Assert.Equal(HttpStatusCode.OK, ignoring.StatusCode);
            Assert.Equal(members, JsonDocument.Parse(await ignoring.Content.ReadAsStringAsync(Ct)).RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray());
        }
    }

    /// <summary>
    /// EPP13, a characterization and said so: two different agents enrol with proofs carrying the same nonce value and both are accepted, because the nonce is the Forum's epoch value, shared and not
    /// single-use (R4.47); the <c>jti</c> is what is single-use. If the owner chooses per-request nonces this goes red, by design. It is green today by the placeholder and green after by the real value.
    /// </summary>
    [Fact]
    public async Task R4_47_EPP13_TheNonceIsSharedAndAcceptedForTwoAgents()
    {
        var nonce = await NonceAsync(Client, Ct);
        foreach (var stem in new[] { "epp13-a", "epp13-b" })
        {
            var agent = NewAgent(stem);
            var answer = await PostAsync(Client, agent, Proof(agent, Audience(Client), nonce, forum.Now), Ct);
            Assert.True(answer.Status == HttpStatusCode.Created, $"{stem} was refused with the shared nonce: {answer.Says}");
        }
    }

    /// <summary>
    /// EPP19, the key signed with: a proof signed by the profile's <i>other</i> key (the DPoP key, not the registered one) is refused <c>proof-not-possessed</c> by the real Forum; the registered key's
    /// proof is the control. This holds the right key against a real Forum and not only against a double.
    /// </summary>
    [Fact]
    public async Task R4_46_EPP19_AProofSignedByTheOtherKeyOfTheProfileIsNotPossession()
    {
        var agent = NewAgent("epp19");
        using var dpopKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var wrong = Proof(agent, Audience(Client), await NonceAsync(Client, Ct), forum.Now, signer: dpopKey);
        AssertRefused(await PostAsync(Client, agent, wrong, Ct), "proof-not-possessed");

        var right = await PostValidAsync(Client, agent, forum.Now, Ct);
        Assert.True(right.Status == HttpStatusCode.Created, $"the registered key's proof was refused: {right.Says}");
    }

    /// <summary>
    /// EPP23 (Amendment 2, ruling 16), the pin: <b>the Forum honours the Host a request names</b> (register defect D41; non-property (n)). (1) A client assertion and DPoP proof signed for
    /// <c>http://relay.example/oauth/token</c> and sent with that Host answer a token. (2) An enrollment whose proof is made for <c>http://relay.example/v1/agents</c> and sent with <c>Host: relay.example</c>
    /// answers <c>201</c> and the key is in the key set. Both go red the day D41 closes (a configured origin or <c>AllowedHosts</c>): <b>when that happens, flip (1) and (2) to refusals and delete
    /// non-property (n)</b>; the Forum's own origin is then what <c>aud</c> and <c>htu</c> are compared with. The control that makes the pin mean something is its own fact below.
    /// </summary>
    [Fact]
    public async Task D41_TheForumHonoursTheHostARequestNames()
    {
        var token = NewAgent("epp23-token");
        using (var enrolled = await token.EnrollAsync(Client, Ct)) Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);
        using var relay = forum.CreateDefaultClient(new Uri("http://relay.example"));
        var tokenFromRelay = await DpopClient.For(token, token.AssertionKey).GetTokenAsync(relay, "http://relay.example/oauth/token", forum.Now, Ct);
        Assert.False(string.IsNullOrEmpty(tokenFromRelay), "D41's pin (1) failed: the token endpoint no longer honours the Host a request names; D41 is closed, flip this pin");

        var agent = NewAgent("epp23-enrol");
        var proof = Proof(agent, Audience(relay), await NonceAsync(Client, Ct), forum.Now);
        var answer = await PostAsync(relay, agent, proof, Ct);
        Assert.True(answer.Status == HttpStatusCode.Created, $"D41's pin (2) failed: {answer.Says}; D41 is closed, flip pins (1) and (2) to refusals and delete non-property (n)");
        Assert.NotNull(await JwksAsync(Client, agent.AgentId, Ct));
    }

    /// <summary>
    /// EPP23's control (3): the same shape as the pin's second step, a proof made for the fixture's own origin, sent with <c>Host: relay.example</c>, is refused <c>proof-mismatch</c> <c>claim=aud</c>. It
    /// shows the Host reaches the route and that <c>aud</c> is compared with it; without it the pin would pass if <c>aud</c> were not checked at all. <b>It is red until the route exists</b>: the
    /// unmodified route compares no <c>aud</c>, so it answers <c>201</c>. It is a control, not a pin, and goes green with the route (FEPP5 kills its removal).
    /// </summary>
    [Fact]
    public async Task D41_AProofForTheOwnOriginSentUnderAForeignHostIsRefusedByAud()
    {
        var agent = NewAgent("epp23-control");
        using var relay = forum.CreateDefaultClient(new Uri("http://relay.example"));
        var proof = Proof(agent, Audience(Client), await NonceAsync(Client, Ct), forum.Now);
        var answer = await PostAsync(relay, agent, proof, Ct);
        AssertRefused(answer, "proof-mismatch", "claim=aud");
    }
}
