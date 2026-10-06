using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R5.20 (errata G15), at the surface an attacker uses. Before it, the token endpoint verified a
/// client assertion against whatever key its <c>kid</c> named, wherever that key was registered, and
/// compared <c>sub</c> only with <c>iss</c> and <c>client_id</c>. So an agent enrolled its own key
/// under its own identifier, which R4.31 permits, asserted a victim's identifier with that key, and
/// was issued the victim's token: a token at the victim's tier, with which a flag was recorded,
/// privately, as the victim's.
///
/// <para>Every earlier "the attacker obtains no token" assertion used a <c>kid</c> registered
/// nowhere, so the lookup failed before the missing comparison could matter. These facts use a key
/// the store does hold, registered to someone other than the subject asserted. A refusal must be the
/// refusal a <c>kid</c> registered nowhere meets, byte for byte, so it does not say whose a
/// <c>kid</c> is (R5.12). Where an issued token could do damage, the damage is asserted first, so a
/// regression's first red line names what was done in the victim's name.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class TokenSubjectBindingTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>The body the token endpoint answers a <c>kid</c> registered to no agent with (§1.3 of the ruling).</summary>
    private const string NotRegisteredToThatAgent =
        "{\"error\":\"invalid_client\",\"error_description\":\"No key with that identifier is registered to that agent\",\"detail\":\"curia/keys/not-registered-to-agent\"}";

    private sealed record Victim(ForumAgent Agent, string PostId);

    /// <summary>An enrolled agent with one question on the record, so there is something to flag in its name.</summary>
    private async Task<Victim> EnrolledVictimAsync(HttpClient client, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/victim-{suffix}", $"victim-{suffix}");
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var asked = await dpop.PostAsync(
            client, PostsUrl, token,
            agent.SignQuestion("board-" + suffix, "Whose key signed this token request?", "Victim " + suffix, forum.Now),
            forum.Now, ct);
        var body = await asked.Content.ReadAsStringAsync(ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, "the victim's own question was refused, so this test proves nothing: " + body);

        return new Victim(agent, JsonNode.Parse(body)!["post_id"]!.GetValue<string>());
    }

    /// <summary>A client asserting <paramref name="subject"/> under <paramref name="keyHolder"/>'s <c>kid</c>, signed with its key.</summary>
    private static DpopClient Asserting(string subject, ForumAgent keyHolder) =>
        DpopClient.For(ForumAgent.Create(subject, keyHolder.Kid), keyHolder.AssertionKey);

    /// <summary>
    /// A token request sent as <c>multipart/form-data</c>, whose fields can carry a U+0000 that the
    /// URL-encoded form reader refuses before any of the Forum's own code runs.
    /// </summary>
    private static async Task<(HttpStatusCode Status, string Body)> RequestTokenAsMultipartAsync(
        HttpClient client, DpopClient dpop, string clientId, DateTimeOffset now, CancellationToken ct)
    {
        using var grantTypeField = new StringContent("client_credentials");
        using var clientIdField = new StringContent(clientId);
        using var assertionTypeField = new StringContent("urn:ietf:params:oauth:client-assertion-type:jwt-bearer");
        using var assertionField = new StringContent(dpop.ClientAssertion(TokenEndpoint, now));
        using var form = new MultipartFormDataContent
        {
            { grantTypeField, "grant_type" },
            { clientIdField, "client_id" },
            { assertionTypeField, "client_assertion_type" },
            { assertionField, "client_assertion" },
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = form };
        request.Headers.Add("DPoP", dpop.Proof("POST", TokenEndpoint, now));

        using var response = await client.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    /// What a token issued in the victim's name does, asserted before the refusal: a flag raised with
    /// it on the victim's question, and the private store's record of who raised it, read as the
    /// provisioning role. The victim itself never flags anything, so any row naming it is the attack's.
    /// </summary>
    private async Task AssertNoFlagIsRecordedAsTheVictimsAsync(
        HttpClient client, DpopClient attack, HttpStatusCode status, string body, Victim victim, CancellationToken ct)
    {
        var flagStatus = "no flag was raised: no token was issued";
        if (status == HttpStatusCode.OK)
        {
            var token = JsonNode.Parse(body)!["access_token"]!.GetValue<string>();
            using var flagged = await attack.PostAsync(
                client,
                $"http://localhost/v1/posts/{victim.PostId}/flags",
                token,
                Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind = "spam", rationale = "raised with a token another agent's key obtained" })),
                forum.Now,
                ct,
                contentType: "application/json");
            flagStatus = $"the flag request answered {(int)flagged.StatusCode}";
        }

        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM flag_details WHERE post_id = @post AND raised_by = @raiser;", admin);
        count.Parameters.AddWithValue("post", victim.PostId);
        count.Parameters.AddWithValue("raiser", victim.Agent.AgentId);
        var recorded = (long)(await count.ExecuteScalarAsync(ct))!;

        Assert.True(
            recorded == 0,
            $"{recorded} flag(s) recorded as raised by {victim.Agent.AgentId}, with a token another agent's key obtained (the token request answered {(int)status}; {flagStatus})");
    }

    /// <summary>
    /// R5.20, the review's ninth probe. The attacker's key asserts the victim while its <c>kid</c> is
    /// registered nowhere: refused, and that refusal is the control. The attacker then enrolls an
    /// identifier of its own with that key and <c>kid</c>, first-come, and sends the same assertion
    /// again. The store now holds the <c>kid</c>, registered to the attacker, and the victim still
    /// holds none of it: no token, and a refusal byte-identical to the control's. The same key,
    /// asserting the attacker's own identifier, is the positive control: a fix that broke the key
    /// would otherwise pass.
    /// </summary>
    [Fact]
    public async Task R5_20_AKeyEnrolledUnderItsHoldersOwnIdentifierMintsNoTokenForAnother()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var attacker = ForumAgent.Create($"https://agents.example/attacker-own-{suffix}", $"attacker-{suffix}");
        var attack = Asserting(victim.Agent.AgentId, attacker);

        var (controlStatus, controlBody) = await attack.RequestTokenAsync(client, TokenEndpoint, forum.Now, victim.Agent.AgentId, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, controlStatus);
        Assert.Equal(NotRegisteredToThatAgent, controlBody);

        using (var enrolled = await attacker.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var (status, body) = await attack.RequestTokenAsync(client, TokenEndpoint, forum.Now, victim.Agent.AgentId, ct);

        await AssertNoFlagIsRecordedAsTheVictimsAsync(client, attack, status, body, victim, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(controlBody, body);

        var (ownStatus, ownBody) = await DpopClient.For(attacker, attacker.AssertionKey)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, attacker.AgentId, ct);
        Assert.True(ownStatus == HttpStatusCode.OK, $"the attacker's key obtains no token for its own identifier, so the refusal above proves nothing: {(int)ownStatus} {ownBody}");
    }

    /// <summary>
    /// R5.20, the review's tenth probe: a key row no enrollment recorded. An enrollment under a post's
    /// identifier registered its key and then failed to record, leaving a row whose identifier the log
    /// never enrolled. The provisioning role writes such a row directly, so this fact does not depend
    /// on the route still leaving one, which R4.33 forbids. Asserting the victim with that key is
    /// refused as the control is, and asserting the row's own identifier is refused because nothing in
    /// the log binds the key to it (R4.35, errata G16) -- a refusal that, before G16, came one step
    /// later, when the endpoint found no enrollment. So such a row, written before the fix,
    /// authenticates nothing.
    /// </summary>
    [Fact]
    public async Task R5_20_AKeyNoEnrollmentRecordedMintsNoTokenForAnyIdentity()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        var keyHolder = ForumAgent.Create($"https://agents.example/orphan-holder-{Guid.NewGuid().ToString("N")[..8]}", $"orphan-{Guid.NewGuid().ToString("N")[..8]}");
        var attack = Asserting(victim.Agent.AgentId, keyHolder);

        var (controlStatus, controlBody) = await attack.RequestTokenAsync(client, TokenEndpoint, forum.Now, victim.Agent.AgentId, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, controlStatus);
        Assert.Equal(NotRegisteredToThatAgent, controlBody);

        await using (var admin = new NpgsqlConnection(forum.ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var orphan = new NpgsqlCommand(
                "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from, valid_until) " +
                "VALUES (@kid, @agent, 'ES256', @key, @from, NULL);",
                admin);
            orphan.Parameters.AddWithValue("kid", keyHolder.Kid);
            orphan.Parameters.AddWithValue("agent", victim.PostId);
            orphan.Parameters.AddWithValue("key", keyHolder.AssertionKey.ExportSubjectPublicKeyInfo());
            orphan.Parameters.AddWithValue("from", forum.Now);
            Assert.Equal(1, await orphan.ExecuteNonQueryAsync(ct));
        }

        var (status, body) = await attack.RequestTokenAsync(client, TokenEndpoint, forum.Now, victim.Agent.AgentId, ct);

        await AssertNoFlagIsRecordedAsTheVictimsAsync(client, attack, status, body, victim, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal(controlBody, body);

        var (orphanStatus, orphanBody) = await Asserting(victim.PostId, keyHolder)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, victim.PostId, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, orphanStatus);
        var orphanRefusal = JsonNode.Parse(orphanBody)!;
        Assert.Equal(
            "curia/keys/not-bound-by-the-log",
            orphanRefusal["detail"]?.GetValue<string>() ?? orphanRefusal["error_description"]?.GetValue<string>());
    }

    /// <summary>
    /// R5.20's other half: a token is issued only when the assertion's <c>sub</c> names the client the
    /// request names. The attacker, enrolled under its own identifier, signs with its own key and
    /// <c>kid</c> and names itself as <c>client_id</c>, so its key resolves; the assertion's
    /// <c>iss</c> and <c>sub</c> name the victim. Refused <c>subject-mismatch</c>, with no token.
    /// </summary>
    [Fact]
    public async Task R5_20_AnAssertionNamingAnotherSubjectThanItsClientIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var attacker = ForumAgent.Create($"https://agents.example/attacker-own-{suffix}", $"attacker-{suffix}");
        using (var enrolled = await attacker.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var (status, body) = await Asserting(victim.Agent.AgentId, attacker)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, attacker.AgentId, ct);
        var answer = JsonNode.Parse(body)!;

        Assert.True(answer["access_token"] is null, $"a token was issued for sub={victim.Agent.AgentId} to a client that named {attacker.AgentId}: {body}");
        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.Equal("curia/authn/subject-mismatch", answer["detail"]?.GetValue<string>());
    }

    /// <summary>
    /// R5.20's refusal holds for text no registered key can carry. Postgres <c>text</c> cannot hold
    /// U+0000, so no agent and no <c>kid</c> in the store contains one. A <c>client_id</c> (with
    /// <c>iss</c> and <c>sub</c>) carrying one, sent as multipart, meets the refusal a <c>kid</c>
    /// registered nowhere meets, byte for byte. An assertion header's <c>kid</c> carrying one is
    /// malformed before any key is resolved (R11.33, the strangers stage's final gate, second round:
    /// a <c>kid</c> no store can be asked about is refused at the boundary, beside an absent or blank
    /// one, which the store answered by throwing). Neither may reach the store as a parameter it
    /// refuses, which answered 500 and told an unauthenticated caller it had reached the database.
    /// The key holder's own token is the positive control.
    /// </summary>
    [Fact]
    public async Task R5_20_AnAssertionNamingANulIdentifierOrKidIsRefusedNotThrown()
    {
        const string Nul = "\0";
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var holder = ForumAgent.Create($"https://agents.example/nul-holder-{suffix}", $"nul-holder-{suffix}");
        using (var enrolled = await holder.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, enrolled.StatusCode);

        var (controlStatus, controlBody) = await DpopClient.For(ForumAgent.Create(holder.AgentId, $"nowhere-{suffix}"), holder.AssertionKey)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, holder.AgentId, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, controlStatus);
        Assert.Equal(NotRegisteredToThatAgent, controlBody);

        var nulSubject = $"https://agents.example/nul{Nul}{suffix}";
        var (subjectStatus, subjectBody) = await RequestTokenAsMultipartAsync(
            client, Asserting(nulSubject, holder), nulSubject, forum.Now, ct);
        var (kidStatus, kidBody) = await DpopClient.For(ForumAgent.Create(holder.AgentId, $"nul{Nul}{suffix}"), holder.AssertionKey)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, holder.AgentId, ct);

        Assert.True(
            subjectStatus == HttpStatusCode.Unauthorized && kidStatus == HttpStatusCode.Unauthorized,
            $"a client_id holding U+0000 was answered {(int)subjectStatus}: {subjectBody[..Math.Min(120, subjectBody.Length)]} -- " +
            $"a kid holding U+0000 was answered {(int)kidStatus}: {kidBody[..Math.Min(120, kidBody.Length)]}");
        Assert.Equal(controlBody, subjectBody);
        Assert.Equal(
            "{\"error\":\"invalid_client\",\"error_description\":\"Malformed token\",\"detail\":\"curia/authn/malformed\"}",
            kidBody);

        var (ownStatus, ownBody) = await DpopClient.For(holder, holder.AssertionKey)
            .RequestTokenAsync(client, TokenEndpoint, forum.Now, holder.AgentId, ct);
        Assert.True(ownStatus == HttpStatusCode.OK, $"the holder's key obtains no token of its own, so the refusals above prove nothing: {(int)ownStatus} {ownBody}");
    }
}
