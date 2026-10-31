using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// The helpers errata G51's route facts share (the D92 PR): a signed post of each discussion kind for an agent that may send it, the members a receipt or a
/// search response carries, and the oracles, which are the events table and <c>post_embeddings</c> counted from the fixture's own connection string and never a
/// response's claim about itself.
/// </summary>
internal static class IndexLag
{
    /// <summary>The discussion kinds a post can be sent as (the ones the vector index holds), by the word a theory row uses.</summary>
    internal static readonly string[] Kinds = ["question", "answer", "comment", "revision"];

    /// <summary>A post ready to send: the agent that signed it, the wire and the board.</summary>
    internal sealed record Fresh(SceneActor Actor, byte[] Wire, string Board);

    /// <summary>A signed post of <paramref name="kind"/> whose text has nothing in common with any other, by an agent that may send it (an answer needs a T1 agent, a revision a post of its own to revise).</summary>
    internal static async Task<Fresh> FreshAsync(ForumFixture forum, string kind, CancellationToken ct)
    {
        var board = Probe.Board();
        switch (kind)
        {
            case "question":
            {
                var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-q", ct);
                return new Fresh(actor, actor.Agent.SignQuestion(board, Probe.Unrelated(), "About " + Probe.Unrelated(), forum.Now), board);
            }

            case "comment":
            {
                var parent = await BudgetBursts.StoredQuestionAsync(forum, board, ct);
                var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-c", ct);
                return new Fresh(actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), board);
            }

            case "answer":
            {
                var parent = await BudgetBursts.StoredQuestionAsync(forum, board, ct);
                var actor = await AcceptedAnswerScene.T1Async(forum, board, ct);
                return new Fresh(actor, actor.Agent.SignAnswer(board, Probe.Unrelated(), parent.PostId, forum.Now), board);
            }

            case "revision":
            {
                var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lag-r", ct);
                var original = await Probe.AskAsync(forum, actor, board, ct);
                return new Fresh(actor, actor.Agent.SignRevision(board, "Corrected " + Probe.Unrelated(), original.PostId, original.Digest, forum.Now), board);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "a discussion kind");
        }
    }

    /// <summary>The raw JSON text of the member <paramref name="name"/> of a response body, or <c>absent</c>.</summary>
    internal static string Member(string body, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty(name, out var value) ? value.GetRawText() : "absent";
        }
        catch (JsonException)
        {
            return "absent";
        }
    }

    /// <summary>The raw text of the member <paramref name="name"/> of the receipt's <c>budget</c> member, or <c>absent</c>.</summary>
    internal static string BudgetMember(string body, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("budget", out var budget)
                && budget.ValueKind == JsonValueKind.Object && budget.TryGetProperty(name, out var value) ? value.GetRawText() : "absent";
        }
        catch (JsonException)
        {
            return "absent";
        }
    }

    /// <summary>The status and the receipt's <c>vector_index</c> member as a word: <c>201 pending</c>, <c>201 member absent</c>, <c>500 curia/...</c>.</summary>
    internal static string Says(Sent sent) =>
        sent.Threw is not null ? sent.Says : sent.Status == 201 ? $"201 {(Member(sent.Body, "vector_index") is var m && m == "absent" ? "member vector_index absent" : m.Trim('"'))}" : sent.Says;

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every caller passes a constant statement and binds its values as parameters.")]
    private static async Task<T> ScalarAsync<T>(ForumFixture forum, string sql, params (string Name, object Value)[] parameters)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand(sql, admin);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)Convert.ChangeType(await command.ExecuteScalarAsync() ?? default(T)!, typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>The rows <c>post_embeddings</c> holds for <paramref name="digest"/>.</summary>
    internal static Task<long> RowsAsync(ForumFixture forum, string digest) =>
        ScalarAsync<long>(forum, "SELECT count(*) FROM post_embeddings WHERE digest = @digest;", ("digest", digest));

    /// <summary>The rows <c>post_embeddings</c> holds.</summary>
    internal static Task<long> AllRowsAsync(ForumFixture forum) => ScalarAsync<long>(forum, "SELECT count(*) FROM post_embeddings;");

    /// <summary>The digests of the <c>post.accepted</c> events <paramref name="agentId"/> wrote, in log order.</summary>
    internal static async Task<string[]> DigestsOfAsync(ForumFixture forum, string agentId)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT payload->>'digest' FROM events WHERE event_type = 'post.accepted' AND payload->>'author' = @a ORDER BY seq;", admin);
        command.Parameters.AddWithValue("a", agentId);
        var digests = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) digests.Add(reader.GetString(0));
        return [.. digests];
    }

    /// <summary>The post ids of the <c>post.accepted</c> events <paramref name="agentId"/> wrote, in log order.</summary>
    internal static async Task<string[]> PostIdsOfAsync(ForumFixture forum, string agentId)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT payload->>'post_id' FROM events WHERE event_type = 'post.accepted' AND payload->>'author' = @a ORDER BY seq;", admin);
        command.Parameters.AddWithValue("a", agentId);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return [.. ids];
    }

    /// <summary><c>GET /v1/search?{query}</c> as a document.</summary>
    internal static async Task<JsonDocument> SearchAsync(HttpClient client, string query, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri($"/v1/search?{query}", UriKind.Relative), ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>The raw text of <c>vector_pending</c> in a search document, or <c>absent</c>.</summary>
    internal static string Pending(JsonDocument search) =>
        search.RootElement.ValueKind == JsonValueKind.Object && search.RootElement.TryGetProperty("vector_pending", out var v) ? v.GetRawText() : "absent";

    /// <summary>The post ids of the results of a search document, in order.</summary>
    internal static string[] Ids(JsonDocument search) =>
        search.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array
            ? [.. results.EnumerateArray().Select(r => r.GetProperty("post").GetProperty("post_id").GetString()!)]
            : [];

    /// <summary>Whether the first result of <paramref name="postId"/> in a search document (asked with <c>why=true</c>) was ranked by the vector channel.</summary>
    internal static bool HasVectorTerm(JsonDocument search, string postId) =>
        search.RootElement.TryGetProperty("results", out var results)
        && results.EnumerateArray().Any(r => r.GetProperty("post").GetProperty("post_id").GetString() == postId
            && r.TryGetProperty("why_ranked", out var why) && why.TryGetProperty("vector", out var vector) && vector.ValueKind == JsonValueKind.Object);

    /// <summary>
    /// Moves a timer-capable clock forward <paramref name="steps"/> steps of one second, a short real pause after each so that a loop woken by the advance has run, and
    /// stops early where <paramref name="done"/> holds. The pause is a scheduling courtesy and is never what a fact asserts.
    /// </summary>
    internal static async Task AdvanceUntilAsync(TimerManualTimeProvider clock, Func<Task<bool>> done, int steps, CancellationToken ct)
    {
        for (var i = 0; i < steps; i++)
        {
            if (await done()) return;
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(120, ct);
        }
    }

    /// <summary>Polls <paramref name="condition"/> every 50 ms until it holds or <paramref name="within"/> has passed.</summary>
    internal static async Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan within, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50, ct);
        }

        return condition();
    }
}
