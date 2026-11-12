using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain.Keys;
using Curia.OperatorTool;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using JsonObject = System.Text.Json.Nodes.JsonObject;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G68, R4.40, R5.24, R6.64 (ruling 4: no request is refused because of a row's content), at the surface and as the application role. LRV12 to LRV14: a lifecycle row appended by a plain
/// <c>INSERT</c> under the Forum's own database credential, through no use case, is skipped, reported and inert; it does not stop the agent it names, and it does not make an attacker's key the agent's.
/// Each probe asserts the legs of one summary line, with a control (the same legs on an agent no row names) that holds first, so a probe cannot go red because the fixture refused something else.
/// Red on the seam is the harm: <c>forge-probe.txt</c>'s content in a tracked test.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class ForgedLifecycleRowsTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private string AppRoleConnection() =>
        new NpgsqlConnectionStringBuilder(forum.ConnectionString) { Username = forum.RoleName, Password = forum.RolePassword }.ConnectionString;

    /// <summary>A row appended by SQL under the app role and through no use case: what a holder of the Forum's database credential can do.</summary>
    private async Task InsertRowAsync(string type, string aggregate, string payloadJson, CancellationToken ct)
    {
        var id = new Curia.Domain.Primitives.UlidGenerator(forum.Clock).Next().Match(u => u.ToString(), e => throw new InvalidOperationException(e.Type));
        await using var connection = new NpgsqlConnection(AppRoleConnection());
        await connection.OpenAsync(ct);
        await using var insert = new NpgsqlCommand(
            "INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) VALUES (@id, @type, @aggregate, 'operator:ops', @payload::jsonb, @ts);", connection);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("type", type);
        insert.Parameters.AddWithValue("aggregate", aggregate);
        insert.Parameters.AddWithValue("payload", payloadJson);
        insert.Parameters.AddWithValue("ts", forum.Now);
        Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
    }

    private async Task<long> EventCountAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }

    private sealed record Victim(ForumAgent Agent, DpopClient Dpop, string Token, string PostId, string Board);

    private async Task<Victim> VictimAsync(string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create(KeyActRequests.Unique(stem), $"{stem}-{Suffix()}");
        var (dpop, token) = await agent.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var board = "board-" + Suffix();
        using var posted = await dpop.PostAsync(forum.Client, PostsUrl, token, agent.SignQuestion(board, "Before any row. " + Suffix(), "First " + Suffix(), forum.Now), forum.Now, ct);
        var text = await posted.Content.ReadAsStringAsync(ct);
        Assert.True(posted.StatusCode == HttpStatusCode.Created, text);
        return new Victim(agent, dpop, token, JsonDocument.Parse(text).RootElement.GetProperty("post_id").GetString()!, board);
    }

    private static string ErrorOf(string body)
    {
        try { return JsonDocument.Parse(body).RootElement.TryGetProperty("error", out var e) ? e.GetString()! : "?"; }
        catch (JsonException) { return "?"; }
    }

    /// <summary>
    /// The legs of one victim, as one line: the token request, a post made with the token issued before the row, the post's served key status, the key set (and whether it lists the row), and a rotation by the victim
    /// at the last honoured index. <paramref name="rowIndex"/> is the row's log index, or -1 for the control.
    /// </summary>
    private async Task<string> LegsAsync(Victim victim, long rowIndex, CancellationToken ct)
    {
        var client = forum.Client;
        var (tokenStatus, tokenBody) = await victim.Dpop.RequestTokenAsync(client, KeyActRequests.TokenEndpoint, forum.Now, victim.Agent.AgentId, ct);
        using var asked = await victim.Dpop.PostAsync(client, PostsUrl, victim.Token, victim.Agent.SignQuestion(victim.Board, "After the row. " + Suffix(), "Second " + Suffix(), forum.Now), forum.Now, ct);
        var servedPost = await client.GetFromJsonAsync<JsonElement>($"/v1/posts/{victim.PostId}", ct);
        var provenance = servedPost.GetProperty("provenance");
        var keyStatus = provenance.GetProperty("key_status").GetString();
        var marked = provenance.TryGetProperty("disputed_by_log_index", out _);

        using var keySetResponse = await client.GetAsync(new Uri($"/v1/jwks?agent={Uri.EscapeDataString(victim.Agent.AgentId)}", UriKind.Relative), ct);
        var keySetText = await keySetResponse.Content.ReadAsStringAsync(ct);
        long[] lifecycle = [];
        if (keySetResponse.StatusCode == HttpStatusCode.OK)
            lifecycle = [.. JsonDocument.Parse(keySetText).RootElement.GetProperty("curia_lifecycle").EnumerateArray().Select(i => i.GetInt64())];

        var rotation = "n/a";
        if (lifecycle.Length > 0)
        {
            var next = KeyActRequests.KeyPair.New("next-" + Suffix());
            using var rotated = await victim.Dpop.PostAsync(
                client, KeyActRequests.RotateUrl(victim.Agent.AgentId), victim.Token, KeyActRequests.RotationBody(victim.Agent, next, lifecycle[^1]), forum.Now, ct, contentType: "application/json");
            rotation = ((int)rotated.StatusCode).ToString(CultureInfo.InvariantCulture);
        }

        var tokenWords = tokenStatus == HttpStatusCode.OK ? "200" : $"{(int)tokenStatus} {ErrorOf(tokenBody)}";
        return $"token {tokenWords}; issued-token ask {(int)asked.StatusCode}; key_status {keyStatus}{(marked ? " marked" : string.Empty)}; "
            + $"key set {(int)keySetResponse.StatusCode} {(lifecycle.Contains(rowIndex) ? "listing the row" : "without the row")}; rotate {rotation}";
    }

    private const string Healthy = "token 200; issued-token ask 201; key_status active; key set 200 without the row; rotate 201";

    private static JsonObject Retirement(string agent, string kid, bool withAgent = true, string typ = KeyStatements.RetirementStatementTyp, bool extra = false)
    {
        var statement = $"{{\"agent_id\":\"{agent}\",\"kid\":\"{kid}\",\"prev\":1,\"typ\":\"{typ}\"}}";
        var row = new JsonObject();
        if (withAgent) row["agent_id"] = agent;
        row["authorization"] = "this-is-not-a-signature-qq7";
        row["kid"] = kid;
        row["prev"] = 1;
        row["statement"] = statement;
        if (extra) row["surprise"] = "x";
        return row;
    }

    /// <summary>A <c>/2</c> declaration row of <paramref name="kids"/>, its <c>declaration</c> member <paramref name="declaration"/>.</summary>
    private static JsonObject Declaration(string agent, string[] kids, long fromIndex, long prev, string declaration)
    {
        var statement = Encoding.UTF8.GetString(CanonicalJson.CanonicalizeWithNfc(new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent)),
            new("from_index", new JsonValue.Number(fromIndex)),
            new("kids", new JsonValue.Array([.. kids.Select(k => (JsonValue)new JsonValue.String(k))])),
            new("prev", new JsonValue.Number(prev)),
            new("reason", new JsonValue.String("owner-reported")),
            new("typ", new JsonValue.String(KeyStatements.CompromiseStatementTyp)),
        ])).Match(c => c.Span.ToArray(), e => throw new InvalidOperationException(e.Type)));
        return new JsonObject
        {
            ["agent_id"] = agent,
            ["declaration"] = declaration,
            ["from_index"] = fromIndex,
            ["kids"] = new JsonArray([.. kids.Select(k => (JsonNode?)k)]),
            ["prev"] = prev,
            ["reason"] = "owner-reported",
            ["statement"] = statement,
        };
    }

    private static string StatementOf(JsonObject declaration) => declaration["statement"]!.GetValue<string>();

    private static string ZeroBytes(string kid) => Base64Url(Encoding.UTF8.GetBytes($"{{\"alg\":\"ES256\",\"kid\":\"{kid}\",\"typ\":\"curia-key-compromise+jws\"}}")) + ".." + Base64Url(new byte[64]);

    /// <summary>Signs <paramref name="statement"/> with <paramref name="key"/> under the declaration header <c>typ</c>: a real signature by a key that is no declaration key.</summary>
    private static string SignDeclaration(string statement, LogSigningKey key)
    {
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(statement), AdmitLimits.Default).Match(v => v, e => throw new InvalidOperationException(e.Type));
        var canonical = CanonicalJson.CanonicalizeWithNfc(parsed).Match(c => c, e => throw new InvalidOperationException(e.Type));
        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner> { ["ES256"] = new Es256Adapter() },
            new Dictionary<string, IContentVerifier>(),
            KeyStatements.CompromiseHeaderTyp);
        return jws.Sign(canonical, key.AsSigningKey()).Match(s => s.Compact, e => throw new InvalidOperationException(e.Type));
    }

    /// <summary>The five shapes of M3, one at a time.</summary>
    public static TheoryData<string> M3Shapes => ["retirement-agent-id-absent", "payload-not-an-object", "unknown-statement-typ", "extra-member", "statement-less-binding"];

    private static (string Type, string Payload) M3Row(string shape, Victim v) => shape switch
    {
        "retirement-agent-id-absent" => ("agent.key-retired", Retirement(v.Agent.AgentId, v.Agent.Kid, withAgent: false).ToJsonString()),
        "payload-not-an-object" => ("agent.key-retired", "\"not an object\""),
        "unknown-statement-typ" => ("agent.key-retired", Retirement(v.Agent.AgentId, v.Agent.Kid, typ: "curia.key-retirement/9").ToJsonString()),
        "extra-member" => ("agent.key-retired", Retirement(v.Agent.AgentId, v.Agent.Kid, extra: true).ToJsonString()),
        "statement-less-binding" => ("agent.key-bound", new JsonObject
        {
            ["agent_id"] = v.Agent.AgentId,
            ["jwk"] = new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = "AAAA", ["y"] = "AAAA" },
            ["kid"] = "forged-" + Suffix(),
        }.ToJsonString()),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "not one of M3's five shapes"),
    };

    /// <summary>
    /// LRV12: no row's content stops an agent. Each of M3's five shapes, appended to the victim's stream through the app role's own connection, leaves every leg of the victim's use at the control's answer.
    /// Red on the seam, and the red text is what the unmodified tree measured (M3): the token request is <c>401 invalid_client</c> and the key set is <c>503</c>.
    /// </summary>
    [Theory]
    [MemberData(nameof(M3Shapes))]
    public async Task LRV12_NoRowsContentStopsTheAgentItNames(string shape)
    {
        var ct = TestContext.Current.CancellationToken;
        var control = await VictimAsync("lrv12c", ct);
        WholeText.Same(Healthy, await LegsAsync(control, -1, ct));

        var victim = await VictimAsync("lrv12", ct);
        var index = await EventCountAsync(ct);
        var (type, payload) = M3Row(shape, victim);
        await InsertRowAsync(type, victim.Agent.AgentId, payload, ct);

        WholeText.Same(Healthy, await LegsAsync(victim, index, ct));
    }

    /// <summary>The five shapes of the forgery probe, each a leg of one class: (a) to (d) are declarations, (e) the takeover.</summary>
    public static TheoryData<string> ForgeryLegs => ["a-zero-bytes", "b-signed-by-the-log-key", "c-forged-retirement", "d-log-key-over-the-real-kid-and-an-invented-one"];

    /// <summary>
    /// LRV13 (a) to (d): a forged declaration or retirement leaves the victim's token request 200, the post's key status active, not omitted from search (<c>excluded_disputed</c> 0), no <c>disputed_by_log_index</c>.
    /// Red on the seam: <c>Expected: 200  Actual: 401 invalid_client</c> on the token request, and the post marked.
    /// </summary>
    [Theory]
    [MemberData(nameof(ForgeryLegs))]
    public async Task LRV13_AForgedDeclarationOrRetirementIsInert(string leg)
    {
        var ct = TestContext.Current.CancellationToken;
        var control = await VictimAsync("lrv13c", ct);
        var victim = await VictimAsync("lrv13", ct);
        var index = await EventCountAsync(ct);
        var agent = victim.Agent.AgentId;
        var kid = victim.Agent.Kid;

        using var logKey = LogSigningKey.FromPem(LogSigningKey.GeneratePem());
        switch (leg)
        {
            case "a-zero-bytes":
                await InsertRowAsync("agent.key-compromised", agent, Declaration(agent, [kid], 1, 1, ZeroBytes(kid)).ToJsonString(), ct);
                break;

            case "b-signed-by-the-log-key":
            {
                var row = Declaration(agent, [kid], 1, 1, "x");
                row["declaration"] = SignDeclaration(StatementOf(row), logKey);
                await InsertRowAsync("agent.key-compromised", agent, row.ToJsonString(), ct);
                break;
            }

            case "c-forged-retirement":
                await InsertRowAsync("agent.key-retired", agent, Retirement(agent, kid).ToJsonString(), ct);
                break;

            case "d-log-key-over-the-real-kid-and-an-invented-one":
            {
                var row = Declaration(agent, [kid, "invented-" + Suffix()], 1, 1, "x");
                row["declaration"] = SignDeclaration(StatementOf(row), logKey);
                await InsertRowAsync("agent.key-compromised", agent, row.ToJsonString(), ct);
                break;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(leg), leg, "not one of the forgery probe's legs");
        }

        var client = forum.Client;
        var term = "zforge" + Suffix();
        var (tokenStatus, tokenBody) = await victim.Dpop.RequestTokenAsync(client, KeyActRequests.TokenEndpoint, forum.Now, agent, ct);
        var provenance = (await client.GetFromJsonAsync<JsonElement>($"/v1/posts/{victim.PostId}", ct)).GetProperty("provenance");
        using var search = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/v1/search?q=Before&board={victim.Board}", UriKind.Relative), ct));
        var excluded = search.RootElement.GetProperty("floor").GetProperty("excluded_disputed").GetInt32();
        _ = (control, term);

        WholeText.Same(
            "token 200; key_status active; no disputed_by_log_index; excluded_disputed 0",
            $"token {(tokenStatus == HttpStatusCode.OK ? "200" : $"{(int)tokenStatus} {ErrorOf(tokenBody)}")}; key_status {provenance.GetProperty("key_status").GetString()}; "
            + $"{(provenance.TryGetProperty("disputed_by_log_index", out var by) ? "disputed_by_log_index " + by.GetInt64().ToString(CultureInfo.InvariantCulture) : "no disputed_by_log_index")}; excluded_disputed {excluded}");
        _ = index;
    }

    /// <summary>
    /// LRV13 (e), the takeover: an <c>agent_keys</c> <c>INSERT</c> under the victim's id (the provisioning role), a forged statement-bearing <c>agent.key-bound</c> naming the attacker's key (the app role), and an
    /// attacker client assertion for the victim's <c>client_id</c>. The attacker's token request is <c>401 invalid_client</c>, a post signed by the attacker key is refused and appends nothing, and the victim's own
    /// post and token are unaffected. Red on the seam, and the red text is the harm: <c>Expected: 401 invalid_client  Actual: 200</c>, and the attacker's post is accepted.
    /// </summary>
    [Fact]
    public async Task LRV13_TheTakeoverIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await VictimAsync("lrv13e", ct);
        var hole = ForumAgent.Create(victim.Agent.AgentId, "attacker-" + Suffix());
        await using (var admin = new NpgsqlConnection(forum.ConnectionString))
        {
            await admin.OpenAsync(ct);
            await using var insert = new NpgsqlCommand(
                "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from, valid_until) VALUES (@kid, @agent, 'ES256', @key, @from, NULL);", admin);
            insert.Parameters.AddWithValue("kid", hole.Kid);
            insert.Parameters.AddWithValue("agent", victim.Agent.AgentId);
            insert.Parameters.AddWithValue("key", hole.AssertionKey.ExportSubjectPublicKeyInfo());
            insert.Parameters.AddWithValue("from", forum.Now);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
        }

        var attackerKey = new KeyActRequests.KeyPair(hole.Kid, hole.AssertionKey);
        var statement = KeyActRequests.RotationStatement(victim.Agent.AgentId, victim.Agent.Kid, attackerKey, 1);
        var binding = new JsonObject
        {
            ["agent_id"] = victim.Agent.AgentId,
            ["authorization"] = "this-is-not-a-signature-qq7",
            ["authorized_by"] = victim.Agent.Kid,
            ["jwk"] = JsonNode.Parse(PublicJwk.Of(attackerKey.Material).Match(j => ToJson(j), e => throw new InvalidOperationException(e.Type))),
            ["kid"] = hole.Kid,
            ["possession"] = "this-is-not-a-signature-qq7",
            ["prev"] = 1,
            ["statement"] = statement,
        };
        await InsertRowAsync("agent.key-bound", victim.Agent.AgentId, binding.ToJsonString(), ct);

        var before = await EventCountAsync(ct);
        var (status, body) = await DpopClient.For(hole, hole.AssertionKey).RequestTokenAsync(client, KeyActRequests.TokenEndpoint, forum.Now, victim.Agent.AgentId, ct);
        var attackerAsked = "n/a";
        if (status == HttpStatusCode.OK)
        {
            var dpop = DpopClient.For(hole, hole.AssertionKey);
            var token = await dpop.GetTokenAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
            using var asked = await dpop.PostAsync(client, PostsUrl, token, hole.SignQuestion("board-" + Suffix(), "Signed under the attacker's key.", "Takeover " + Suffix(), forum.Now), forum.Now, ct);
            attackerAsked = ((int)asked.StatusCode).ToString(CultureInfo.InvariantCulture);
        }

        var after = await EventCountAsync(ct);
        using var own = await victim.Dpop.PostAsync(client, PostsUrl, victim.Token, victim.Agent.SignQuestion(victim.Board, "The victim's own. " + Suffix(), "Own " + Suffix(), forum.Now), forum.Now, ct);

        WholeText.Same(
            "attacker token 401 invalid_client; attacker ask n/a; events 0; victim ask 201",
            $"attacker token {(status == HttpStatusCode.OK ? "200" : $"{(int)status} {ErrorOf(body)}")}; attacker ask {attackerAsked}; events {after - before}; victim ask {(int)own.StatusCode}");
    }

    /// <summary>LRV13's control: a genuine declaration under a published declaration key still marks the post compromised and omits it from search (R6.57), where the probes above must not.</summary>
    [Fact]
    public async Task LRV13_AGenuineDeclarationStillMarksAndOmits_Control()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await VictimAsync("lrv13g", ct);
        var boundAt = await KeyActRequests.LatestLifecycleAsync(client, victim.Agent.AgentId, ct);
        var logPem = LogSigningKey.GeneratePem();
        var declarationPem = await DeclarationKeyHost.PemAsync(forum, ct);

        async Task<int> OperatorAsync(string? log, string? declaration, params string[] args)
        {
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            return await OperatorCommands.RunAsync(
                args, forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, ct,
                readLogSigningKeyPem: () => log, readDeclarationSigningKeyPem: () => declaration);
        }

        var published = await OperatorAsync(logPem, null, "sign-head", "--by", "ops");
        var declared = await OperatorAsync(null, declarationPem, "compromise", "--agent", victim.Agent.AgentId, "--kid", victim.Agent.Kid, "--from-index", boundAt.ToString(CultureInfo.InvariantCulture), "--reason", "owner-reported", "--operator", "ops");

        var provenance = (await client.GetFromJsonAsync<JsonElement>($"/v1/posts/{victim.PostId}", ct)).GetProperty("provenance");
        using var search = JsonDocument.Parse(await client.GetStringAsync(new Uri($"/v1/search?q=Before&board={victim.Board}", UriKind.Relative), ct));

        WholeText.Same(
            "sign-head 0; compromise 0; key_status compromised; marked; excluded_disputed 1",
            $"sign-head {published}; compromise {declared}; key_status {provenance.GetProperty("key_status").GetString()}; {(provenance.TryGetProperty("disputed_by_log_index", out _) ? "marked" : "not marked")}; "
            + $"excluded_disputed {search.RootElement.GetProperty("floor").GetProperty("excluded_disputed").GetInt32()}");
    }

    private static string ToJson(JsonValue.Object jwk) =>
        Encoding.UTF8.GetString(CanonicalJson.CanonicalizeWithNfc(jwk).Match(c => c.Span.ToArray(), e => throw new InvalidOperationException(e.Type)));

    /// <summary>
    /// LRV14: a skipped row does not stop the victim's own lifecycle acts. After a forged retirement, the victim's genuine rotation appends with <c>prev</c> the last honoured index and the row is honoured; the
    /// genuine retirement of the old key after it is honoured. Red on the seam: <c>Expected: 201  Actual: 503</c> (the old fold's answer for a forged row it refuses) or <c>409 curia/keys/lifecycle-moved</c>.
    /// </summary>
    [Fact]
    public async Task LRV14_ASkippedRowDoesNotStopTheVictimsOwnLifecycleActs()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var victim = await VictimAsync("lrv14", ct);
        await InsertRowAsync("agent.key-retired", victim.Agent.AgentId, Retirement(victim.Agent.AgentId, victim.Agent.Kid, withAgent: false).ToJsonString(), ct);

        using var keySet = await KeyActRequests.KeySetAsyncOrNull(client, victim.Agent.AgentId, ct);
        var prev = keySet is null ? -1 : KeyActRequests.LifecycleOf(keySet)[^1];
        var next = KeyActRequests.KeyPair.New("next-" + Suffix());
        using var rotated = await victim.Dpop.PostAsync(
            client, KeyActRequests.RotateUrl(victim.Agent.AgentId), victim.Token, KeyActRequests.RotationBody(victim.Agent, next, prev), forum.Now, ct, contentType: "application/json");
        var rotatedAt = rotated.StatusCode == HttpStatusCode.Created ? await KeyActRequests.LatestLifecycleAsync(client, victim.Agent.AgentId, ct) : -1;

        var old = new KeyActRequests.KeyPair(victim.Agent.Kid, victim.Agent.AssertionKey);
        using var retired = await victim.Dpop.PostAsync(
            client, KeyActRequests.RetireUrl(victim.Agent.AgentId, victim.Agent.Kid), victim.Token, KeyActRequests.RetirementBody(victim.Agent.AgentId, old, rotatedAt), forum.Now, ct, contentType: "application/json");

        WholeText.Same("rotate 201; retire 200", $"rotate {(int)rotated.StatusCode}; retire {(int)retired.StatusCode}");
    }
}
