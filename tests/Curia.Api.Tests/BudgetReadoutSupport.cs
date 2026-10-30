using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Domain.Moderation;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>An answer to <c>GET /v1/budget</c> (or to a request at a neighbouring path): the status, the raw body, the cache headers and the parsed body.</summary>
internal sealed record BudgetReply(HttpStatusCode Status, string Raw, string? CacheControl, bool HasValidator, JsonElement Body, string? Challenge = null)
{
    /// <summary>The words a failing assertion prints: status, and the start of the body.</summary>
    internal string Says => $"{(int)Status} {(Raw.Length > 300 ? Raw[..300] : Raw)}";

    /// <summary>The problem type, or the empty string.</summary>
    internal string Type => Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : string.Empty;

    /// <summary>The readout's entry of that name; throws the reply's words when the body has none.</summary>
    internal JsonElement Entry(string name)
    {
        if (Body.ValueKind == JsonValueKind.Object && Body.TryGetProperty("budgets", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var entry in list.EnumerateArray())
                if (entry.TryGetProperty("name", out var n) && n.GetString() == name)
                    return entry;

        throw new Xunit.Sdk.XunitException($"Expected: a `{name}` entry Actual: {Says}");
    }
}

/// <summary>The outcome of one act through its route: the status, the problem type and detail where there is one, and the raw body.</summary>
internal sealed record Act(int Status, string Type, string? Detail, string Raw)
{
    internal string Says => $"{Status} {Type} {Detail}".TrimEnd();

    /// <summary>The receipt's <c>budget</c> member, or an undefined element.</summary>
    internal JsonElement Budget
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(Raw);
                return doc.RootElement.TryGetProperty("budget", out var b) ? b.Clone() : default;
            }
            catch (JsonException)
            {
                return default;
            }
        }
    }

    internal string? ServerTs
    {
        get
        {
            try
            {
                using var doc = JsonDocument.Parse(Raw);
                return doc.RootElement.TryGetProperty("server_ts", out var s) ? s.GetString() : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}

/// <summary>The standing the errata's sentences give for a set of instants, written out here (R7.28): the arithmetic the oracle applies to the events table.</summary>
internal sealed record Standing(int Limit, int Used, int Remaining, string? NextSlotAt)
{
    internal string Describe() => $"limit={Limit} used={Used} remaining={Remaining} next_slot_at={NextSlotAt ?? "null"}";
}

/// <summary>
/// The helpers errata G49's route facts share (ruling 11 (d), (e)): an authenticated GET, the oracles read from the events table (and the flag store's
/// table) by SQL from the fixture's own connection string, the limits parsed from Table 11, R7.26 and R7.22, and the arithmetic of R7.28 written out. No fact
/// here asserts a wall-clock claim: the window's boundary moves the fixture's settable clock.
/// </summary>
internal static class BudgetReadouts
{
    internal const string TokenEndpoint = "http://localhost/oauth/token";
    internal static readonly string[] Tiers = ["T0", "T1", "T2"];
    internal static readonly string[] Budgets = ["posting", "acceptance", "flag"];

    /// <summary>R7.28's window, parsed from the errata.</summary>
    internal static TimeSpan Window => TimeSpan.FromSeconds(PublishedBudgetReadout.WindowSeconds());

    internal static string Form(DateTimeOffset instant) => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    /// <summary>A budget's limit at a tier: Table 11 (posting), R7.26 (acceptance), R7.22 (flag), as published.</summary>
    internal static int LimitOf(string tier, string budget)
    {
        var posts = Tiers.Select(t => Curia.Domain.Tests.Authorization.PublishedTable11.Rows[t].PostsPerDay!.Value).ToArray();
        return PublishedBudgetReadout.Limits(posts)[budget][Array.IndexOf(Tiers, tier)];
    }

    /// <summary>A DPoP-bound GET as <paramref name="actor"/> (or none), with the proof signed by <paramref name="proofBy"/> when it is not the actor's key.</summary>
    internal static async Task<BudgetReply> GetAsync(ForumFixture forum, SceneActor? actor, string path = "/v1/budget", DpopClient? proofBy = null, string? token = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var htu = "http://localhost" + path.Split('?')[0];
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (actor is not null)
        {
            token ??= await actor.Dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct);
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
            request.Headers.Add("DPoP", (proofBy ?? actor.Dpop).Proof("GET", htu, forum.Now, token, nonce: null));
        }

        using var response = await client.SendAsync(request, ct);
        var raw = await response.Content.ReadAsStringAsync(ct);
        JsonElement body = default;
        if (raw.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                body = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // A body that is not JSON is read as no body; the raw text is still on the reply.
            }
        }

        return new BudgetReply(
            response.StatusCode, raw, response.Headers.CacheControl?.ToString(),
            response.Headers.ETag is not null || response.Content.Headers.LastModified is not null, body,
            string.Join(' ', response.Headers.WwwAuthenticate.Select(h => h.ToString())));
    }

    /// <summary>A token for the actor, minted now, for a fact that must hold it before it changes a seam.</summary>
    internal static Task<string> TokenAsync(ForumFixture forum, SceneActor actor) =>
        actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, TestContext.Current.CancellationToken);

    /// <summary>The readout, asserted to be a <c>200</c> in the words the red text carries.</summary>
    internal static async Task<BudgetReply> ReadAsync(ForumFixture forum, SceneActor actor)
    {
        var reply = await GetAsync(forum, actor);
        Assert.True(reply.Status == HttpStatusCode.OK, $"Expected: 200 Actual: {reply.Says}");
        return reply;
    }

    /// <summary>How the response states one entry, in the words an assertion compares.</summary>
    internal static string Describe(JsonElement entry) =>
        $"limit={Value(entry, "limit")} used={Value(entry, "used")} remaining={Value(entry, "remaining")} next_slot_at={Value(entry, "next_slot_at")}";

    private static string Value(JsonElement entry, string member) =>
        entry.TryGetProperty(member, out var v) ? (v.ValueKind == JsonValueKind.Null ? "null" : v.ValueKind == JsonValueKind.String ? v.GetString()! : v.GetRawText()) : "absent";

    /// <summary>R7.28's arithmetic over stored instants: counted while less than the window old; k is the larger of 1 and used less limit plus 1.</summary>
    internal static Standing Expected(int limit, IReadOnlyList<DateTimeOffset> instants, DateTimeOffset now)
    {
        var counted = instants.Where(i => i > now - Window).Order().ToArray();
        var k = Math.Max(1, counted.Length - limit + 1);
        return new Standing(limit, counted.Length, Math.Max(0, limit - counted.Length), counted.Length == 0 ? null : Form(counted[k - 1] + Window));
    }

    /// <summary>The standing the events table (and the flag store's table) gives for an agent's budget, at the fixture's clock.</summary>
    internal static async Task<Standing> OracleAsync(ForumFixture forum, string tier, string budget, string agentId) =>
        Expected(LimitOf(tier, budget), await InstantsAsync(forum, budget, agentId), forum.Now);

    /// <summary>The stored <c>server_ts</c> of every entry the budget counts for the agent, oldest first: the column, at the precision the store holds.</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The statement is chosen from three constants by the budget's name; the agent is bound.")]
    internal static async Task<DateTimeOffset[]> InstantsAsync(ForumFixture forum, string budget, string agentId)
    {
        var sql = budget switch
        {
            "posting" => "SELECT server_ts FROM events WHERE event_type = 'post.accepted' AND payload->>'author' = @a ORDER BY server_ts, seq;",
            "acceptance" => "SELECT server_ts FROM events WHERE event_type = 'answer.accepted' AND payload->>'accepted_by' = @a ORDER BY server_ts, seq;",
            _ => "SELECT e.server_ts FROM flag_details d JOIN events e ON e.event_id = d.event_id WHERE e.event_type = 'flag.committed' AND d.raised_by = @a ORDER BY e.server_ts, e.seq;",
        };

        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, admin);
        command.Parameters.AddWithValue("a", agentId);
        var instants = new List<DateTimeOffset>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            instants.Add(new DateTimeOffset(DateTime.SpecifyKind(await reader.GetFieldValueAsync<DateTime>(0, TestContext.Current.CancellationToken), DateTimeKind.Utc), TimeSpan.Zero));

        return [.. instants];
    }

    /// <summary>A count of the rows in a table, for a fact that says a read changed nothing.</summary>
    internal static async Task<(long Events, long FlagRows)> TableSizesAsync(ForumFixture forum)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT (SELECT count(*) FROM events), (SELECT count(*) FROM flag_details);", admin);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        await reader.ReadAsync(TestContext.Current.CancellationToken);
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>
    /// Clones the agent's first raised flag <c>count - 1</c> more times by admin SQL (the app role cannot do it), each at the instant given, so a flag budget
    /// can start part spent without a request per flag: the event row and its private row, with new event ids and the same commitment inputs.
    /// </summary>
    internal static async Task CloneFlagsAsync(ForumFixture forum, string agentId, IReadOnlyList<DateTimeOffset> instants)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        await using var source = new NpgsqlCommand(
            "SELECT d.event_id FROM flag_details d WHERE d.raised_by = @a ORDER BY d.event_id LIMIT 1;", admin);
        source.Parameters.AddWithValue("a", agentId);
        var from = (string)(await source.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;

        foreach (var instant in instants)
        {
            var id = "clone-" + Guid.NewGuid().ToString("N");
            await using var clone = new NpgsqlCommand(
                "INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) " +
                "SELECT @new, event_type, aggregate_id, actor_id, payload, @at FROM events WHERE event_id = @src; " +
                "INSERT INTO flag_details (event_id, post_id, raised_by, rationale, salt) " +
                "SELECT @new, post_id, raised_by, rationale, salt FROM flag_details WHERE event_id = @src;", admin);
            clone.Parameters.AddWithValue("new", id);
            clone.Parameters.AddWithValue("src", from);
            clone.Parameters.AddWithValue("at", instant.UtcDateTime);
            await clone.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Stores <paramref name="count"/> acceptances for the agent as <c>accepted_by</c> without touching its posting budget: a thread by two other agents, one
    /// acceptance recorded with the host's own use case, and its event cloned (admin role) at each instant in turn.
    /// </summary>
    internal static async Task SeedAcceptsAsync(ForumFixture forum, string agentId, string board, IReadOnlyList<DateTimeOffset> instants)
    {
        if (instants.Count == 0) return;
        var other = ForumAgent.Create("https://agents.example/thread-" + Guid.NewGuid().ToString("N")[..8], "thread");
        var question = await BudgetBursts.StoreAsync(forum, other.SignQuestion(board, Probe.Unrelated(), "Thread " + Probe.Unrelated(), forum.Now), TestContext.Current.CancellationToken);
        var answer = await BudgetBursts.StoreAsync(forum, other.SignAnswer(board, Probe.Unrelated(), question.PostId, forum.Now), TestContext.Current.CancellationToken);
        await forum.AppendStoredAcceptanceAsync(question.PostId, answer.PostId, agentId, TestContext.Current.CancellationToken);

        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var instant in instants.Skip(1))
        {
            await using var clone = new NpgsqlCommand(
                "INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) " +
                "SELECT 'clone-' || gen_random_uuid()::text, e.event_type, e.aggregate_id, e.actor_id, e.payload, @at " +
                "FROM events e WHERE e.seq = (SELECT max(seq) FROM events WHERE event_type = 'answer.accepted' AND payload->>'accepted_by' = @a);", admin);
            clone.Parameters.AddWithValue("a", agentId);
            clone.Parameters.AddWithValue("at", instant.UtcDateTime);
            await clone.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A flag raised through the route against <paramref name="postId"/>: the status and the problem, or the receipt.</summary>
    internal static Task<Act> FlagAsync(ForumFixture forum, SceneActor actor, string postId, FlagKind kind) =>
        PostJsonAsync(forum, actor, $"http://localhost/v1/posts/{postId}/flags", JsonSerializer.Serialize(new { kind = FlagKinds.Wire(kind), rationale = "this reads as " + FlagKinds.Wire(kind) }));

    /// <summary>An acceptance through the route.</summary>
    internal static Task<Act> AcceptAsync(ForumFixture forum, SceneActor actor, string answerId) =>
        PostJsonAsync(forum, actor, $"http://localhost/v1/posts/{answerId}/accept", "{}");

    internal static async Task<Act> PostJsonAsync(ForumFixture forum, SceneActor actor, string url, string json)
    {
        var ct = TestContext.Current.CancellationToken;
        using var response = await actor.Dpop.PostAsync(
            forum.Client, url, await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct), Encoding.UTF8.GetBytes(json), forum.Now, ct, contentType: "application/json");
        return Read((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>A question through the route: text with nothing in common with any other, so the near-duplicate check refuses none.</summary>
    internal static async Task<Act> AskAsync(ForumFixture forum, SceneActor actor, string board)
    {
        var sent = await Probe.SendAsync(forum, actor, actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), cancellationToken: TestContext.Current.CancellationToken);
        return Read(sent.Status, sent.Body);
    }

    private static Act Read(int status, string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            string? Text(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return new Act(status, Text("type") ?? string.Empty, Text("detail"), raw);
        }
        catch (JsonException)
        {
            return new Act(status, string.Empty, null, raw);
        }
    }

    /// <summary>Moves the fixture's clock to <paramref name="instant"/> (forward only).</summary>
    internal static void MoveTo(ForumFixture forum, DateTimeOffset instant)
    {
        var by = instant - forum.Now;
        Assert.True(by >= TimeSpan.Zero, $"the clock cannot move back: {forum.Now:O} to {instant:O}");
        forum.Clock.Advance(by);
    }
}

/// <summary>
/// One agent and the next act of one kind it can send, so a fact can spend a budget one real act at a time (the three budgets' routes share this shape). A
/// posting actor and a flag actor are fresh T0 agents; an acceptance actor is a scene's asker, who accepts the scene's two answers in turn.
/// </summary>
internal sealed class BudgetActor
{
    private readonly ForumFixture _forum;
    private readonly Func<int, Task<Act>> _next;
    private int _count;

    private BudgetActor(ForumFixture forum, string budget, SceneActor actor, Func<int, Task<Act>> next)
    {
        _forum = forum;
        Budget = budget;
        Actor = actor;
        _next = next;
    }

    internal string Budget { get; }

    internal SceneActor Actor { get; }

    /// <summary>The next act of this budget's kind; each call is a new act (a new post, the other answer, the next flag of a kind on a post).</summary>
    internal async Task<Act> NextAsync()
    {
        var act = await _next(_count);
        if (act.Status is 200 or 201) _count++;   // a refused act is tried again as itself, so a refused acceptance is not turned into a replay
        return act;
    }

    internal static async Task<BudgetActor> T0Async(ForumFixture forum, string budget, CancellationToken ct)
    {
        var board = Probe.Board();
        switch (budget)
        {
            case "posting":
            {
                var actor = await AcceptedAnswerScene.EnrolAsync(forum, "poster", ct);
                return new BudgetActor(forum, budget, actor, _ => BudgetReadouts.AskAsync(forum, actor, board));
            }

            case "acceptance":
            {
                var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
                return new BudgetActor(forum, budget, scene.Asker, i => BudgetReadouts.AcceptAsync(forum, scene.Asker, (i % 2 == 0 ? scene.Answer1 : scene.Answer2).PostId));
            }

            default:
            {
                var actor = await AcceptedAnswerScene.EnrolAsync(forum, "flagger", ct);
                var posts = new[] { await BudgetBursts.StoredQuestionAsync(forum, board, ct), await BudgetBursts.StoredQuestionAsync(forum, board, ct) };
                var kinds = Enum.GetValues<FlagKind>();
                return new BudgetActor(forum, budget, actor, i => BudgetReadouts.FlagAsync(forum, actor, posts[i / kinds.Length % posts.Length].PostId, kinds[i % kinds.Length]));
            }
        }
    }

    /// <summary>Spends <paramref name="acts"/> real acts, each a minute and a few ticks (1 to 9) after the one before, so the stamps carry sub-microsecond residue.</summary>
    internal async Task SpendAsync(int acts)
    {
        for (var i = 0; i < acts; i++)
        {
            _forum.Clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromTicks(1 + i % 9));
            var act = await NextAsync();
            Assert.True(act.Status is 200 or 201, $"Expected: an act of the {Budget} budget stored Actual: {act.Says}");
        }
    }
}
