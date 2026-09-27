using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G14, at the surface an attacker uses. Before it, <c>POST /v1/agents</c> would register a
/// new key under any identifier it was sent, and replace the bytes behind any <c>kid</c> it was
/// sent again: anyone who knew an agent's identifier could obtain a token as that agent and post
/// under its name, and anyone who knew its <c>kid</c> could make every post it had signed stop
/// verifying and lock it out. Both identifiers are public -- every post and every JWKS carries them.
///
/// <para>Each fact holds the victim to the Forum's answer and then to what a store holds afterwards:
/// the key set it serves, a token, or the log's record. Two attacks would change the key a verifier
/// reads for the victim -- the overwrite, and the lost row -- and those facts hold the served key to
/// the victim's own. The overwrite fact runs the independent verifier, which shares no code with the
/// store, over a post the victim made before the attack, and its negative control shows the verifier
/// does refuse the overwriter's key, so that pass carries information. The lost-row fact compares the
/// served key set byte for byte with the one served before the loss. The new-kid fact runs the
/// verifier too, but that attack adds a key and leaves the victim's alone, so the verifier would pass
/// had the attack succeeded; the single served key and the attacker's missing token fence it.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class EnrollmentBindingTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private sealed record Victim(ForumAgent Agent, string PostId);

    /// <summary>An enrolled agent with one question on the record, so there is authorship to lose.</summary>
    private async Task<Victim> EnrolledVictimAsync(HttpClient client, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/victim-{suffix}", $"victim-{suffix}");
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var asked = await dpop.PostAsync(
            client, PostsUrl, token,
            agent.SignQuestion("board-" + suffix, "Which canonical form does the log hash?", "Victim " + suffix, forum.Now),
            forum.Now, ct);
        var body = await asked.Content.ReadAsStringAsync(ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, "the victim's own question was refused, so this test proves nothing: " + body);

        return new Victim(agent, JsonNode.Parse(body)!["post_id"]!.GetValue<string>());
    }

    private static async Task<JsonElement> JwksAsync(HttpClient client, string agentId, CancellationToken ct) =>
        await client.GetFromJsonAsync<JsonElement>($"/v1/jwks?agent={Uri.EscapeDataString(agentId)}", ct);

    private static JsonObject JwkOf(ForumAgent agent)
    {
        var q = agent.AssertionKey.ExportParameters(includePrivateParameters: false).Q;
        return new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["alg"] = "ES256",
            ["kid"] = agent.Kid,
            ["x"] = System.Buffers.Text.Base64Url.EncodeToString(q.X!),
            ["y"] = System.Buffers.Text.Base64Url.EncodeToString(q.Y!),
        };
    }

    /// <summary>The served key set holds exactly the victim's key, coordinate for coordinate.</summary>
    private static void AssertServesOnlyTheVictimsKey(JsonElement jwks, ForumAgent victim)
    {
        var served = Assert.Single(jwks.GetProperty("keys").EnumerateArray());
        var expected = JwkOf(victim);

        Assert.Equal(victim.Kid, served.GetProperty("kid").GetString());
        Assert.Equal(expected["x"]!.GetValue<string>(), served.GetProperty("x").GetString());
        Assert.Equal(expected["y"]!.GetValue<string>(), served.GetProperty("y").GetString());
    }

    /// <summary>Runs <c>curia-testis</c> over a served post and a key set; returns its exit code and output.</summary>
    private static async Task<(int Exit, string Output)> TestisAsync(HttpClient client, string postId, string jwks, CancellationToken ct)
    {
        var served = await client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        var submission = $"{{\"envelope\":{served.GetProperty("canonical").GetString()},\"signature\":\"{served.GetProperty("signature").GetString()}\"}}";

        var directory = Directory.CreateTempSubdirectory("curia-g14-");
        try
        {
            var envelopePath = Path.Combine(directory.FullName, "submission.json");
            var jwksPath = Path.Combine(directory.FullName, "jwks.json");
            await File.WriteAllTextAsync(envelopePath, submission, ct);
            await File.WriteAllTextAsync(jwksPath, jwks, ct);

            var (exit, stdout, stderr) = TestisBinary.Run(TestisBinary.Locate(), envelopePath, jwksPath);
            return (exit, stdout + stderr);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<string?> TokenOrNullAsync(HttpClient client, ForumAgent agent, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            return await DpopClient.For(agent, agent.AssertionKey).GetTokenAsync(client, TokenEndpoint, now, ct);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>A refusal's <c>type</c> and <c>detail</c>, as the Forum served them.</summary>
    private static async Task<(string? Type, string? Detail)> ProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return (problem["type"]?.GetValue<string>(), problem["detail"]?.GetValue<string>());
    }

    /// <summary>
    /// The agent's key row lost from the store: deleted by the provisioning role, as a restore from a
    /// backup older than the enrollment would lose it. The application role cannot delete one.
    /// </summary>
    private async Task LoseKeyRowAsync(string agentId, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var lose = new NpgsqlCommand("DELETE FROM agent_keys WHERE agent_id = @agent;", admin);
        lose.Parameters.AddWithValue("agent", agentId);
        Assert.Equal(1, await lose.ExecuteNonQueryAsync(ct));
    }

    /// <summary>The <c>agent.enrolled</c> events in the agent's own stream, counted as the provisioning role.</summary>
    private async Task<long> EnrollmentsRecordedAsync(string agentId, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE aggregate_id = @agent AND event_type = 'agent.enrolled';", admin);
        count.Parameters.AddWithValue("agent", agentId);
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// R4.31: an enrolled identifier sent a second key under a new <c>kid</c> registers nothing. The
    /// attacker gets a refusal naming what happened and what to do, cannot obtain a token as the
    /// victim, and the victim's key set is unchanged -- so the victim's question still verifies
    /// offline. The victim's own token is the control: a token helper that always failed would pass
    /// the attacker's line above it.
    /// </summary>
    [Fact]
    public async Task R4_31_EnrollingAnEnrolledIdentityWithANewKeyRegistersNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        var attacker = ForumAgent.Create(victim.Agent.AgentId, "attacker-" + Guid.NewGuid().ToString("N")[..8]);
        using var enrolled = await attacker.EnrollAsync(client, ct);
        var (type, detail) = await ProblemAsync(enrolled, ct);

        Assert.Equal(HttpStatusCode.Conflict, enrolled.StatusCode);
        Assert.Equal("curia/enroll/already-enrolled", type);

        // Spec Decision 9: the detail names the identifier and the remedy. Before this stage the
        // endpoint served the bare kid here, which tells an honest agent nothing it can act on.
        Assert.StartsWith($"agent={victim.Agent.AgentId}: nothing was registered.", detail, StringComparison.Ordinal);
        Assert.Contains("an agent identifier of its own", detail, StringComparison.Ordinal);

        Assert.Null(await TokenOrNullAsync(client, attacker, forum.Now, ct));
        Assert.NotNull(await TokenOrNullAsync(client, victim.Agent, forum.Now, ct));

        var jwks = await JwksAsync(client, victim.Agent.AgentId, ct);
        AssertServesOnlyTheVictimsKey(jwks, victim.Agent);

        var (exit, output) = await TestisAsync(client, victim.PostId, jwks.GetRawText(), ct);
        Assert.True(exit == 0, $"curia-testis no longer verifies the victim's question: exit={exit}\n{output}");
        Assert.Contains(victim.Agent.AgentId, output, StringComparison.Ordinal);
    }

    /// <summary>
    /// R4.32: the victim's own <c>kid</c>, sent again with other bytes, replaces nothing. The victim
    /// still authenticates, the served coordinates are still its own, and its question still
    /// verifies offline -- while the same question against a key set carrying the overwriter's bytes
    /// under that <c>kid</c> does not, which is the negative control that makes the pass mean something.
    /// </summary>
    [Fact]
    public async Task R4_32_ReEnrollingAKidWithOtherBytesReplacesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        var overwriter = ForumAgent.Create(victim.Agent.AgentId, victim.Agent.Kid);
        using var enrolled = await overwriter.EnrollAsync(client, ct);
        var (type, detail) = await ProblemAsync(enrolled, ct);

        Assert.Equal(HttpStatusCode.Conflict, enrolled.StatusCode);
        Assert.Equal("curia/keys/material-immutable", type);
        Assert.StartsWith($"kid={victim.Agent.Kid}: nothing was registered.", detail, StringComparison.Ordinal);

        var jwks = await JwksAsync(client, victim.Agent.AgentId, ct);
        AssertServesOnlyTheVictimsKey(jwks, victim.Agent);
        Assert.NotNull(await TokenOrNullAsync(client, victim.Agent, forum.Now, ct));

        var (exit, output) = await TestisAsync(client, victim.PostId, jwks.GetRawText(), ct);
        Assert.True(exit == 0, $"curia-testis no longer verifies the victim's question: exit={exit}\n{output}");
        Assert.Contains(victim.Agent.AgentId, output, StringComparison.Ordinal);

        // The served key set with only the coordinates changed to the overwriter's: the one
        // difference a successful overwrite would have made. Exit 1 is "verification failed"; a usage
        // error (2) or "could not be checked" (3) would be a control failing for the wrong reason.
        var substituted = JsonNode.Parse(jwks.GetRawText())!;
        var forged = JwkOf(overwriter);
        substituted["keys"]![0]!["x"] = forged["x"]!.GetValue<string>();
        substituted["keys"]![0]!["y"] = forged["y"]!.GetValue<string>();

        var (controlExit, controlOutput) = await TestisAsync(client, victim.PostId, substituted.ToJsonString(), ct);
        Assert.True(controlExit == 1, $"curia-testis did not refuse the victim's question under the overwriter's key (exit={controlExit}) -- the check above cannot fail:\n{controlOutput}");
    }

    /// <summary>
    /// A <c>kid</c> another identity holds is refused to a fresh identifier, and the refusal names both
    /// in the form spec Decision 9 fixes, <c>agent=… kid=…</c>; the Forum used to serve the bare
    /// <c>kid</c>. The fresh identifier holds no key afterwards, and the holder's key set is unchanged.
    /// </summary>
    [Fact]
    public async Task R4_31_AKidAnotherIdentityHoldsIsRefusedNamingBoth()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        var newcomer = ForumAgent.Create("https://agents.example/newcomer-" + Guid.NewGuid().ToString("N")[..8], victim.Agent.Kid);
        using var enrolled = await newcomer.EnrollAsync(client, ct);
        var (type, detail) = await ProblemAsync(enrolled, ct);

        Assert.Equal(HttpStatusCode.Conflict, enrolled.StatusCode);
        Assert.Equal("curia/enroll/kid-already-registered", type);
        Assert.Equal($"agent={newcomer.AgentId} kid={victim.Agent.Kid}", detail);

        using var newcomersKeys = await client.GetAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(newcomer.AgentId)}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NotFound, newcomersKeys.StatusCode);
        AssertServesOnlyTheVictimsKey(await JwksAsync(client, victim.Agent.AgentId, ct), victim.Agent);
    }

    /// <summary>
    /// R4.31's log half at the surface. The victim's key row is lost from the store -- deleted by the
    /// provisioning role, as a restore from a backup older than the enrollment would lose it; the
    /// application role cannot delete one. An attacker's new <c>kid</c> is still refused, because
    /// the log's enrollment names the victim's. The victim, re-presenting its own key an hour later,
    /// is registered again under R4.31's one exception, dated from the enrollment, so the question
    /// the victim asked before the loss is still inside its key's window (R6.31). Under this fixture's
    /// one clock the lost row was dated from that same instant, so the Forum serves the key set it
    /// served before the loss, window and all. On a real clock the recovered window can start later,
    /// by the moments between the lost row's insert and the log's append; no admitted post is stamped
    /// inside them, because a post is admitted only once the log holds the enrollment.
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentityWhoseKeyRowWasLostIsStillBoundByItsEnrollment()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);
        var before = await JwksAsync(client, victim.Agent.AgentId, ct);
        AssertServesOnlyTheVictimsKey(before, victim.Agent);

        await LoseKeyRowAsync(victim.Agent.AgentId, ct);

        // An hour on, so a key re-registered from "now" would serve a later window than the one lost.
        forum.Clock.Advance(TimeSpan.FromHours(1));

        var attacker = ForumAgent.Create(victim.Agent.AgentId, "attacker-" + Guid.NewGuid().ToString("N")[..8]);
        using (var enrolled = await attacker.EnrollAsync(client, ct))
        {
            Assert.Equal(HttpStatusCode.Conflict, enrolled.StatusCode);
            Assert.Contains("curia/enroll/already-enrolled", await enrolled.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
        }

        Assert.Null(await TokenOrNullAsync(client, attacker, forum.Now, ct));

        using (var recovered = await victim.Agent.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);

        Assert.Equal(before.GetRawText(), (await JwksAsync(client, victim.Agent.AgentId, ct)).GetRawText());
        Assert.NotNull(await TokenOrNullAsync(client, victim.Agent, forum.Now, ct));
    }

    /// <summary>
    /// R4.31 rev. (errata G16), the residual errata G14's fourth cost named. The victim's key row is
    /// lost, and a request presents the victim's own <c>kid</c> with other bytes. Before G16 it was
    /// registered, dated from the victim's enrollment, and its sender held the identity: a token, and
    /// every later post. The log now carries the victim's key (R4.34), so the request is refused by
    /// name and registers nothing, and the victim, re-presenting its own key, is served the key set it
    /// was served before the loss, byte for byte.
    /// </summary>
    [Fact]
    public async Task R4_31_ALostRowsKidPresentedWithOtherBytesIsRefusedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);
        var before = await JwksAsync(client, victim.Agent.AgentId, ct);

        await LoseKeyRowAsync(victim.Agent.AgentId, ct);

        var impostor = ForumAgent.Create(victim.Agent.AgentId, victim.Agent.Kid);
        using var refused = await impostor.EnrollAsync(client, ct);
        var (type, detail) = await ProblemAsync(refused, ct);
        var impostorToken = await TokenOrNullAsync(client, impostor, forum.Now, ct);

        Assert.True(
            refused.StatusCode == HttpStatusCode.Conflict && impostorToken is null,
            $"other bytes under {victim.Agent.Kid} were answered {(int)refused.StatusCode}, and their holder {(impostorToken is null ? "obtained no token" : "obtained the victim's token")}");
        Assert.Equal("curia/keys/material-immutable", type);
        Assert.Equal($"kid={victim.Agent.Kid}: nothing was registered. The key registered under a kid never changes (R4.32).", detail);

        using (var recovered = await victim.Agent.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);

        Assert.Equal(before.GetRawText(), (await JwksAsync(client, victim.Agent.AgentId, ct)).GetRawText());
    }

    /// <summary>
    /// R4.31 rev. (errata G16, as its review amended it), at the surface. An identifier the log never
    /// enrolled -- as every one enrolled before <c>agent.enrolled</c> existed is -- whose key store
    /// holds its own key and a second one beside it, as errata G14's hole wrote them. Before this
    /// clause, a request presenting the second key's public half enrolled the identifier and bound that
    /// key: its holder held the identity from then on, and the identity's own key was refused. The
    /// request is now refused by name, whichever key it presents, and nothing is recorded. The damage
    /// is asserted first. An identifier holding one such row is the control: the same request enrolls
    /// it, which is how an enrollment whose log append failed recovers.
    /// </summary>
    [Fact]
    public async Task R4_31_AnIdentifierTheLogNeverEnrolledIsNotBoundByWhicheverOfItsKeysIsPresented()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var own = ForumAgent.Create($"https://agents.example/never-enrolled-{suffix}", $"own-{suffix}");
        var hole = ForumAgent.Create(own.AgentId, $"hole-{suffix}");
        await WriteKeyRowAsync(own, ct);
        await WriteKeyRowAsync(hole, ct);

        using var holePresented = await hole.EnrollAsync(client, ct);
        using var ownPresented = await own.EnrollAsync(client, ct);
        var (type, _) = await ProblemAsync(holePresented, ct);

        Assert.True(
            holePresented.StatusCode == HttpStatusCode.Conflict,
            $"presenting the second stored key of {own.AgentId} was answered {(int)holePresented.StatusCode}, and the identity's own key then {(int)ownPresented.StatusCode}");
        Assert.Equal("curia/enroll/keys-ambiguous", type);
        Assert.Equal("409 curia/enroll/keys-ambiguous", $"{(int)ownPresented.StatusCode} {(await ProblemAsync(ownPresented, ct)).Type}");
        Assert.Equal(0, await EnrollmentsRecordedAsync(own.AgentId, ct));

        var single = ForumAgent.Create($"https://agents.example/append-failed-{suffix}", $"single-{suffix}");
        await WriteKeyRowAsync(single, ct);
        using (var recovered = await single.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, recovered.StatusCode);
        Assert.Equal(1, await EnrollmentsRecordedAsync(single.AgentId, ct));
    }

    /// <summary>
    /// A key row holding <paramref name="agent"/>'s key under its identifier, written as the
    /// provisioning role: what errata G14's hole wrote beside an identity's own, or what a store's
    /// write leaves when the log's append after it fails.
    /// </summary>
    private async Task WriteKeyRowAsync(ForumAgent agent, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from, valid_until) " +
            "VALUES (@kid, @agent, 'ES256', @key, @from, NULL);",
            admin);
        insert.Parameters.AddWithValue("kid", agent.Kid);
        insert.Parameters.AddWithValue("agent", agent.AgentId);
        insert.Parameters.AddWithValue("key", agent.AssertionKey.ExportSubjectPublicKeyInfo());
        insert.Parameters.AddWithValue("from", forum.Now);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
    }

    /// <summary>
    /// Where R4.31's one exception stops. The victim's key row is lost, and before the victim
    /// recovers, a fresh identifier enrolls the victim's <c>kid</c>, which the store no longer holds:
    /// that enrollment is registered. The victim, re-presenting the key its enrollment bound, is then
    /// refused by name, <c>curia/enroll/kid-already-registered</c> naming the victim and the
    /// <c>kid</c>, as any <c>kid</c> held elsewhere is. The store must not answer the conflict as the
    /// victim's own key held, which would report an enrollment that registered nothing. The victim's
    /// stream still holds its one <c>agent.enrolled</c>: the refusal appends nothing.
    /// </summary>
    [Fact]
    public async Task R4_31_ALostRowsKidTakenByAnotherIdentityRefusesTheRecoveryByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        await LoseKeyRowAsync(victim.Agent.AgentId, ct);

        var newcomer = ForumAgent.Create("https://agents.example/newcomer-" + Guid.NewGuid().ToString("N")[..8], victim.Agent.Kid);
        using (var taken = await newcomer.EnrollAsync(client, ct))
            Assert.Equal(HttpStatusCode.Created, taken.StatusCode);

        using var recovered = await victim.Agent.EnrollAsync(client, ct);
        var (type, detail) = await ProblemAsync(recovered, ct);

        Assert.Equal(HttpStatusCode.Conflict, recovered.StatusCode);
        Assert.Equal("curia/enroll/kid-already-registered", type);
        Assert.Equal($"agent={victim.Agent.AgentId} kid={victim.Agent.Kid}", detail);
        Assert.Equal(1, await EnrollmentsRecordedAsync(victim.Agent.AgentId, ct));
    }

    /// <summary>
    /// The case R4.31 keeps: an agent re-announcing the key it enrolled. Accepted, the enrollment
    /// instant unmoved, and still exactly one key served.
    /// </summary>
    [Fact]
    public async Task R4_31_ReEnrollingTheEnrolledKeyIsAcceptedAndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await EnrolledVictimAsync(client, ct);

        using var first = await victim.Agent.EnrollAsync(client, ct);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstAt = JsonNode.Parse(await first.Content.ReadAsStringAsync(ct))!["enrolled_at"]!.GetValue<string>();

        forum.Clock.Advance(TimeSpan.FromHours(1));
        using var again = await victim.Agent.EnrollAsync(client, ct);
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
        Assert.Equal(firstAt, JsonNode.Parse(await again.Content.ReadAsStringAsync(ct))!["enrolled_at"]!.GetValue<string>());

        AssertServesOnlyTheVictimsKey(await JwksAsync(client, victim.Agent.AgentId, ct), victim.Agent);
    }
}
