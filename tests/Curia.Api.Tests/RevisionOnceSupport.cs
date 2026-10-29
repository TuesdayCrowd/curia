using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Domain;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// What a submission was answered with, as a fact reads it: the status, the problem document's members, the receipt's, and, for a request the host
/// threw out of (an unhandled exception reaches an in-process client as an exception, where the deployed host answers 500), <see cref="Threw"/>.
/// </summary>
internal sealed record Sent(int Status, string Type, string Title, string? Detail, string Body, string? ContentType, string? PostId, string? Digest, string? Threw)
{
    /// <summary>The words a failing assertion prints: status and type, or the status and what the host threw.</summary>
    internal string Says => Threw is not null ? $"500 (threw {Threw})" : Type.Length > 0 ? $"{Status} {Type}" : $"{Status}";
}

/// <summary>
/// The helpers PR 1's route facts share (errata G45): sending a signed wire as an enrolled agent, releasing several together, and the oracles the
/// plan's ruling 13 names, each read from the <c>events</c> table or from a different route than the request under test, never from the response of
/// that request.
/// </summary>
internal static class RevisionProbe
{
    internal const string TokenEndpoint = "http://localhost/oauth/token";
    internal const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>
    /// Asserts two descriptions of what happened are equal, and says both in full when they are not: xUnit shortens a long string's difference, and a
    /// red text is what these probes are written to leave behind.
    /// </summary>
    internal static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    internal static void Said(IEnumerable<string> expected, IEnumerable<string> actual) =>
        Said(string.Join(" | ", expected), string.Join(" | ", actual));

    internal static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    internal static string Board() => "board-" + Suffix();

    /// <summary>Text with nothing in common with any other call's, so the near-duplicate check (R8.18) refuses none of them.</summary>
    internal static string Unrelated() => string.Join(' ', Enumerable.Range(0, 4).Select(_ => Guid.NewGuid().ToString("N")));

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A request the host threw out of is recorded as 500 with the exception's type: the deployed host answers 500, and an in-process client sees the exception instead.")]
    internal static async Task<Sent> SendAsync(ForumFixture forum, SceneActor actor, byte[] wire, string? token = null, CancellationToken cancellationToken = default)
    {
        try
        {
            token ??= await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, cancellationToken);
            using var response = await actor.Dpop.PostAsync(forum.Client, PostsUrl, token, wire, forum.Now, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return Parse((int)response.StatusCode, body, response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new Sent(0, string.Empty, string.Empty, null, string.Empty, null, null, null, "cancelled");
        }
        catch (Exception ex)
        {
            return new Sent(500, string.Empty, string.Empty, null, string.Empty, null, null, null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Sent Parse(int status, string body, string? mediaType)
    {
        string Text(JsonElement root, string name) =>
            root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var detail = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return new Sent(status, Text(root, "type"), Text(root, "title"), detail, body, mediaType,
                Text(root, "post_id") is { Length: > 0 } id ? id : null, Text(root, "digest") is { Length: > 0 } digest ? digest : null, null);
        }
        catch (JsonException)
        {
            return new Sent(status, string.Empty, string.Empty, null, body, mediaType, null, null, null);
        }
    }

    internal static async Task<ScenePost> PostAsync(ForumFixture forum, SceneActor actor, byte[] wire, CancellationToken cancellationToken)
    {
        var sent = await SendAsync(forum, actor, wire, cancellationToken: cancellationToken);
        Assert.True(sent.Status == (int)HttpStatusCode.Created, $"a post that must be accepted was answered {sent.Says}: {sent.Body}");
        return new ScenePost(sent.PostId!, sent.Digest!);
    }

    internal static Task<ScenePost> AskAsync(ForumFixture forum, SceneActor actor, string board, CancellationToken cancellationToken) =>
        PostAsync(forum, actor, actor.Agent.SignQuestion(board, Unrelated(), "About " + Unrelated(), forum.Now), cancellationToken);

    internal static Task<Sent> ReviseAsync(ForumFixture forum, SceneActor actor, string board, ScenePost of, string? body = null, string? token = null, CancellationToken cancellationToken = default) =>
        SendAsync(forum, actor, actor.Agent.SignRevision(board, body ?? "Corrected " + Unrelated(), of.PostId, of.Digest, forum.Now), token, cancellationToken);

    /// <summary>All of <paramref name="sends"/> started at once from one signal.</summary>
    internal static async Task<Sent[]> TogetherAsync(IEnumerable<Func<Task<Sent>>> sends)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = sends.Select(send => Task.Run(async () =>
        {
            await start.Task;
            return await send();
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(running);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every caller passes a constant statement and binds its values as parameters.")]
    private static async Task<long> CountAsync(ForumFixture forum, string sql, params (string Name, object Value)[] parameters)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand(sql, admin);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    /// <summary>The revisions the table holds whose signed <c>prev</c> is <paramref name="digest"/>: counted from the table, whatever the response said.</summary>
    internal static Task<long> RevisionsOfAsync(ForumFixture forum, string digest) =>
        CountAsync(forum,
            "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'kind' = 'revision' AND (payload->>'canonical')::jsonb->>'prev' = @digest;",
            ("digest", digest));

    /// <summary>The <c>post.accepted</c> events whose digest is <paramref name="digest"/>.</summary>
    internal static Task<long> EventsWithDigestAsync(ForumFixture forum, string digest) =>
        CountAsync(forum, "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'digest' = @digest;", ("digest", digest));

    /// <summary>The posts the table holds whose author is <paramref name="agentId"/>.</summary>
    internal static Task<long> PostsByAsync(ForumFixture forum, string agentId) =>
        CountAsync(forum, "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'author' = @author;", ("author", agentId));

    /// <summary>The <c>post.accepted</c> events on <paramref name="board"/>.</summary>
    internal static Task<long> PostsOnAsync(ForumFixture forum, string board) =>
        CountAsync(forum, "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'board' = @board;", ("board", board));

    /// <summary>The post ids of the <c>post.accepted</c> events that carry <paramref name="digest"/>, in log order.</summary>
    internal static async Task<string[]> PostIdsWithDigestAsync(ForumFixture forum, string digest)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT payload->>'post_id' FROM events WHERE event_type = 'post.accepted' AND payload->>'digest' = @digest ORDER BY seq;", admin);
        command.Parameters.AddWithValue("digest", digest);
        var ids = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetString(0));
        return [.. ids];
    }

    /// <summary>The <c>superseded_by</c> a single read of <paramref name="postId"/> serves, as (digest, post id or null), in the order served.</summary>
    internal static async Task<(string Digest, string? PostId)[]> SupersededByAsync(ForumFixture forum, string postId, CancellationToken cancellationToken)
    {
        var raw = await forum.Client.GetStringAsync(new Uri($"/v1/posts/{postId}", UriKind.Relative), cancellationToken);
        using var json = JsonDocument.Parse(raw);
        var list = json.RootElement.GetProperty("provenance").GetProperty("superseded_by");
        return [.. list.EnumerateArray().Select(e => (e.GetProperty("digest").GetString()!, e.TryGetProperty("post_id", out var id) ? id.GetString() : null))];
    }

    /// <summary>The whole log, read through the host's reader: a replay (R11.9) starts here.</summary>
    internal static async Task<IReadOnlyList<AppendedEvent>> ReadLogAsync(ForumFixture forum, CancellationToken cancellationToken)
    {
        var read = await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(cancellationToken);
        return read.Match(events => events, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
    }

    /// <summary>The ways a refusal differs from the shape R8.77 and R8.78 give it (problem media type, type, title, status, exact detail, and nothing an author wrote); empty when it does not.</summary>
    internal static string ShapeProblems(Sent sent, Curia.Domain.Primitives.Error expected, int status, params string[] sentinels)
    {
        if (sent.Threw is not null) return "threw " + sent.Threw;
        var problems = new List<string>();
        if (sent.Status != status) problems.Add($"status {sent.Status}");
        if (!string.Equals(sent.ContentType, "application/problem+json", StringComparison.Ordinal)) problems.Add($"content type {sent.ContentType}");
        if (sent.Type != expected.Type) problems.Add($"type {sent.Type}");
        if (sent.Title != expected.Title) problems.Add($"title {sent.Title}");
        if (sent.Detail != expected.Detail) problems.Add($"detail {sent.Detail}");
        try
        {
            using var json = JsonDocument.Parse(sent.Body);
            if (!json.RootElement.TryGetProperty("status", out var s) || s.ValueKind != JsonValueKind.Number || s.GetInt32() != status) problems.Add("no status member");
        }
        catch (JsonException)
        {
            problems.Add("body is not JSON");
        }

        problems.AddRange(sentinels.Where(x => sent.Body.Contains(x, StringComparison.Ordinal)).Select(_ => "the body echoes what the author wrote"));
        return string.Join(", ", problems);
    }
}
