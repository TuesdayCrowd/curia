#pragma warning disable CA1305, CA1308, CA1707, CA1822, CA1031, CA2007, CA1861, CA1859, CA1062, CA1307, CA1310, CA1002, CA1819
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain.Content;
using Curia.Domain.Keys;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// PR N2, Task 1: the shared support of the operator-act probes (errata G70). It forges rows the way
/// <c>ForgedLifecycleRowsTests</c> does (an <c>INSERT</c> as the application role, the six granted columns) and
/// reads every result through the same observer, so a control and the harm it controls for are read alike.
/// </summary>
internal sealed class OperatorActsApi
{
    internal const string Token = "http://localhost/oauth/token";
    internal const string Posts = "http://localhost/v1/posts";
    private readonly ForumFixture _forum;
    private readonly HttpClient? _client;

    internal OperatorActsApi(ForumFixture forum, HttpClient? client = null)
    {
        _forum = forum;
        _client = client;
    }

    internal ForumFixture Forum => _forum;

    internal HttpClient Http()
    {
        var c = _client ?? _forum.Client;
        c.DefaultRequestHeaders.Host = "localhost";
        return c;
    }

    /// <summary>Records an observation to the file the T1_OUT variable names (and the console): a red text is what these probes leave behind, and a run's output is read from a file.</summary>
    internal static void Log(string line)
    {
        var path = Environment.GetEnvironmentVariable("T1_OUT");
        if (!string.IsNullOrEmpty(path)) File.AppendAllText(path, line + Environment.NewLine);
        Console.WriteLine("T1| " + line);
    }

    internal static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    internal static string Board() => "board-" + Suffix();

    /// <summary>The genuine attestation, written the way the operator's writer writes it today. Task 4 gives <c>AttestOwner</c> a signer; this is the one place that changes in the Api tests.</summary>
    internal Task GenuineAttest(string agentId, CancellationToken ct, string owner = "owner:test", bool verified = true) =>
        _forum.AttestOwnerAsync(agentId, ct, owner, verified);

    /// <summary>The genuine withholding, through the writer the operator tool runs. Task 4 gives <c>ApplyModeration</c> a signer; this is the one place that changes in the Api tests.</summary>
    internal Task GenuineWithhold(string postId, CancellationToken ct) => _forum.WithholdAsync(postId, ct);

    /// <summary>The genuine restore; see <see cref="GenuineWithhold"/>.</summary>
    internal Task GenuineRestore(string postId, CancellationToken ct) => _forum.RestoreAsync(postId, ct);

    internal string RoleConn() =>
        new NpgsqlConnectionStringBuilder(_forum.ConnectionString) { Username = _forum.RoleName, Password = _forum.RolePassword }.ConnectionString;

    internal static string ToJson(JsonValue.Object jwk) =>
        Encoding.UTF8.GetString(CanonicalJson.CanonicalizeWithNfc(jwk).Match(c => c.Span.ToArray(), e => throw new InvalidOperationException(e.Type)));

    internal async Task<string?> TryInsertRowAsync(string type, string aggregate, string payloadJson, string actor, DateTimeOffset ts, CancellationToken ct)
    {
        var id = new Curia.Domain.Primitives.UlidGenerator(_forum.Clock).Next().Match(u => u.ToString(), e => throw new InvalidOperationException(e.Type));
        try
        {
            await using var c = new NpgsqlConnection(RoleConn());
            await c.OpenAsync(ct);
            await using var ins = new NpgsqlCommand(
                "INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) VALUES (@id, @type, @agg, @actor, @p::jsonb, @ts);", c);
            ins.Parameters.AddWithValue("id", id);
            ins.Parameters.AddWithValue("type", type);
            ins.Parameters.AddWithValue("agg", aggregate);
            ins.Parameters.AddWithValue("actor", actor);
            ins.Parameters.AddWithValue("p", payloadJson);
            ins.Parameters.AddWithValue("ts", ts);
            return await ins.ExecuteNonQueryAsync(ct) == 1 ? null : "0 rows";
        }
        catch (PostgresException ex)
        {
            return $"SQLSTATE {ex.SqlState}: {ex.MessageText}";
        }
    }

    internal async Task InsertRowAsync(string type, string aggregate, string payloadJson, string actor, DateTimeOffset ts, CancellationToken ct)
    {
        var refused = await TryInsertRowAsync(type, aggregate, payloadJson, actor, ts, ct);
        Assert.True(refused is null, $"insert {type} refused: {refused}");
    }

    internal async Task<long> EventCountAsync(CancellationToken ct)
    {
        await using var c = new NpgsqlConnection(_forum.ConnectionString);
        await c.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM events;", c);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    internal sealed record Actor(ForumAgent Agent, DpopClient Dpop);

    /// <summary>A problem document read apart: the status, the <c>type</c> member, the <c>title</c>, the <c>detail</c> and the raw body.</summary>
    internal sealed record Reply(int Status, string Type, string Title, string Detail, string Body, string PostId, string Digest)
    {
        internal string Words => $"{Status} {Type} (reason {Detail})";
    }

    internal static Reply Read(HttpStatusCode status, string body)
    {
        string Member(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "-";
        try
        {
            using var d = JsonDocument.Parse(body);
            var root = d.RootElement;
            var type = Member(root, "type") is "-" ? Member(root, "error") : Member(root, "type");
            return new Reply((int)status, type, Member(root, "title"), Member(root, "detail"), body, Member(root, "post_id"), Member(root, "digest"));
        }
        catch (JsonException)
        {
            return new Reply((int)status, "-", "-", "-", body, "-", "-");
        }
    }

    internal async Task<Actor> GenuineAsync(string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{Suffix()}", $"{stem}-{Suffix()}");
        var (dpop, _) = await agent.AuthenticateAsync(Http(), Token, _forum.Now, ct);
        return new Actor(agent, dpop);
    }

    /// <summary>
    /// A forged identity: no genuine enrolment touched it; every row is inserted as the application role.
    /// <paramref name="attestPayload"/> is written as given (a forged attestation's payload carries no statement, as every row written before N2).
    /// </summary>
    internal async Task<Actor> ForgeAsync(string stem, TimeSpan enrolledAgo, bool attest, string owner, CancellationToken ct, bool attestVerified = true, bool enrolRoot = true)
    {
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{Suffix()}", $"{stem}-{Suffix()}");
        var ts = _forum.Now - enrolledAgo;
        var spki = agent.AssertionKey.ExportSubjectPublicKeyInfo();
        await using (var c = new NpgsqlConnection(RoleConn()))
        {
            await c.OpenAsync(ct);
            await using var ins = new NpgsqlCommand(
                "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from, valid_until) VALUES (@kid, @agent, 'ES256', @key, @from, NULL);", c);
            ins.Parameters.AddWithValue("kid", agent.Kid);
            ins.Parameters.AddWithValue("agent", agent.AgentId);
            ins.Parameters.AddWithValue("key", spki);
            ins.Parameters.AddWithValue("from", ts);
            await ins.ExecuteNonQueryAsync(ct);
        }

        var jwk = ToJson(PublicJwk.Of(new PublicKeyMaterial("ES256", agent.Kid, spki)).Match(j => j, e => throw new InvalidOperationException(e.Type)));
        var enrolled = $"{{\"agent_id\":\"{agent.AgentId}\",\"kid\":\"{agent.Kid}\",\"reason\":\"Enrollment accepted: agent key registered with the Registrar\"}}";
        var bound = $"{{\"agent_id\":\"{agent.AgentId}\",\"jwk\":{jwk},\"kid\":\"{agent.Kid}\"}}";
        if (enrolRoot) await InsertRowAsync("agent.enrolled", agent.AgentId, enrolled, agent.AgentId, ts, ct);
        await InsertRowAsync("agent.key-bound", agent.AgentId, bound, agent.AgentId, ts, ct);
        if (attest) await ForgeAttestationAsync(agent.AgentId, owner, attestVerified, ts, ct);
        return new Actor(agent, DpopClient.For(agent, agent.AssertionKey));
    }

    internal Task ForgeAttestationAsync(string agentId, string owner, bool verified, DateTimeOffset ts, CancellationToken ct, string actor = "operator:ops") =>
        InsertRowAsync("agent.owner-attested", agentId,
            $"{{\"agent_id\":\"{agentId}\",\"method\":\"manual\",\"owner_id\":\"{owner}\",\"owner_verified\":{(verified ? "true" : "false")},\"reason\":\"forged by a probe\"}}",
            actor, ts, ct);

    /// <summary>A forged moderation row, with every member R10.60 lists and no statement (the shape of every row written before N2).</summary>
    internal Task ForgeModerationAsync(string postId, string digest, string effect, CancellationToken ct, string category = "spam", string moderator = "human", string[]? adjudicates = null, string actor = "operator:ops") =>
        InsertRowAsync("moderation.applied", postId,
            $"{{\"actor_id\":\"{actor}\",\"adjudicates\":[{string.Join(",", (adjudicates ?? []).Select(a => "\"" + a + "\""))}],\"category\":\"{category}\",\"digest\":\"{digest}\",\"effect\":\"{effect}\",\"moderator\":\"{moderator}\",\"post_id\":\"{postId}\",\"rationale\":\"forged by a probe\"}}",
            actor, _forum.Now, ct);

    internal async Task<Reply> PostRawAsync(Actor a, byte[] wire, CancellationToken ct)
    {
        var (tstatus, tbody) = await a.Dpop.RequestTokenAsync(Http(), Token, _forum.Now, a.Agent.AgentId, ct);
        if (tstatus != HttpStatusCode.OK) return Read(tstatus, tbody) with { Type = "token:" + Read(tstatus, tbody).Type };
        var token = JsonDocument.Parse(tbody).RootElement.GetProperty("access_token").GetString()!;
        using var r = await a.Dpop.PostAsync(Http(), Posts, token, wire, _forum.Now, ct);
        return Read(r.StatusCode, await r.Content.ReadAsStringAsync(ct));
    }

    internal Task<Reply> AskAsync(Actor a, string board, CancellationToken ct) =>
        PostRawAsync(a, a.Agent.SignQuestion(board, Probe.Unrelated(), "Q " + Probe.Unrelated(), _forum.Now), ct);

    internal Task<Reply> AnswerAsync(Actor a, string board, string target, CancellationToken ct) =>
        PostRawAsync(a, a.Agent.SignAnswer(board, Probe.Unrelated(), target, _forum.Now), ct);

    /// <summary>The token endpoint's reply for an actor (status, body and, when issued, the decoded <c>tier</c> claim).</summary>
    internal async Task<(Reply Reply, string? Tier)> TokenAsync(Actor a, CancellationToken ct)
    {
        var (status, body) = await a.Dpop.RequestTokenAsync(Http(), Token, _forum.Now, a.Agent.AgentId, ct);
        var reply = Read(status, body);
        if (status != HttpStatusCode.OK) return (reply, null);
        var jwt = JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;
        var payload = jwt.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
        using var claims = JsonDocument.Parse(Convert.FromBase64String(payload));
        return (reply, claims.RootElement.TryGetProperty("tier", out var t) ? t.GetString() : "(no tier claim)");
    }

    /// <summary>A genuine T1 agent: attested, three questions, 49 hours on the fixture's clock. Advances the clock.</summary>
    internal async Task<Actor> GenuineT1Async(string stem, string board, CancellationToken ct)
    {
        var a = await GenuineAsync(stem, ct);
        await GenuineAttest(a.Agent.AgentId, ct, "owner:" + stem + "-" + Suffix());
        for (var i = 0; i < 3; i++) Assert.Equal(201, (await AskAsync(a, board, ct)).Status);
        _forum.Clock.Advance(TimeSpan.FromHours(49));
        return a;
    }

    internal async Task<string> QuestionByAsync(Actor genuine, string board, CancellationToken ct)
    {
        var r = await PostRawAsync(genuine, genuine.Agent.SignQuestion(board, "Target " + Probe.Unrelated(), "Target " + Probe.Unrelated(), _forum.Now), ct);
        Assert.True(r.Status == 201, $"target question {r.Words}");
        return r.PostId;
    }

    /// <summary>The provenance of a served post: the status, the raw <c>owner_verified</c> and <c>owner</c> members.</summary>
    internal async Task<(int Status, string Type, bool? OwnerVerified, string? Owner)> ProvenanceAsync(string postId, CancellationToken ct)
    {
        using var r = await Http().GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);
        var body = await r.Content.ReadAsStringAsync(ct);
        if (r.StatusCode != HttpStatusCode.OK) return ((int)r.StatusCode, Read(r.StatusCode, body).Type, null, null);
        var p = JsonDocument.Parse(body).RootElement.GetProperty("provenance");
        return (200, "-", p.GetProperty("owner_verified").GetBoolean(), p.TryGetProperty("owner", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null);
    }

    internal async Task<int> GetStatusAsync(string relative, CancellationToken ct)
    {
        using var r = await Http().GetAsync(new Uri(relative, UriKind.Relative), ct);
        return (int)r.StatusCode;
    }

    internal async Task<string> GetBodyAsync(string relative, CancellationToken ct)
    {
        using var r = await Http().GetAsync(new Uri(relative, UriKind.Relative), ct);
        return (int)r.StatusCode + " " + await r.Content.ReadAsStringAsync(ct);
    }

    /// <summary>The post a question made: id and digest, for a forged moderation row to name.</summary>
    internal async Task<(Actor Who, string Board, string Id, string Digest, string Words)> QuestionAsync(string stem, CancellationToken ct)
    {
        var who = await GenuineAsync(stem, ct);
        var board = Board();
        var words = "zq" + Suffix() + " pelican";
        var r = await PostRawAsync(who, who.Agent.SignQuestion(board, $"How does {words} behave under load and what is its failure mode in production?", $"About {words}", _forum.Now), ct);
        Assert.True(r.Status == 201, r.Words);
        return (who, board, r.PostId, r.Digest, words);
    }

    /// <summary>The words of a reply as a probe states them: a tier denial and a spent budget are the same type, so the reason (the first word of <c>detail</c>) is part of what is said (Amendment 1, ruling 17).</summary>
    internal static string Said(Reply r) => r.Status is 201 or 200 ? $"{r.Status} (accepted)" : $"{r.Status} {r.Type} (reason {r.Detail.Split(' ')[0]})";

    /// <summary>The attestation through the genuine writer, as the words of its outcome: <c>recorded</c>, or the refusal's type.</summary>
    internal async Task<string> GenuineAttestResultAsync(string agentId, string owner, CancellationToken ct)
    {
        try
        {
            await GenuineAttest(agentId, ct, owner);
            return "recorded";
        }
        catch (InvalidOperationException e)
        {
            return "refused " + e.Message.Split(':')[0];
        }
    }

    internal async Task<string> LevelAsync(string postId, CancellationToken ct)
    {
        using var r = await Http().GetAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);
        var body = await r.Content.ReadAsStringAsync(ct);
        return r.StatusCode != HttpStatusCode.OK ? $"{(int)r.StatusCode}" : JsonDocument.Parse(body).RootElement.GetProperty("provenance").GetProperty("verification_level").GetString()!;
    }

    internal async Task<int> SearchCountAsync(string words, string board, CancellationToken ct)
    {
        using var r = await Http().GetAsync(new Uri($"/v1/search?q={Uri.EscapeDataString(words)}&board={board}", UriKind.Relative), ct);
        var body = await r.Content.ReadAsStringAsync(ct);
        Assert.True(r.StatusCode == HttpStatusCode.OK, $"Expected: search answers 200  Actual: {(int)r.StatusCode} {body}");
        var doc = JsonDocument.Parse(body).RootElement;
        Assert.True(doc.TryGetProperty("results", out var rs), $"Expected: a results member  Actual: {body}");
        return rs.GetArrayLength();
    }

    /// <summary>
    /// How many rows the operator's own audit reports skipped (R6.67: a row the act fold does not honour), read from the total on its first line, <c>honoured &lt;h&gt;  skipped &lt;n&gt;</c>, which the audit does not cap:
    /// its listing stops at 100 rows, so a count of listed lines is a count of the truncation (Task 6b, ruling 100). Throws when the line is absent or does not parse: there is no default of 0.
    /// </summary>
    internal async Task<int> SkippedRowsAsync(CancellationToken ct)
    {
        var audit = await OperatorAsync(ct, "lifecycle-audit");
        var first = audit.Out.Split('\n')[0];
        var match = System.Text.RegularExpressions.Regex.Match(first, @"^honoured (\d+)  skipped (\d+)\r?$");
        if (!match.Success) throw new InvalidOperationException($"the audit's first line is not 'honoured <h>  skipped <n>': [{first}] (exit {audit.Exit}, stderr {audit.Err})");
        return int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>A forged attestation naming <paramref name="owner"/> for an agent a genuine enrolment made (no backdated root, no key rows).</summary>
    internal Task ForgeAttestationNowAsync(string agentId, string owner, CancellationToken ct, bool verified = true) =>
        ForgeAttestationAsync(agentId, owner, verified, _forum.Now, ct);

    internal Task<(int Exit, string Out, string Err)> OperatorAsync(CancellationToken ct, params string[] args) => OperatorWithKeysAsync(args, ct);

    /// <summary>
    /// The operator tool run in-process with the environment a test states. The operator-acts key defaults to the fixture's (published once, as the fixture's writers' is) for <c>attest-owner</c> and
    /// <c>moderate</c>, and to none for any other verb; a test that needs another key, or a mixed environment, hands the readers itself.
    /// </summary>
    internal async Task<(int Exit, string Out, string Err)> OperatorWithKeysAsync(
        string[] args, CancellationToken ct, Func<string?>? operatorActsKey = null, Func<string?>? logKey = null, Func<string?>? declarationKey = null, ILifecycleAppendGate? gate = null)
    {
        using var so = new StringWriter();
        using var se = new StringWriter();
        var exit = await Curia.OperatorTool.OperatorCommands.RunAsync(
            args, _forum.ConnectionString, _forum.Clock, gate ?? _forum.PermissiveGate(), Curia.OperatorTool.OperatorCommands.LoadDeclarationSigner, so, se, ct,
            readLogSigningKeyPem: logKey,
            readDeclarationSigningKeyPem: declarationKey,
            readOperatorActsSigningKeyPem: operatorActsKey ?? await _forum.OperatorActsKeyForAsync(args, ct));
        return (exit, so.ToString(), se.ToString());
    }

    /// <summary>A flag's state as <c>curia-operator flags --post</c> prints it: the word after <c>state</c> on the flag's line, or the first line's text.</summary>
    internal async Task<string> FlagStateAsync(string postId, CancellationToken ct)
    {
        var run = await OperatorAsync(ct, "flags", "--post", postId);
        var line = run.Out.Split('\n').FirstOrDefault(l => l.StartsWith("state", StringComparison.Ordinal));
        return line is null ? "no flag listed" : System.Text.RegularExpressions.Regex.Replace(line.Trim(), @"\s+", " ");
    }

    internal async Task<string> RaiseFlagAsync(Actor flagger, string postId, CancellationToken ct)
    {
        var (tst, tbody) = await flagger.Dpop.RequestTokenAsync(Http(), Token, _forum.Now, flagger.Agent.AgentId, ct);
        Assert.Equal(HttpStatusCode.OK, tst);
        var token = JsonDocument.Parse(tbody).RootElement.GetProperty("access_token").GetString()!;
        using var r = await flagger.Dpop.PostAsync(Http(), $"http://localhost/v1/posts/{postId}/flags", token,
            Encoding.UTF8.GetBytes("{\"kind\":\"spam\",\"rationale\":\"this is a flag raised by a probe\"}"), _forum.Now, ct, contentType: "application/json");
        Assert.True(r.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, "flag raise: " + (int)r.StatusCode);
        var ops = await OperatorAsync(ct, "flags", "--post", postId);
        var m = System.Text.RegularExpressions.Regex.Match(ops.Out, @"^flag\s+(\S+)", System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.True(m.Success, ops.Out + ops.Err);
        return m.Groups[1].Value;
    }

    /// <summary>What an agent's inbox says of a post on a board: <c>lists the post</c> or <c>omits the post</c>, with the status (Task 3b, C8).</summary>
    internal async Task<string> InboxListsAsync(Actor reader, string board, string postId, CancellationToken ct)
    {
        var token = await reader.Dpop.GetTokenAsync(Http(), Token, _forum.Now, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/inbox?board={board}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("DPoP", token);
        request.Headers.Add("DPoP", reader.Dpop.Proof("GET", "http://localhost/v1/inbox", _forum.Now, token, nonce: null));
        using var response = await Http().SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return $"{(int)response.StatusCode} {(text.Contains(postId, StringComparison.Ordinal) ? "lists the post" : "omits the post")}";
    }

    /// <summary>
    /// A forged flag (Task 3b, C20): a <c>flag_details</c> row and the <c>flag.committed</c> event that commits to it, both inserted as the application role. The commitment opens, so the Forum's directory
    /// lists the flag as a genuine one. Returns the flag's id.
    /// </summary>
    internal async Task<string> ForgeFlagAsync(string postId, string raiserAgentId, CancellationToken ct)
    {
        var flagId = new Curia.Domain.Primitives.UlidGenerator(_forum.Clock).Next().Match(u => u.ToString(), f => throw new InvalidOperationException(f.Type));
        const string salt = "c2FsdHNhbHRzYWx0c2FsdHNhbHRzYWx0c2FsdHNhbHQ";
        const string rationale = "forged flag by a probe";
        var commitment = Curia.Domain.Moderation.FlagCommitment.Of(postId, raiserAgentId, rationale, salt).Match(k => k, f => throw new InvalidOperationException(f.Type));
        await using var conn = new NpgsqlConnection(RoleConn());
        await conn.OpenAsync(ct);
        await using (var d = new NpgsqlCommand("INSERT INTO flag_details (event_id, post_id, raised_by, rationale, salt) VALUES (@id, @post, @raiser, @r, @s);", conn))
        {
            d.Parameters.AddWithValue("id", flagId);
            d.Parameters.AddWithValue("post", postId);
            d.Parameters.AddWithValue("raiser", raiserAgentId);
            d.Parameters.AddWithValue("r", rationale);
            d.Parameters.AddWithValue("s", salt);
            await d.ExecuteNonQueryAsync(ct);
        }

        await using var committed = new NpgsqlCommand("INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) VALUES (@id, 'flag.committed', @agg, NULL, @p::jsonb, @ts);", conn);
        committed.Parameters.AddWithValue("id", flagId);
        committed.Parameters.AddWithValue("agg", "flag:" + flagId);
        committed.Parameters.AddWithValue("p", $"{{\"commitment\":\"{commitment}\",\"kind\":\"spam\"}}");
        committed.Parameters.AddWithValue("ts", _forum.Now);
        await committed.ExecuteNonQueryAsync(ct);
        return flagId;
    }

    internal async Task<string> EnrollBodyMemberAsync(ForumAgent agent, string member, CancellationToken ct)
    {
        using var r = await agent.EnrollAsync(Http(), ct);
        var body = await r.Content.ReadAsStringAsync(ct);
        var root = JsonDocument.Parse(body).RootElement;
        return $"{(int)r.StatusCode} {(root.TryGetProperty(member, out var v) ? v.ToString() : Read(r.StatusCode, body).Type)}";
    }
}
