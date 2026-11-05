using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// The helpers PR 6's route facts share (errata G62): a stored discussion post on a board of the test's own, a GET read as status, text and JSON, and the member
/// <c>unmatched_filters</c> read as the list it is, or as <c>(absent)</c> where the response has none, so that "absent" is never read as "none".
/// </summary>
internal static class NameProbe
{
    internal const string TokenEndpoint = "http://localhost/oauth/token";

    internal sealed record NameReply(HttpStatusCode Status, string Body, JsonElement Json)
    {
        internal string Says => $"{(int)Status}{(Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? " " + t.GetString() : string.Empty)}";

        internal int Results => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array ? r.GetArrayLength() : -1;

        /// <summary>The names in <c>unmatched_filters</c>, comma-joined and bracketed, or <c>(absent)</c> / <c>(not an array)</c>.</summary>
        internal string Unmatched
        {
            get
            {
                if (Json.ValueKind != JsonValueKind.Object || !Json.TryGetProperty("unmatched_filters", out var member)) return "(absent)";
                return member.ValueKind == JsonValueKind.Array ? "[" + string.Join(",", member.EnumerateArray().Select(e => e.ToString())) + "]" : "(not an array)";
            }
        }
    }

    /// <summary>A board name of this test's own: lower case, so that its upper-case twin (<see cref="Upper"/>) is a spelling no post bears.</summary>
    internal static string Board() => "nmn-" + Guid.NewGuid().ToString("N")[..8];

    internal static string Upper(string board) => board.ToUpperInvariant();

    internal static async Task<NameReply> GetAsync(HttpClient client, string pathAndQuery, string? token = null, SceneActor? actor = null, ForumFixture? forum = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        if (actor is not null && token is not null && forum is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
            var bare = pathAndQuery.Split('?')[0];
            request.Headers.Add("DPoP", actor.Dpop.Proof("GET", "http://localhost" + bare, forum.Now, token, nonce: null));
        }

        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        JsonElement json = default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            json = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not JSON: no members.
        }

        return new NameReply(response.StatusCode, body, json);
    }

    internal static Task<NameReply> SearchAsync(HttpClient client, string query, CancellationToken ct) =>
        GetAsync(client, "/v1/search" + (query.Length > 0 ? "?" + query : string.Empty), ct: ct);

    internal static async Task<NameReply> InboxAsync(ForumFixture forum, HttpClient client, SceneActor actor, string query, CancellationToken ct)
    {
        var token = await actor.Dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct);
        return await GetAsync(client, "/v1/inbox" + (query.Length > 0 ? "?" + query : string.Empty), token, actor, forum, ct);
    }

    /// <summary>A question stored on <paramref name="board"/> straight into the log (no route and no budget), by an author of the test's own; the author's identifier and the post id.</summary>
    internal static async Task<(string AuthorId, ScenePost Post)> StoreQuestionAsync(ForumFixture forum, string board, CancellationToken ct)
    {
        var author = ForumAgent.Create("https://agents.example/nmn-author-" + Guid.NewGuid().ToString("N")[..8], "nmn");
        var stored = await BudgetBursts.StoreAsync(forum, author.SignQuestion(board, RevisionProbe.Unrelated(), "About " + RevisionProbe.Unrelated(), forum.Now), ct);
        return (author.AgentId, stored);
    }

    /// <summary>A comment stored under <paramref name="parent"/> on <paramref name="board"/>.</summary>
    internal static async Task<(string AuthorId, ScenePost Post)> StoreCommentAsync(ForumFixture forum, string board, string parentId, CancellationToken ct)
    {
        var author = ForumAgent.Create("https://agents.example/nmn-commenter-" + Guid.NewGuid().ToString("N")[..8], "nmn");
        var stored = await BudgetBursts.StoreAsync(forum, author.Sign(PostKind.Comment, board, RevisionProbe.Unrelated(), null, parentId, forum.Now), ct);
        return (author.AgentId, stored);
    }

    /// <summary>The slug of an agent identifier: its last path segment, what a caller typed for <c>--author</c> instead of the identifier.</summary>
    internal static string Slug(string agentId) => agentId[(agentId.LastIndexOf('/') + 1)..];

    /// <summary>How many posts <c>GET /v1/boards/{board}/posts</c> lists, or -1 when it did not answer a list.</summary>
    internal static async Task<int> ListedAsync(HttpClient client, string board, CancellationToken ct)
    {
        var reply = await GetAsync(client, "/v1/boards/" + Uri.EscapeDataString(board) + "/posts", ct: ct);
        return reply.Status == HttpStatusCode.OK && reply.Json.ValueKind == JsonValueKind.Array ? reply.Json.GetArrayLength() : -1;
    }

    /// <summary>Several lines as one text, for a red message.</summary>
    internal static void Said(IEnumerable<string> expected, IEnumerable<string> actual) => RevisionProbe.Said(expected, actual);

    internal static void Said(string expected, string actual) => RevisionProbe.Said(expected, actual);
}
