using System.Globalization;
using System.Net;
using System.Text;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// The helpers errata G48's route facts share: agents of each tier, posts and acceptances stored without the route (so the budget a fact starts from is
/// taken from the table and asserted), bursts released from one signal on one token, and the words a response is classified in. The limits come from
/// Table 11 (<c>PublishedTable11</c>) and R7.26 (<see cref="PublishedAcceptBudget"/>), never from the code under test.
/// </summary>
internal static class BudgetBursts
{
    internal const string TokenEndpoint = "http://localhost/oauth/token";

    internal static readonly string[] Tiers = ["T0", "T1", "T2"];

    internal static int PostLimit(string tier) =>
        Curia.Domain.Tests.Authorization.PublishedTable11.Rows[tier].PostsPerDay ?? throw new InvalidOperationException($"Table 11 names no posting limit for {tier}");

    internal static int AcceptLimit(string tier) => PublishedAcceptBudget.Numbers()[Array.IndexOf(Tiers, tier)];

    /// <summary>The kinds a tier may create among the burst's posts (Table 10's create rows: T0 asks and comments, T1 also answers, T2 also publishes findings).</summary>
    internal static PostKind[] KindsOf(string tier) => tier switch
    {
        "T0" => [PostKind.Question, PostKind.Comment],
        "T1" => [PostKind.Question, PostKind.Comment, PostKind.Answer],
        _ => [PostKind.Question, PostKind.Comment, PostKind.Answer, PostKind.Finding],
    };

    internal static async Task<SceneActor> AgentOfAsync(ForumFixture forum, string tier, string board, CancellationToken ct)
    {
        if (tier == "T0") return await AcceptedAnswerScene.EnrolAsync(forum, "t0", ct);
        if (tier == "T1") return await AcceptedAnswerScene.T1Async(forum, board, ct);
        return await T2Async(forum, board, ct);
    }

    /// <summary>Table 11's T2: a T1 agent with five of its answers accepted (stored, with the host's own use case) and thirty days at T1.</summary>
    internal static async Task<SceneActor> T2Async(ForumFixture forum, string board, CancellationToken ct)
    {
        var actor = await AcceptedAnswerScene.T1Async(forum, board, ct);
        var asker = ForumAgent.Create("https://agents.example/asker-" + Guid.NewGuid().ToString("N")[..8], "asker");
        for (var i = 0; i < 5; i++)
        {
            var question = await StoreAsync(forum, asker.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), ct);
            var answer = await StoreAsync(forum, actor.Agent.SignAnswer(board, Probe.Unrelated(), question.PostId, forum.Now), ct);
            await forum.AppendStoredAcceptanceAsync(question.PostId, answer.PostId, asker.AgentId, ct);
        }

        forum.Clock.Advance(TimeSpan.FromDays(31));
        return actor;
    }

    /// <summary>A post appended straight to the store (no route, no budget), so a burst can start with part of a budget spent.</summary>
    internal static async Task<ScenePost> StoreAsync(ForumFixture forum, byte[] wire, CancellationToken ct)
    {
        var (postId, digest) = await forum.AppendStoredPostAsync(wire, ct);
        return new ScenePost(postId, digest);
    }

    /// <summary>Stores <paramref name="count"/> questions by the agent, so that <paramref name="count"/> of its budget is spent.</summary>
    internal static async Task SeedPostsAsync(ForumFixture forum, ForumAgent agent, string board, int count, CancellationToken ct)
    {
        for (var i = 0; i < count; i++)
            await StoreAsync(forum, agent.SignQuestion(board, Probe.Unrelated(), "Seed " + Probe.Unrelated(), forum.Now), ct);
    }

    /// <summary>Stores a question by an agent the Forum has never enrolled, for a burst's comments and answers to name.</summary>
    internal static async Task<ScenePost> StoredQuestionAsync(ForumFixture forum, string board, CancellationToken ct)
    {
        var asker = ForumAgent.Create("https://agents.example/parent-" + Guid.NewGuid().ToString("N")[..8], "parent");
        return await StoreAsync(forum, asker.SignQuestion(board, Probe.Unrelated(), "Parent " + Probe.Unrelated(), forum.Now), ct);
    }

    /// <summary>A signed post of <paramref name="kind"/>, text unrelated to any other so the near-duplicate check refuses none.</summary>
    internal static byte[] Wire(ForumAgent agent, PostKind kind, string board, string parentQuestion, DateTimeOffset now)
    {
        if (kind == PostKind.Question) return agent.SignQuestion(board, Probe.Unrelated(), "Burst " + Probe.Unrelated(), now);
        if (kind == PostKind.Answer) return agent.SignAnswer(board, Probe.Unrelated(), parentQuestion, now);
        if (kind == PostKind.Comment) return agent.Sign(PostKind.Comment, board, Probe.Unrelated(), null, parentQuestion, now);
        if (kind == PostKind.Finding) return agent.Sign(PostKind.Finding, board, Probe.Unrelated(), "Finding " + Probe.Unrelated(), null, now);
        throw new ArgumentOutOfRangeException(nameof(kind), kind, "the burst draws only the kinds a tier creates");
    }

    /// <summary>N posts cycling through the tier's kinds, signed before the burst starts.</summary>
    internal static byte[][] Wires(SceneActor actor, string tier, string board, string parentQuestion, int count, DateTimeOffset now)
    {
        var kinds = KindsOf(tier);
        return [.. Enumerable.Range(0, count).Select(i => Wire(actor.Agent, kinds[i % kinds.Length], board, parentQuestion, now))];
    }

    /// <summary>All of <paramref name="wires"/> sent at once from one signal on one token minted before the burst.</summary>
    internal static async Task<Sent[]> BurstAsync(ForumFixture forum, SceneActor actor, byte[][] wires, HttpClient? client = null, CancellationToken ct = default)
    {
        var token = await actor.Dpop.GetTokenAsync(client ?? forum.Client, TokenEndpoint, forum.Now, ct);
        return await Probe.TogetherAsync(wires.Select(w => (Func<Task<Sent>>)(() => Probe.SendViaAsync(forum, client, actor, w, token, ct))));
    }

    /// <summary>One response in a word: its status and, for a refusal, what refused it (the budget by its published reason, the gate by its published type).</summary>
    internal static string Kind(Sent sent)
    {
        ArgumentNullException.ThrowIfNull(sent);
        var (posting, acceptance) = PublishedBudgetGate.BudgetReasons();
        if (sent.Threw is not null) return "threw";
        if (sent.Status == (int)HttpStatusCode.Created) return "201";
        if (sent.Status == (int)HttpStatusCode.OK) return "200";
        if (sent.Status == (int)HttpStatusCode.Forbidden && sent.Body.Contains(posting, StringComparison.Ordinal)) return "403 " + posting;
        if (sent.Status == (int)HttpStatusCode.Forbidden && sent.Body.Contains(acceptance, StringComparison.Ordinal)) return "403 " + acceptance;
        return sent.Type.Length > 0 ? $"{sent.Status} {sent.Type}" : sent.Status.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The distinct words of a burst's responses with how many of each, in a stable order.</summary>
    internal static string Tally(IEnumerable<Sent> sent) =>
        string.Join(", ", sent.GroupBy(Kind).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} x {g.Key}"));

    /// <summary>
    /// Whether every response is the one a burst may be answered with: stored, or told the budget, or (only for the tail of a burst wider than the holder and the
    /// queue, R7.27) told agent-in-flight: at most as many of those as requests beyond the holder and the published number waiting.
    /// </summary>
    internal static bool OnlyStoredOrBudget(IEnumerable<Sent> sent)
    {
        var (posting, acceptance) = PublishedBudgetGate.BudgetReasons();
        var all = sent.ToArray();
        var inFlight = $"{PublishedBudgetGate.InFlightStatus()} {PublishedBudgetGate.InFlightType()}";
        var tail = Math.Max(0, all.Length - (PublishedBudgetGate.Waiters() + 1));
        return all.Count(s => Kind(s) == inFlight) <= tail
            && all.All(s => Kind(s) is "201" or "200" || Kind(s) == "403 " + posting || Kind(s) == "403 " + acceptance || Kind(s) == inFlight);
    }

    /// <summary>An accept of <paramref name="postId"/> by the asker, as the route receives it.</summary>
    internal static async Task<Sent> AcceptAsync(ForumFixture forum, SceneActor asker, string postId, string? token = null, HttpClient? client = null, CancellationToken ct = default)
    {
        try
        {
            client ??= forum.Client;
            token ??= await asker.Dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct);
            using var response = await asker.Dpop.PostAsync(
                client, $"http://localhost/v1/posts/{postId}/accept", token, Encoding.UTF8.GetBytes("{}"), forum.Now, ct, contentType: "application/json");
            var body = await response.Content.ReadAsStringAsync(ct);
            return Parsed((int)response.StatusCode, body);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new Sent(500, string.Empty, string.Empty, null, string.Empty, null, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Sent Parsed(int status, string body)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body);
            string Text(string name) => json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && json.RootElement.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()! : string.Empty;
            return new Sent(status, Text("type"), Text("title"), Text("detail") is { Length: > 0 } d ? d : null, body, null, null, null, null);
        }
        catch (System.Text.Json.JsonException)
        {
            return new Sent(status, string.Empty, string.Empty, null, body, null, null, null, null);
        }
    }

    /// <summary>A thread for an asker to accept: its question by the asker (stored) and one answer by an unenrolled agent (stored); returns the answer's post id.</summary>
    internal static async Task<string> StoredThreadAsync(ForumFixture forum, ForumAgent asker, ForumAgent answerer, string board, CancellationToken ct)
    {
        var question = await StoreAsync(forum, asker.SignQuestion(board, Probe.Unrelated(), "Thread " + Probe.Unrelated(), forum.Now), ct);
        var answer = await StoreAsync(forum, answerer.SignAnswer(board, Probe.Unrelated(), question.PostId, forum.Now), ct);
        return answer.PostId;
    }

    /// <summary>
    /// Spends <paramref name="count"/> of an asker's acceptance budget without the route: one acceptance recorded with the host's own use case, then its event
    /// cloned (admin role, throwaway database) <c>count - 1</c> times with new event ids at the clock's now, so a budget of 196 costs one thread.
    /// </summary>
    internal static async Task SeedAcceptsAsync(ForumFixture forum, ForumAgent asker, ForumAgent answerer, string board, int count, CancellationToken ct)
    {
        if (count <= 0) return;
        var answer = await StoredThreadAsync(forum, asker, answerer, board, ct);
        var question = await QuestionOfAsync(forum, answer, ct);
        await forum.AppendStoredAcceptanceAsync(question, answer, asker.AgentId, ct);
        if (count == 1) return;

        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var clone = new NpgsqlCommand(
            "INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) " +
            "SELECT 'clone-' || gen_random_uuid()::text, e.event_type, e.aggregate_id, e.actor_id, e.payload, @now " +
            "FROM events e, generate_series(1, @n) WHERE e.seq = (SELECT max(seq) FROM events WHERE event_type = 'answer.accepted' AND payload->>'accepted_by' = @a);",
            admin);
        clone.Parameters.AddWithValue("now", forum.Now);
        clone.Parameters.AddWithValue("n", count - 1);
        clone.Parameters.AddWithValue("a", asker.AgentId);
        await clone.ExecuteNonQueryAsync(ct);
    }

    private static async Task<string> QuestionOfAsync(ForumFixture forum, string answerId, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT payload->>'parent' FROM events WHERE event_type = 'post.accepted' AND payload->>'post_id' = @id;", admin);
        command.Parameters.AddWithValue("id", answerId);
        return (string)(await command.ExecuteScalarAsync(ct))!;
    }

    /// <summary>The host's reader of the whole log has been called this many times, for a fact that wants to say "nothing was read".</summary>
    internal static int ReadsOf(IServiceProvider services) => AgentWriteGateForumFixture.StoreOf(services).ForwardReads;

    internal static IServiceProvider ServicesOf(ForumFixture forum) => forum.Services;
}
