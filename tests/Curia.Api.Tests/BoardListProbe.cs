using System.Globalization;
using System.Net;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;
using static Curia.Api.Tests.NameProbe;

namespace Curia.Api.Tests;

/// <summary>
/// The helpers PR 7's route facts share (errata G63): the listing read as the rows it holds, or as <c>(not a listing)</c> where the response is not a <c>200</c> with a <c>boards</c> array, so that an
/// absent member is never read as an empty list; posts stored straight into the log (no route, no budget, no board grammar: the seam D103's names are made through); and the two oracles that are not
/// the route under test, a <c>GROUP BY</c> over the events table and the per-board listing.
/// </summary>
internal static class BoardListProbe
{
    internal const string Route = "/v1/boards";

    internal sealed record Row(string Board, int Posts, int Authors, string Latest, JsonElement Provenance)
    {
        internal string Text => $"{Board.Replace("\n", "\\n", StringComparison.Ordinal)} {Posts} posts {Authors} authors";
    }

    internal static Task<NameReply> ListAsync(HttpClient client, string query, CancellationToken ct) =>
        GetAsync(client, Route + (query.Length > 0 ? "?" + query : string.Empty), ct: ct);

    /// <summary>The rows of a <c>200</c> listing, or null where the reply is no listing.</summary>
    internal static List<Row>? Rows(NameReply reply)
    {
        if (reply.Status != HttpStatusCode.OK || reply.Json.ValueKind != JsonValueKind.Object || !reply.Json.TryGetProperty("boards", out var boards) || boards.ValueKind != JsonValueKind.Array)
            return null;

        return [.. boards.EnumerateArray().Select(r => new Row(
            r.GetProperty("board").GetString()!,
            r.GetProperty("posts").GetInt32(),
            r.GetProperty("authors").GetInt32(),
            r.GetProperty("latest_post_at").GetString()!,
            r.GetProperty("provenance")))];
    }

    internal static string Summary(NameReply reply) => Rows(reply) is { } rows
        ? $"200, {rows.Count} rows, total {Total(reply)}{(NextCursor(reply) is null ? string.Empty : ", next_cursor")}"
        : reply.Says;

    internal static int Total(NameReply reply) =>
        reply.Json.ValueKind == JsonValueKind.Object && reply.Json.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : -1;

    internal static string? NextCursor(NameReply reply) =>
        reply.Json.ValueKind == JsonValueKind.Object && reply.Json.TryGetProperty("next_cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

    internal static long CorpusBound(NameReply reply) =>
        reply.Json.ValueKind == JsonValueKind.Object && reply.Json.TryGetProperty("corpus_bound", out var b) && b.ValueKind == JsonValueKind.Number ? b.GetInt64() : -1;

    /// <summary>A board named <paramref name="prefix"/> and a suffix of this test's own, lower case.</summary>
    internal static string Name(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    /// <summary>Stores <paramref name="count"/> questions on <paramref name="board"/>, by <paramref name="authors"/> distinct authors (round robin), and returns the post ids.</summary>
    internal static async Task<List<ScenePost>> StoreAsync(ForumFixture forum, string board, int count, int authors, CancellationToken ct)
    {
        var agents = Enumerable.Range(0, authors).Select(_ => ForumAgent.Create("https://agents.example/lob-" + Guid.NewGuid().ToString("N")[..8], "lob")).ToArray();
        var stored = new List<ScenePost>();
        for (var i = 0; i < count; i++)
            stored.Add(await BudgetBursts.StoreAsync(forum, agents[i % authors].SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), forum.Now), ct));

        return stored;
    }

    /// <summary>A vote on <paramref name="board"/> targeting <paramref name="digest"/>, stored directly.</summary>
    internal static async Task<ScenePost> StoreVoteAsync(ForumFixture forum, string board, string digest, CancellationToken ct)
    {
        var voter = ForumAgent.Create("https://agents.example/lob-voter-" + Guid.NewGuid().ToString("N")[..8], "lob");
        return await BudgetBursts.StoreAsync(forum, voter.SignVote(board, digest, forum.Now), ct);
    }

    /// <summary>The number of events in the log, from the table itself.</summary>
    internal static async Task<long> EventCountAsync(ForumFixture forum, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The oracle that is neither the route nor a projection: a <c>GROUP BY</c> over the events table for the named boards, over posts of a discussion kind (<c>question</c>, <c>answer</c>,
    /// <c>finding</c>, <c>comment</c>, <c>revision</c>), most posts first and then by name in byte order. Columns are those of <c>db/0001</c>: <c>payload</c> holds <c>board</c>, <c>author</c>, <c>kind</c>.
    /// A fixture that withholds nothing is the only caller, so no moderation event is read.
    /// </summary>
    internal static async Task<List<(string Board, int Posts, int Authors, DateTimeOffset Latest)>> SqlRowsAsync(ForumFixture forum, string[] boards, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT board, count(*), count(DISTINCT author), max(server_ts) FROM (" +
            "SELECT payload->>'board' AS board, payload->>'author' AS author, server_ts FROM events " +
            "WHERE event_type = 'post.accepted' AND payload->>'kind' IN ('question','answer','finding','comment','revision') AND payload->>'board' = ANY(@boards)) posts " +
            "GROUP BY board ORDER BY count(*) DESC, board COLLATE \"C\" ASC;", connection);
        command.Parameters.AddWithValue("boards", boards);
        var rows = new List<(string, int, int, DateTimeOffset)>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            rows.Add((reader.GetString(0), (int)reader.GetInt64(1), (int)reader.GetInt64(2), await reader.GetFieldValueAsync<DateTimeOffset>(3, ct)));

        return rows;
    }

    /// <summary>The per-board listing's rows for a name the route can address: its length, its distinct authors and its greatest <c>server_ts</c>, or null where the route did not answer a list.</summary>
    internal static async Task<(int Posts, int Authors, DateTimeOffset Latest)?> PerBoardAsync(HttpClient client, string board, CancellationToken ct)
    {
        var reply = await GetAsync(client, "/v1/boards/" + Uri.EscapeDataString(board) + "/posts", ct: ct);
        if (reply.Status != HttpStatusCode.OK || reply.Json.ValueKind != JsonValueKind.Array) return null;

        var posts = reply.Json.EnumerateArray().ToArray();
        return (posts.Length,
            posts.Select(p => p.GetProperty("provenance").GetProperty("author").GetString()).Distinct(StringComparer.Ordinal).Count(),
            posts.Length == 0 ? default : posts.Max(p => DateTimeOffset.Parse(p.GetProperty("server_ts").GetString()!, CultureInfo.InvariantCulture)));
    }

    /// <summary>A Forum host of this test's own, for a fact that needs every board it holds to be its own (an order, a total, an empty log). The caller disposes it.</summary>
    internal static async Task<PassThroughGateForumFixture> FreshAsync()
    {
        var fresh = new PassThroughGateForumFixture();
        await fresh.InitializeAsync();
        return fresh;
    }

    internal static string Describe(IEnumerable<Row> rows) => string.Join(" | ", rows.Select(r => r.Text));
}
