using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Curia.Canon.Json;
using Curia.OperatorTool;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

using Posted = Curia.Api.Tests.ScenePost;
using Scene = Curia.Api.Tests.AcceptedAnswerScene;

namespace Curia.Api.Tests;

/// <summary>
/// R9.41 (errata G40): <c>provenance.accepted_answer</c>, where it is an object, carries <c>superseded_by</c> always: the answer's revisions as R9.32
/// lists them. <b>Oracles.</b> The revisions the test signed and posted (their receipts), the <c>events</c> table by SQL (the revision events whose signed
/// <c>prev</c> is the answer's digest), the member's name parsed from the errata, and the raw response text. The index's report of itself is never read.
/// A negative case has a control.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptedAnswerSupersededEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string BatchUrl = "/v1/posts/batch";

    private static readonly string LogKeyPem = LogSigningKey.GeneratePem();
    private static readonly string Member = PublishedReadersWords.R941Member();
    private static readonly string[] ElementMembers = ["digest", "post_id"];

    /// <summary>The routes that can serve a question with an accepted answer: the single read, the thread, the board, a search and the batch. (The inbox lists open questions only, and the 409 serves answers.)</summary>
    public static TheoryData<string> Routes() => ["post", "thread", "board", "search", "batch"];

    private static Dictionary<string, JsonElement> ById(IEnumerable<JsonElement> posts) =>
        posts.ToDictionary(p => p.GetProperty("post_id").GetString()!, p => p, StringComparer.Ordinal);

    private async Task<Dictionary<string, JsonElement>> ServedAsync(string route, Scene scene, CancellationToken ct)
    {
        var http = forum.Client;
        switch (route)
        {
            case "post":
                return ById(new[] { await http.GetFromJsonAsync<JsonElement>(new Uri($"/v1/posts/{scene.Question.PostId}", UriKind.Relative), ct) });
            case "thread":
                return ById((await http.GetFromJsonAsync<JsonElement>(new Uri($"/v1/threads/{scene.Question.PostId}", UriKind.Relative), ct)).EnumerateArray().Select(e => e.Clone()));
            case "board":
                return ById((await http.GetFromJsonAsync<JsonElement>(new Uri($"/v1/boards/{scene.Board}/posts", UriKind.Relative), ct)).EnumerateArray().Select(e => e.Clone()));
            case "search":
            {
                var search = await http.GetFromJsonAsync<JsonElement>(new Uri($"/v1/search?q={scene.Term}&board={scene.Board}&limit=50", UriKind.Relative), ct);
                return ById(search.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").Clone()));
            }

            case "batch":
            {
                using var response = await http.PostAsJsonAsync(BatchUrl, new { digests = new[] { scene.Question.Digest } }, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return ById(JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray()
                    .Select(i => i.GetProperty("post")).Where(p => p.ValueKind == JsonValueKind.Object).Select(p => p.Clone()));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(route), route, "not one of the routes the theory names");
        }
    }

    /// <summary>The standing answer's member as served, failing by name when the question or the member is missing.</summary>
    private static JsonElement AcceptedAnswerOf(Dictionary<string, JsonElement> served, Scene scene, string route)
    {
        Assert.True(served.TryGetValue(scene.Question.PostId, out var question), $"{route} did not serve the question");
        Assert.True(question.GetProperty("provenance").TryGetProperty("accepted_answer", out var member), "provenance.accepted_answer is absent on a question");
        return member;
    }

    private static JsonElement ListOf(JsonElement acceptedAnswer, string route)
    {
        var list = default(JsonElement);
        var present = acceptedAnswer.ValueKind == JsonValueKind.Object && acceptedAnswer.TryGetProperty(Member, out list);
        Assert.True(present, $"Expected: member \"{Member}\" Actual: missing from accepted_answer on {route}: {acceptedAnswer.GetRawText()}");
        Assert.Equal(JsonValueKind.Array, list.ValueKind);
        return list;
    }

    private async Task<HttpStatusCode> AcceptAsync(Scene scene, Posted answer, CancellationToken ct) => await scene.AcceptAsync(forum, answer, ct);

    /// <summary>A revision of <paramref name="original"/> by the answerer, who authored it, chained by the original's digest: the post it posts.</summary>
    private async Task<Posted> ReviseAsync(Scene scene, Posted original, string body, CancellationToken ct) =>
        await Scene.PostAsync(forum, scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, body, original.PostId, original.Digest, forum.Now), ct);

    /// <summary>The post ids of the revision events whose signed <c>prev</c> is <paramref name="digest"/>, in log order: the events table's own account.</summary>
    private async Task<string[]> RevisionsOfAsync(string digest, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var query = new NpgsqlCommand(
            "SELECT event_id FROM events WHERE event_type = 'post.accepted' AND payload->>'kind' = 'revision' AND ((payload->>'canonical')::jsonb)->>'prev' = @digest ORDER BY seq;", connection);
        query.Parameters.AddWithValue("digest", digest);
        var ids = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
        return [.. ids];
    }

    [Theory]
    [MemberData(nameof(Routes))]
    public async Task R9_41_TheListIsOnTheMemberOfEveryRouteThatServesAQuestion(string route)
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);

        // A null member carries none: before any acceptance the member is JSON null, not an object with a list.
        var before = AcceptedAnswerOf(await ServedAsync(route, scene, ct), scene, route);
        Assert.Equal(JsonValueKind.Null, before.ValueKind);

        await AcceptAsync(scene, scene.Answer1, ct);
        var unrevised = ListOf(AcceptedAnswerOf(await ServedAsync(route, scene, ct), scene, route), route);
        Assert.Equal(0, unrevised.GetArrayLength());

        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.", ct);
        var revised = ListOf(AcceptedAnswerOf(await ServedAsync(route, scene, ct), scene, route), route);

        var element = Assert.Single(revised.EnumerateArray());
        Assert.Equal(revision.Digest, element.GetProperty("digest").GetString());
        Assert.Equal(revision.PostId, element.GetProperty("post_id").GetString());
        Assert.All(element.EnumerateObject(), m => Assert.Contains(m.Name, ElementMembers));
    }

    [Fact]
    public async Task R9_41_RevisingTheAcceptedAnswerNamesTheRevisionOnTheQuestionAndMovesItsValidator()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        await AcceptAsync(scene, scene.Answer1, ct);

        // A signed head first, so the only thing that can move the question's tag is its accepted answer's correction.
        using (var stdout = new StringWriter())
        using (var stderr = new StringWriter())
        {
            Assert.Equal(0, await OperatorCommands.RunAsync(
                ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadLogSigner, stdout, stderr, ct, readLogSigningKeyPem: () => LogKeyPem));
        }

        async Task<HttpResponseMessage> GetAsync(string? ifNoneMatch)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/posts/{scene.Question.PostId}");
            if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
            return await forum.Client.SendAsync(request, ct);
        }

        using var first = await GetAsync(null);
        var tag = first.Headers.ETag!.ToString();
        using (var current = await GetAsync(tag))
            Assert.Equal(HttpStatusCode.NotModified, current.StatusCode);

        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.", ct);

        // The events table says which post is the revision whose signed prev is the answer's digest; the question names that post.
        Assert.Equal([revision.PostId], await RevisionsOfAsync(scene.Answer1.Digest, ct));
        using var after = await GetAsync(tag);
        var text = await after.Content.ReadAsStringAsync(ct);
        Assert.True(after.StatusCode == HttpStatusCode.OK, $"expected 200 for the question's old validator after its accepted answer was revised, got {(int)after.StatusCode}");
        Assert.NotEqual(tag, after.Headers.ETag!.ToString());

        var list = ListOf(JsonDocument.Parse(text).RootElement.GetProperty("provenance").GetProperty("accepted_answer"), "post");
        Assert.Equal([revision.PostId], list.EnumerateArray().Select(e => e.GetProperty("post_id").GetString()!).ToArray());
    }

    /// <summary>
    /// A stranger's revision and a comment naming the answer's digest correct nothing (R9.28). The Forum's door refuses a stranger's revision, so the
    /// revision is put into the log as a post accepted before the door's rule would have been (the fixture's direct append), and a comment naming the
    /// digest in its refs is posted through the route; the list stays what the answer's author signed. Green first: held by the falsification row that
    /// lists them.
    /// </summary>
    [Fact]
    public async Task R9_41_AStrangersRevisionAndACommentAreNotListed()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        await AcceptAsync(scene, scene.Answer1, ct);
        var stranger = await Scene.EnrolAsync(forum, "stranger", ct);

        // The refusal at the door, so the stored append below is what puts the revision in the log.
        using (var refused = await stranger.Dpop.PostAsync(
            forum.Client, "http://localhost/v1/posts", await stranger.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct),
            stranger.Agent.SignRevision(scene.Board, "A hostile rewrite.", scene.Answer1.PostId, scene.Answer1.Digest, forum.Now), forum.Now, ct))
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        await forum.AppendStoredPostAsync(stranger.Agent.SignRevision(scene.Board, "A hostile rewrite.", scene.Answer1.PostId, scene.Answer1.Digest, forum.Now), ct);
        var naming = new JsonValue.Array([new JsonValue.Object([new("kind", new JsonValue.String("post")), new("target", new JsonValue.String(scene.Answer1.Digest))])]);
        await Scene.PostAsync(forum, stranger, stranger.Agent.Sign(Curia.Domain.Content.PostKind.Comment, scene.Board, "This answer is wrong.", null, scene.Answer1.PostId, forum.Now, refs: naming), ct);

        // Non-vacuity: the stranger's revision is in the log and names the digest.
        Assert.Single(await RevisionsOfAsync(scene.Answer1.Digest, ct));

        // What is listed, if the Forum lists anything: this fact is about what is not, so an absent member lists nothing (the member's presence is R9.41's other facts).
        var member = AcceptedAnswerOf(await ServedAsync("post", scene, ct), scene, "post");
        Assert.Equal(JsonValueKind.Object, member.ValueKind);
        var listed = member.TryGetProperty(Member, out var list) ? list.GetArrayLength() : 0;
        Assert.Equal(0, listed);

        // Control: the answer's own envelope lists nothing either (the rule is one).
        var answer = await forum.Client.GetFromJsonAsync<JsonElement>(new Uri($"/v1/posts/{scene.Answer1.PostId}", UriKind.Relative), ct);
        Assert.Equal(0, answer.GetProperty("provenance").GetProperty("superseded_by").GetArrayLength());
    }

    [Fact]
    public async Task R9_41_TheMemberNameIsTheOneTheErratumPublishes()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        await AcceptAsync(scene, scene.Answer1, ct);

        var raw = await forum.Client.GetStringAsync(new Uri($"/v1/posts/{scene.Question.PostId}", UriKind.Relative), ct);
        var accepted = JsonDocument.Parse(raw).RootElement.GetProperty("provenance").GetProperty("accepted_answer");
        var names = accepted.EnumerateObject().Select(m => m.Name).ToArray();

        Assert.True(names.Contains(Member, StringComparer.Ordinal), $"Expected: member \"{Member}\" among {string.Join(", ", names)}");
        Assert.Equal(Member, names.Single(n => n.Contains("_by", StringComparison.Ordinal)));
    }
}
