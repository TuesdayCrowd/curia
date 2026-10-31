using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

using Scene = Curia.Api.Tests.AcceptedAnswerScene;

namespace Curia.Api.Tests;

/// <summary>
/// PR 3 (errata G50, R7.30), Task 0's probes RLW3 to RLW5 against a real Forum on real Postgres: the flag listing of a post another agent wrote is refused
/// with the reason of a route that has found the post, in its own title, while no id changes the status it had, and the revision route keeps the shared
/// reason and its clause.
///
/// <para><b>Oracles.</b> The existence partition is the <c>events</c> table's, by SQL from the connection string the fixture holds (a post id that a
/// <c>post.accepted</c> event names is a post: <c>403</c> to a caller who is not its author; none is not: <c>404</c>), never the listing's own answer.
/// The reason and the title are read from G50 (<see cref="PublishedReadersLastWords"/>). The vote is appended with <c>AppendStoredPostAsync</c> (the
/// log holds it without the tier it would need), the withhold goes through the moderator's writer, and every other post is made through the API.</para>
///
/// <para><b>RLW5 is green on the seam tree</b> (a control: the revision route's words do not move) and red under FRLW1 to FRLW3.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OwnResourceFoundEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";
    private const string NoSuchPost = "01M6FFFF000000000000000000";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Reply(HttpStatusCode Status, string Raw)
    {
        internal string Field(string name) =>
            Raw.StartsWith('{') && JsonDocument.Parse(Raw).RootElement.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
    }

    /// <summary>A DPoP-bound <c>GET /v1/posts/{id}/flags</c> by <paramref name="actor"/>.</summary>
    private async Task<Reply> ListFlagsAsync(SceneActor actor, string id)
    {
        var token = await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct);
        var url = $"http://localhost/v1/posts/{id}/flags";
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url).PathAndQuery);
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
        request.Headers.Add("DPoP", actor.Dpop.Proof("GET", url, forum.Now, token, nonce: null));
        using var response = await forum.Client.SendAsync(request, Ct);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<Reply> ReviseAsync(SceneActor actor, string board, string parent, string prev)
    {
        using var response = await actor.Dpop.PostAsync(
            forum.Client, PostsUrl, await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
            actor.Agent.SignRevision(board, "A body that is not its author's to give.", parent, prev, forum.Now), forum.Now, Ct);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    /// <summary>RLW3. A non-author's listing of a served post is <c>403</c> in R7.30's reason and title with <c>post=&lt;id&gt;</c>; an invented id is <c>404</c> and the author reads <c>200</c> in the same fact.</summary>
    [Fact]
    public async Task R7_30_RLW3_TheListingOfAPostThatExistsSaysSo()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var stranger = await Scene.EnrolAsync(forum, "stranger", Ct);

        var refused = await ListFlagsAsync(stranger, scene.Question.PostId);
        var invented = await ListFlagsAsync(stranger, NoSuchPost);
        var own = await ListFlagsAsync(scene.Asker, scene.Question.PostId);

        var found = PublishedReadersLastWords.OwnResourceFound();
        var problems = new List<string>();
        if (refused.Status != HttpStatusCode.Forbidden) problems.Add($"a post that exists: Expected: 403 Actual: {(int)refused.Status} {refused.Raw}");
        if (!refused.Field("detail").StartsWith(found, StringComparison.Ordinal))
            problems.Add($"Expected: reason {found} Actual: detail \"{refused.Field("detail")}\"");
        if (!refused.Field("detail").Contains($"post={scene.Question.PostId}", StringComparison.Ordinal)) problems.Add("the detail does not name the post");
        if (refused.Field("title") != PublishedReadersLastWords.FoundTitle()) problems.Add($"Expected: title \"{PublishedReadersLastWords.FoundTitle()}\" Actual: \"{refused.Field("title")}\"");
        if (invented.Status != HttpStatusCode.NotFound || invented.Field("type") != "curia/post/not-found") problems.Add($"an invented id: Expected: 404 curia/post/not-found Actual: {(int)invented.Status} {invented.Raw}");
        if (own.Status != HttpStatusCode.OK) problems.Add($"the author: Expected: 200 Actual: {(int)own.Status} {own.Raw}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// RLW4 (amended 2026-10-30, M1). No existence bit moves: by a non-author T0 the status of the listing is the events table's membership of the id, <c>403</c>
    /// for a served post, a withheld post, a vote's id and a comment, and <c>404</c> for an id no post bears and a malformed one (never <c>400</c>); the
    /// <c>403</c>s carry R7.30's reason and the <c>404</c>s the listing's own type; the author of the vote and of the comment read <c>200</c> of their own.
    /// </summary>
    [Fact]
    public async Task R7_30_RLW4_NoExistenceBitMoves()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var stranger = await Scene.EnrolAsync(forum, "stranger", Ct);
        await forum.WithholdAsync(scene.Answer2.PostId, Ct);
        var (voteId, _) = await forum.AppendStoredPostAsync(scene.Asker.Agent.SignVote(scene.Board, scene.Answer1.Digest, forum.Now), Ct);

        var cases = new (string Name, string Id)[]
        {
            ("a served post", scene.Answer1.PostId), ("a withheld post", scene.Answer2.PostId), ("a vote's id", voteId), ("a comment", scene.Comment.PostId),
            ("an id no post bears", NoSuchPost), ("a malformed id", "not-a-post"),
        };

        var found = PublishedReadersLastWords.OwnResourceFound();
        var problems = new List<string>();
        foreach (var (name, id) in cases)
        {
            var isPost = await forum.CountEventsAsync("post.accepted", id, Ct) > 0;
            var reply = await ListFlagsAsync(stranger, id);
            var expected = isPost ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound;
            if (reply.Status != expected) problems.Add($"{name}: Expected: {(int)expected} (the events table holds {(isPost ? "a" : "no")} post.accepted) Actual: {(int)reply.Status}");
            else if (isPost && !reply.Field("detail").StartsWith(found, StringComparison.Ordinal)) problems.Add($"{name}: Expected: reason {found} Actual: \"{reply.Field("detail")}\"");
            else if (!isPost && reply.Field("type") != "curia/post/not-found") problems.Add($"{name}: Expected: type curia/post/not-found Actual: {reply.Field("type")}");
        }

        foreach (var (name, id) in new[] { ("the vote's own author", voteId), ("the comment's own author", scene.Comment.PostId) })
        {
            var own = await ListFlagsAsync(scene.Asker, id);
            if (own.Status != HttpStatusCode.OK) problems.Add($"{name}: Expected: 200 Actual: {(int)own.Status}");
        }

        // The single anonymous read is read beside it and is not changed: a withheld post is 404 curia/posts/withheld, a vote's id curia/posts/not-found.
        using var anonymousVote = await forum.Client.GetAsync(new Uri($"/v1/posts/{voteId}", UriKind.Relative), Ct);
        if (anonymousVote.StatusCode != HttpStatusCode.NotFound) problems.Add($"the anonymous read of the vote: Expected: 404 Actual: {(int)anonymousVote.StatusCode}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// RLW5, a control. The revision route cannot tell a post that is another's from a <c>prev</c> that names nothing, so it keeps the shared reason and its
    /// clause: revising another author's post and revising an unknown digest are each <c>403</c>, <c>table-10/own-resource-only</c>, titled with "does not
    /// exist"; nothing is stored for either. Green on the seam tree; red under FRLW1 to FRLW3.
    /// </summary>
    [Fact]
    public async Task R7_30_RLW5_TheRevisionRouteKeepsTheSharedReasonAndItsClause()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var stranger = await Scene.EnrolAsync(forum, "stranger", Ct);
        var only = PublishedReadersLastWords.OwnResourceOnly();

        var others = await ReviseAsync(stranger, scene.Board, scene.Answer1.PostId, scene.Answer1.Digest);
        var unknown = await ReviseAsync(stranger, scene.Board, scene.Answer1.PostId, "sha256:" + new string('a', 64));

        var problems = new List<string>();
        foreach (var (name, reply) in new[] { ("another's post", others), ("an unknown digest", unknown) })
        {
            if (reply.Status != HttpStatusCode.Forbidden) problems.Add($"{name}: Expected: 403 Actual: {(int)reply.Status} {reply.Raw}");
            else if (!reply.Field("detail").StartsWith(only, StringComparison.Ordinal)) problems.Add($"{name}: Expected: reason {only} Actual: \"{reply.Field("detail")}\"");
            else if (!reply.Field("title").Contains("does not exist", StringComparison.Ordinal)) problems.Add($"{name}: the title lost its clause: \"{reply.Field("title")}\"");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
        Assert.Equal(0, await PostsByAsync(stranger.Agent.AgentId));
    }

    /// <summary>How many posts the log holds by <paramref name="author"/>, from the table by SQL.</summary>
    private async Task<long> PostsByAsync(string author)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'author' = @author", connection);
        count.Parameters.AddWithValue("author", author);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }
}
