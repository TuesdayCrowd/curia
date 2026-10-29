using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

using Posted = Curia.Api.Tests.ScenePost;
using Scene = Curia.Api.Tests.AcceptedAnswerScene;

namespace Curia.Api.Tests;

/// <summary>
/// PR 4 (errata G44), Task 0's probes against a real Forum on real Postgres: an asker accepts a revision of an answer (R8.75), the receipt says how the
/// acceptance moved (R8.76), the question names the kind of post it names and a post's <c>accepted</c> is found through its chain root (R9.46), and the
/// tier count stays per thread (R7.25).
///
/// <para><b>Oracles.</b> The ids and digests are the receipts of the posts the test made; every count of acceptances and every <c>thread_root</c> is read
/// from the <c>events</c> table by SQL; the member names and values are parsed from the errata (<see cref="PublishedReadersWords"/>); the response text
/// is read raw, since a deserializer collapses an absent member and a null one. The fold used by the replay drill (PK20) is applied to the events table's rows,
/// not to anything the route returned. A negative case has a control: PK13, PK16, PK17, PK38 and PK39 are green on the unmodified tree by design.</para>
///
/// <para><b>Reach (plan ruling 13, Task 0 item 4).</b> A revision with a lying <c>parent</c> is stored by the route; a stranger's revision, a dangling
/// <c>prev</c> and an answer under a non-question are refused at ingest, so PK4, PK7 and PK10 are seeded with <c>AppendStoredPostAsync</c> (a post the log
/// holds although the route would not take it, the shape of a log this code did not write), and PK2, PK3, PK19, PK20 and PK34 seed the <b>acceptance</b> with
/// <c>AppendStoredAcceptanceAsync</c> (the host's own use case, none of the route's checks) so each reads the state apart from the route that records it.
/// The asker's daily posting budget is counted (ruling 14): the scene's asker has two posts in the window, and no fact here adds a third except TRW12 (which replaced PK40).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptRevisionEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>, IDisposable
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string NoSuchPost = "01M6FFFF000000000000000000";

    private readonly StubLog _stub = new();

    public void Dispose()
    {
        _stub.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    // ----- plumbing -----

    private sealed record Reply(HttpStatusCode Status, string Raw, JsonElement Body);

    private Task<Scene> SceneAsync() => Scene.BuildAsync(forum, Ct);

    private async Task<Reply> AcceptRawAsync(SceneActor actor, string postId)
    {
        using var response = await actor.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{postId}/accept",
            await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
            Encoding.UTF8.GetBytes("{}"), forum.Now, Ct, contentType: "application/json");
        var raw = await response.Content.ReadAsStringAsync(Ct);
        return new Reply(response.StatusCode, raw, raw.StartsWith('{') ? JsonDocument.Parse(raw).RootElement.Clone() : default);
    }

    /// <summary>The call, failing by name when the status is not the one expected (so a setup refusal reads as one).</summary>
    private async Task<Reply> AcceptedAsync(SceneActor actor, string postId, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var reply = await AcceptRawAsync(actor, postId);
        Assert.True(reply.Status == expected, $"Expected: {(int)expected} {expected} Actual: {(int)reply.Status} {reply.Status} {reply.Raw}");
        return reply;
    }

    /// <summary>A revision by the answerer, written as <c>curia revision</c> writes one: <c>parent</c> is the post revised and <c>prev</c> its digest.</summary>
    private async Task<Posted> ReviseAsync(Scene scene, Posted original, string body, string? parent = null) =>
        await Scene.PostAsync(forum, scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, body, parent ?? original.PostId, original.Digest, forum.Now), Ct);

    private async Task<JsonElement> GetAsync(string path)
    {
        using var response = await forum.Client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {(int)response.StatusCode} {raw}");
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private Task<JsonElement> PostJsonAsync(string id) => GetAsync($"/v1/posts/{id}");

    private static JsonElement AcceptedAnswerMember(JsonElement post)
    {
        Assert.True(post.GetProperty("provenance").TryGetProperty("accepted_answer", out var member), "provenance.accepted_answer is absent on a question");
        return member;
    }

    /// <summary>The <c>kind</c> of an <c>accepted_answer</c> object, failing by name when the member is absent (an exception from a missing key would not be an assertion).</summary>
    private static string KindOf(JsonElement member)
    {
        Assert.True(member.TryGetProperty("kind", out var kind), $"Expected: member \"kind\" Actual: missing from accepted_answer {member.GetRawText()}");
        return kind.GetString()!;
    }

    private async Task<Dictionary<string, JsonElement>> ThreadAsync(string root) =>
        (await GetAsync($"/v1/threads/{root}")).EnumerateArray().ToDictionary(p => p.GetProperty("post_id").GetString()!, p => p.Clone(), StringComparer.Ordinal);

    private static string Detail(Reply reply) => reply.Body.ValueKind == JsonValueKind.Object && reply.Body.TryGetProperty("detail", out var d) ? d.GetString() ?? string.Empty : string.Empty;

    private static string TypeOf(Reply reply) => reply.Body.ValueKind == JsonValueKind.Object && reply.Body.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;

    private async Task<string[]> AcceptedInOrderAsync(string root)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand(
            "SELECT payload->>'answer_id' FROM events WHERE event_type = 'answer.accepted' AND payload->>'thread_root' = @root ORDER BY seq;", connection);
        query.Parameters.AddWithValue("root", root);
        var ids = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) ids.Add(reader.GetString(0));
        return [.. ids];
    }

    private async Task<long> AcceptanceEventsNamingAsync(string answerId)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events WHERE event_type = 'answer.accepted' AND payload->>'answer_id' = @answer;", connection);
        count.Parameters.AddWithValue("answer", answerId);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task<string[]> ThreadRootsRecordedForAsync(string answerId)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand("SELECT payload->>'thread_root' FROM events WHERE event_type = 'answer.accepted' AND payload->>'answer_id' = @answer ORDER BY seq;", connection);
        query.Parameters.AddWithValue("answer", answerId);
        var roots = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) roots.Add(reader.GetString(0));
        return [.. roots];
    }

    private async Task<long> AllEventsAsync()
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return (long)(await query.ExecuteScalarAsync(Ct))!;
    }

    private static string RelationOf(Reply reply)
    {
        var member = PublishedReadersWords.R876Member();
        var value = default(JsonElement);
        var present = reply.Body.ValueKind == JsonValueKind.Object && reply.Body.TryGetProperty(member, out value);
        Assert.True(present, $"Expected: member \"{member}\" Actual: missing from the {(int)reply.Status} response {reply.Raw}");
        return value.ValueKind == JsonValueKind.Null ? "(null)" : value.GetString()!;
    }

    private static string ReplacedOf(Reply reply) =>
        reply.Body.GetProperty("replaced_answer").ValueKind == JsonValueKind.Null ? "(null)" : reply.Body.GetProperty("replaced_answer").GetString()!;

    // ----- PK1 to PK3c: the acceptance of a revision, and what it leaves -----

    /// <summary>PK1 (R8.75): the author revises an answer; the thread's asker accepts the revision. <c>201</c>, the receipt names the revision, and nothing was replaced.</summary>
    [Fact]
    public async Task R8_75_PK1_TheAskerAcceptsTheRevisionOfAnAnswer()
    {
        var scene = await SceneAsync();
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.");

        var reply = await AcceptedAsync(scene.Asker, revision.PostId);

        Assert.Equal(revision.PostId, reply.Body.GetProperty("post_id").GetString());
        Assert.Equal(scene.Question.PostId, reply.Body.GetProperty("thread_root").GetString());
        Assert.Equal("(null)", ReplacedOf(reply));
        Assert.Equal([revision.PostId], await AcceptedInOrderAsync(scene.Question.PostId));
    }

    /// <summary>
    /// PK2 (R9.46), the state apart from the route: an acceptance naming the revision stands (seeded through the host's use case), and the question serves
    /// <c>{digest, post_id, kind: "revision", superseded_by: []}</c>.
    /// </summary>
    [Fact]
    public async Task R9_46_PK2_TheQuestionNamesTheRevisionAndItsKind()
    {
        var scene = await SceneAsync();
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.");
        await forum.AppendStoredAcceptanceAsync(scene.Question.PostId, revision.PostId, scene.Asker.Agent.AgentId, Ct);

        var member = AcceptedAnswerMember(await PostJsonAsync(scene.Question.PostId));

        Assert.Equal(revision.Digest, member.GetProperty("digest").GetString());
        Assert.Equal(revision.PostId, member.GetProperty("post_id").GetString());
        Assert.True(member.TryGetProperty("kind", out var kind), $"Expected: member \"kind\" Actual: missing from accepted_answer {member.GetRawText()}");
        Assert.Equal("revision", kind.GetString());
        Assert.Equal(0, member.GetProperty("superseded_by").GetArrayLength());
    }

    /// <summary>PK3 (R9.46), the state apart from the route: <c>accepted</c> is true on the revision the question names and false on the original.</summary>
    [Fact]
    public async Task R9_46_PK3_TheRevisionReadsAcceptedAndTheOriginalDoesNot()
    {
        var scene = await SceneAsync();
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.");
        await forum.AppendStoredAcceptanceAsync(scene.Question.PostId, revision.PostId, scene.Asker.Agent.AgentId, Ct);

        var revisionRead = await PostJsonAsync(revision.PostId);
        var originalRead = await PostJsonAsync(scene.Answer1.PostId);

        Assert.True(revisionRead.GetProperty("accepted").GetBoolean(), "accepted is false on the revision the question names");
        Assert.False(originalRead.GetProperty("accepted").GetBoolean(), "accepted is true on the original, which the question no longer names");
    }

    /// <summary>
    /// PK3b (R8.75, R9.46): a revision whose <c>parent</c> names another question (hand-signed; the route stores it, Task 0 item 4). The asker of the true
    /// question accepts it: <c>201</c>, recorded under the chain root's thread (the events table's <c>thread_root</c> is the true question), and the revision
    /// reads accepted while the original does not. (The plan's second half, a revision with no <c>parent</c>, is removed: it is refused, and PK39 holds that.)
    /// </summary>
    [Fact]
    public async Task R8_75_PK3b_ARevisionWhoseParentNamesAnotherQuestionIsAcceptedUnderItsTrueThread()
    {
        var scene = await SceneAsync();
        var stranger = await Scene.EnrolAsync(forum, "stranger", Ct);
        var other = await Scene.PostAsync(forum, stranger, stranger.Agent.SignQuestion(scene.Board, "Why does the finch sing at night " + Suffix(), "Night singing " + Suffix(), forum.Now), Ct);
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.", parent: other.PostId);

        await AcceptedAsync(scene.Asker, revision.PostId);

        Assert.Equal([scene.Question.PostId], await ThreadRootsRecordedForAsync(revision.PostId));
        Assert.True((await PostJsonAsync(revision.PostId)).GetProperty("accepted").GetBoolean());
        Assert.False((await PostJsonAsync(scene.Answer1.PostId)).GetProperty("accepted").GetBoolean());
    }

    /// <summary>PK3c (R8.75): the same with a revision whose <c>parent</c> names no post at all (stored; it lists in no thread).</summary>
    [Fact]
    public async Task R8_75_PK3c_ARevisionWhoseParentNamesNoPostIsAcceptedUnderItsTrueThread()
    {
        var scene = await SceneAsync();
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.", parent: NoSuchPost);

        await AcceptedAsync(scene.Asker, revision.PostId);

        Assert.Equal([scene.Question.PostId], await ThreadRootsRecordedForAsync(revision.PostId));
        Assert.True((await PostJsonAsync(revision.PostId)).GetProperty("accepted").GetBoolean());
    }

    // ----- PK4 to PK7, PK10: what a chain may end at -----

    /// <summary>
    /// PK4 (R8.75), seeded (ruling 13): a revision of the answer by a stranger, stored although ingest would refuse it. The walk from it ends at the revision
    /// itself (not a correction: another author), so the asker's acceptance is refused <c>400</c> with the detail <c>post=&lt;id&gt; chain=revision kind=revision</c>,
    /// and nothing is stored. The detail is compared whole, not searched, so an echo of author text would fail it.
    /// </summary>
    [Fact]
    public async Task R8_75_PK4_AStrangersRevisionOfTheAnswerIsRefusedNamingTheChainRoot()
    {
        var scene = await SceneAsync();
        var stranger = await Scene.EnrolAsync(forum, "stranger", Ct);
        var (id, _) = await forum.AppendStoredPostAsync(stranger.Agent.SignRevision(scene.Board, "A stranger's rewrite.", scene.Answer1.PostId, scene.Answer1.Digest, forum.Now), Ct);

        var reply = await AcceptRawAsync(scene.Asker, id);

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal("curia/accept/not-an-answer", TypeOf(reply));
        Assert.Equal($"post={id} chain=revision kind=revision", Detail(reply));
        Assert.Equal(0, await AcceptanceEventsNamingAsync(id));
    }

    /// <summary>PK4's live control (green now): a stranger who tries to <i>write</i> that revision through the route is refused <c>403</c> own-resource, and nothing is stored.</summary>
    [Fact]
    public async Task R8_75_PK4_TheRouteRefusesAStrangersRevisionAtIngest()
    {
        var scene = await SceneAsync();
        var stranger = await Scene.EnrolAsync(forum, "stranger", Ct);
        var before = await AllEventsAsync();

        using var response = await stranger.Dpop.PostAsync(
            forum.Client, "http://localhost/v1/posts", await stranger.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
            stranger.Agent.SignRevision(scene.Board, "A stranger's rewrite.", scene.Answer1.PostId, scene.Answer1.Digest, forum.Now), forum.Now, Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("own-resource-only", raw, StringComparison.Ordinal);
        Assert.Equal(before, await AllEventsAsync());
    }

    /// <summary>PK5 (R8.75): a revision of a comment (the answerer's own) walks to the comment; the detail is <c>post=&lt;id&gt; chain=comment kind=revision</c>.</summary>
    [Fact]
    public async Task R8_75_PK5_ARevisionOfACommentIsRefusedNamingTheComment()
    {
        var scene = await SceneAsync();
        var comment = await Scene.PostAsync(forum, scene.Answerer, scene.Answerer.Agent.Sign(PostKind.Comment, scene.Board, $"A remark on {scene.Term}.", null, scene.Question.PostId, forum.Now), Ct);
        var revision = await ReviseAsync(scene, comment, $"A corrected remark on {scene.Term}.");

        var reply = await AcceptRawAsync(scene.Asker, revision.PostId);

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal($"post={revision.PostId} chain=comment kind=revision", Detail(reply));
        Assert.Equal(0, await AcceptanceEventsNamingAsync(revision.PostId));
    }

    /// <summary>PK6 (R8.75): the asker's own revision of the question walks to the question; the detail is <c>post=&lt;id&gt; chain=question kind=revision</c>.</summary>
    [Fact]
    public async Task R8_75_PK6_ARevisionOfAQuestionIsRefusedNamingTheQuestion()
    {
        var scene = await SceneAsync();

        var reply = await AcceptRawAsync(scene.Asker, scene.Revision.PostId);

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal($"post={scene.Revision.PostId} chain=question kind=revision", Detail(reply));
        Assert.Equal(0, await AcceptanceEventsNamingAsync(scene.Revision.PostId));
    }

    /// <summary>
    /// PK7 (R8.75), seeded (ruling 13): a revision whose <c>prev</c> names a digest no post bears, stored although ingest would refuse it (<c>403</c>).
    /// The chain ends at the revision itself, so the refusal reads <c>chain=revision</c>. (The Application fact holds <c>ChainRootOf</c>.)
    /// </summary>
    [Fact]
    public async Task R8_75_PK7_ARevisionWhosePrevNamesNoPostIsRefusedNamingItself()
    {
        var scene = await SceneAsync();
        var (id, _) = await forum.AppendStoredPostAsync(
            scene.Answerer.Agent.SignRevision(scene.Board, "A revision of nothing.", scene.Answer1.PostId, "sha256:" + new string('f', 64), forum.Now), Ct);

        var reply = await AcceptRawAsync(scene.Asker, id);

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal($"post={id} chain=revision kind=revision", Detail(reply));
        Assert.Equal(0, await AcceptanceEventsNamingAsync(id));
    }

    /// <summary>
    /// PK10 (R8.69 after R8.75), seeded (ruling 13): the chain root is an answer stored under a comment (a legacy log; R8.70 refuses it now), and its author
    /// revised it. The comment's author accepts the revision: <c>400</c> <c>curia/accept/root-not-a-question</c> after the ownership check, from the chain
    /// root's <c>parent</c> and not the revision's; nothing is stored.
    /// </summary>
    [Fact]
    public async Task R8_69_PK10_ARevisionOfAnAnswerUnderACommentIsRefusedAsRootNotAQuestion()
    {
        var scene = await SceneAsync();
        var (nestedId, nestedDigest) = await forum.AppendStoredPostAsync(
            scene.Answerer.Agent.SignAnswer(scene.Board, $"Stored under a comment, {scene.Term}.", scene.Comment.PostId, forum.Now), ct: Ct);
        var revision = await Scene.PostAsync(
            forum, scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, $"A corrected nested answer, {scene.Term}.", nestedId, nestedDigest, forum.Now), Ct);

        var reply = await AcceptRawAsync(scene.Asker, revision.PostId);

        Assert.Equal(HttpStatusCode.BadRequest, reply.Status);
        Assert.Equal("curia/accept/root-not-a-question", TypeOf(reply));
        Assert.Equal($"post={revision.PostId} root={scene.Comment.PostId} kind=comment", Detail(reply));
        Assert.Equal(0, await AcceptanceEventsNamingAsync(revision.PostId));
    }

    // ----- PK8, PK9: whose thread -----

    /// <summary>PK8 (R8.75): a stranger accepting the revision of an answer in a thread they did not start is <c>403</c> own-thread-only, not <c>400</c> and not <c>201</c>.</summary>
    [Fact]
    public async Task R8_75_PK8_AStrangerAcceptingARevisionIsForbidden()
    {
        var scene = await SceneAsync();
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.");
        var stranger = await Scene.EnrolAsync(forum, "stranger", Ct);

        var reply = await AcceptRawAsync(stranger, revision.PostId);

        Assert.True(reply.Status == HttpStatusCode.Forbidden, $"Expected: 403 Forbidden Actual: {(int)reply.Status} {reply.Status} {reply.Raw}");
        Assert.Contains("own-thread-only", reply.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await AcceptanceEventsNamingAsync(revision.PostId));
    }

    /// <summary>
    /// PK9 (R8.75, trap 47), the stranger's trap: the answerer's revision names, as <c>parent</c>, a question the <b>caller</b> asked (stored; it also lists in
    /// the caller's thread). The caller accepts it: <c>403</c>, because the chain root's thread is the true asker's. A build that took the thread from the
    /// revision's <c>parent</c> would answer <c>201</c> (FPK1). On the unmodified tree the answer is <c>400</c>, so this is red on the status; the plan's record
    /// called it green for the wrong reason because it asked only for a refusal.
    /// </summary>
    [Fact]
    public async Task R8_75_PK9_AStrangerCannotBorrowTheThreadThroughARevisionsParent()
    {
        var scene = await SceneAsync();
        var stranger = await Scene.EnrolAsync(forum, "stranger", Ct);
        var theirs = await Scene.PostAsync(forum, stranger, stranger.Agent.SignQuestion(scene.Board, "Why does the finch sing at night " + Suffix(), "Night singing " + Suffix(), forum.Now), Ct);
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.", parent: theirs.PostId);

        var reply = await AcceptRawAsync(stranger, revision.PostId);

        Assert.True(reply.Status == HttpStatusCode.Forbidden, $"Expected: 403 Forbidden Actual: {(int)reply.Status} {reply.Status} {reply.Raw}");
        Assert.Contains("own-thread-only", reply.Raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await AcceptanceEventsNamingAsync(revision.PostId));
    }

    // ----- PK11 to PK14, PK18: replay, fork, chain, withheld -----

    /// <summary>PK11 (R8.68 after R8.75): accepting the revision twice is <c>201</c> then <c>200</c> <c>already_accepted</c>, and the log holds one event.</summary>
    [Fact]
    public async Task R8_75_PK11_AReplayOfARevisionRecordsNothing()
    {
        var scene = await SceneAsync();
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.");

        var first = await AcceptedAsync(scene.Asker, revision.PostId);
        var second = await AcceptedAsync(scene.Asker, revision.PostId, HttpStatusCode.OK);

        Assert.False(first.Body.GetProperty("already_accepted").GetBoolean());
        Assert.True(second.Body.GetProperty("already_accepted").GetBoolean());
        Assert.Equal([revision.PostId], await AcceptedInOrderAsync(scene.Question.PostId));
    }

    /// <summary>
    /// PK12 (R8.75, R8.76): a fork. R and C both revise A. The asker accepts R, then C (<c>replaced_answer</c> R, <c>same-chain</c>; the question names C), then A
    /// (<c>same-chain</c> again, backwards): either branch, and the original, may be accepted, and a move within one chain is told as such.
    /// </summary>
    [Fact]
    public async Task R8_75_PK12_EitherBranchOfAForkAndTheOriginalMayBeAccepted()
    {
        var scene = await SceneAsync();
        var r = await ReviseAsync(scene, scene.Answer1, $"A first correction about {scene.Term}.");
        // Stored, not posted: the Forum refuses a second revision of a served prev (R8.77), and a log can still hold the fork.
        var (cId, cDigest) = await forum.AppendStoredPostAsync(scene.Answerer.Agent.SignRevision(scene.Board, $"A second correction about {scene.Term}.", scene.Answer1.PostId, scene.Answer1.Digest, forum.Now), Ct);
        var c = new ScenePost(cId, cDigest);

        await AcceptedAsync(scene.Asker, r.PostId);
        var toC = await AcceptedAsync(scene.Asker, c.PostId);
        Assert.Equal(r.PostId, ReplacedOf(toC));
        Assert.Equal("same-chain", RelationOf(toC));
        Assert.Equal(c.PostId, AcceptedAnswerMember(await PostJsonAsync(scene.Question.PostId)).GetProperty("post_id").GetString());

        var back = await AcceptedAsync(scene.Asker, scene.Answer1.PostId);
        Assert.Equal(c.PostId, ReplacedOf(back));
        Assert.Equal("same-chain", RelationOf(back));
        Assert.Equal([r.PostId, c.PostId, scene.Answer1.PostId], await AcceptedInOrderAsync(scene.Question.PostId));
    }

    /// <summary>
    /// PK13 (R8.75 decision 4): accepting the original after it has been revised is allowed, and the question's <c>accepted_answer.superseded_by</c> lists
    /// the revision. <b>A control</b> (G40, green now): nothing refuses a superseded target.
    /// </summary>
    [Fact]
    public async Task R8_75_PK13_ASupersededAnswerMayStillBeAccepted()
    {
        var scene = await SceneAsync();
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.");

        await AcceptedAsync(scene.Asker, scene.Answer1.PostId);

        var listed = AcceptedAnswerMember(await PostJsonAsync(scene.Question.PostId)).GetProperty("superseded_by");
        var element = Assert.Single(listed.EnumerateArray());
        Assert.Equal(revision.PostId, element.GetProperty("post_id").GetString());
        Assert.Equal(revision.Digest, element.GetProperty("digest").GetString());
    }

    /// <summary>PK14 (R8.75): a revision of a revision. A, R1 (of A), R2 (of R1): R1 is accepted, then R2 (<c>replaced_answer</c> R1): the walk goes to the answer, not the first hop.</summary>
    [Fact]
    public async Task R8_75_PK14_ARevisionOfARevisionMayBeAccepted()
    {
        var scene = await SceneAsync();
        var r1 = await ReviseAsync(scene, scene.Answer1, $"A first correction about {scene.Term}.");
        var r2 = await ReviseAsync(scene, r1, $"A second correction about {scene.Term}.");

        await AcceptedAsync(scene.Asker, r1.PostId);
        var reply = await AcceptedAsync(scene.Asker, r2.PostId);

        Assert.Equal(r1.PostId, ReplacedOf(reply));
        Assert.Equal([r1.PostId, r2.PostId], await AcceptedInOrderAsync(scene.Question.PostId));
    }

    /// <summary>
    /// PK18 (R8.75, R9.46 and the plan's ruling 9b): the Forum withholds the revision, and the asker accepts it anyway, as the unmodified route accepts a
    /// withheld answer (Task 0 item 6: <c>201</c>). The question serves the member without a <c>post_id</c> and with <c>kind: "revision"</c>.
    /// </summary>
    [Fact]
    public async Task R8_75_PK18_AWithheldRevisionIsAcceptedAndTheQuestionNamesItByDigestAndKind()
    {
        var scene = await SceneAsync();
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.");
        await forum.WithholdAsync(revision.PostId, Ct);

        await AcceptedAsync(scene.Asker, revision.PostId);

        var member = AcceptedAnswerMember(await PostJsonAsync(scene.Question.PostId));
        Assert.Equal(revision.Digest, member.GetProperty("digest").GetString());
        Assert.False(member.TryGetProperty("post_id", out _), "the Forum named a post it does not serve");
        Assert.Equal("revision", KindOf(member));
    }

    // ----- PK19 to PK22: the inbox, the replay drill, every route, the receipt -----

    private async Task<string[]> InboxIdsAsync(SceneActor reader, string board)
    {
        var token = await reader.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/inbox?board={board}");
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
        request.Headers.Add("DPoP", reader.Dpop.Proof("GET", "http://localhost/v1/inbox", forum.Now, token, nonce: null));
        using var response = await forum.Client.SendAsync(request, Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, raw);
        return [.. JsonDocument.Parse(raw).RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post_id").GetString()!)];
    }

    /// <summary>
    /// PK19 (R9.36 after R8.75): a question whose acceptance names a revision is not open. A reader's inbox lists the question before the acceptance and not
    /// after it. The acceptance is the route's, so this is red on the unmodified tree for the route's refusal; PK19s reads the inbox apart from it.
    /// </summary>
    [Fact]
    public async Task R8_75_PK19_AQuestionWhoseAcceptanceNamesARevisionIsNotInTheInbox()
    {
        var scene = await SceneAsync();
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.");
        var reader = await Scene.EnrolAsync(forum, "reader", Ct);

        Assert.Contains(scene.Question.PostId, await InboxIdsAsync(reader, scene.Board));
        await AcceptedAsync(scene.Asker, revision.PostId);

        Assert.DoesNotContain(scene.Question.PostId, await InboxIdsAsync(reader, scene.Board));
    }

    /// <summary>
    /// PK19s: the same with the acceptance seeded through the host's use case. <b>Green first</b> (Task 0): the inbox reads the per-thread fold, which settles a
    /// thread whatever kind of post its acceptance names; it is held by FPK12 only through PK19 (a route that records under the revision's <c>parent</c>).
    /// </summary>
    [Fact]
    public async Task R8_75_PK19s_TheInboxReadsTheFoldAndNotTheRoute()
    {
        var scene = await SceneAsync();
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.");
        var reader = await Scene.EnrolAsync(forum, "reader", Ct);
        await forum.AppendStoredAcceptanceAsync(scene.Question.PostId, revision.PostId, scene.Asker.Agent.AgentId, Ct);

        Assert.DoesNotContain(scene.Question.PostId, await InboxIdsAsync(reader, scene.Board));
    }

    /// <summary>
    /// PK20 (R11.9): the replay drill. After A is accepted and the acceptance moved to its revision, the read models folded from the <c>events</c> table
    /// alone (the table's rows through the reader, folded by the projections) agree with what the Forum serves live: the question's member names the revision,
    /// the revision's <c>accepted</c> flag, and the answerer's count of accepted answers is one (the ledger: one thread, one credit).
    /// </summary>
    [Fact]
    public async Task R11_9_PK20_TheModelsFoldedFromTheEventsTableAgreeWithTheLiveOnes()
    {
        var scene = await SceneAsync();
        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.");
        await AcceptedAsync(scene.Asker, scene.Answer1.PostId);
        await AcceptedAsync(scene.Asker, revision.PostId);

        var log = (await forum.Services.GetRequiredService<IEventReader>().ReadAllAsync(Ct))
            .Match(r => r, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
        var posts = PostProjector.Fold(log);
        var index = AcceptedAnswerIndex.Fold(log, posts, _ => true);
        var folded = index.StandingOf(posts.Single(p => p.PostId == scene.Question.PostId));
        var count = AgentStandingProjector.PostureOf(AgentStandingProjector.Fold(log), Typed.Agent(scene.Answerer.Agent.AgentId))
            .Match(p => p.AcceptedAnswers, e => throw new InvalidOperationException(e.Title));

        var live = AcceptedAnswerMember(await PostJsonAsync(scene.Question.PostId));
        Assert.Equal(folded.Digest, live.GetProperty("digest").GetString());
        Assert.Equal(revision.Digest, folded.Digest);
        Assert.Equal(index.IsAccepted(posts.Single(p => p.PostId == revision.PostId)), (await PostJsonAsync(revision.PostId)).GetProperty("accepted").GetBoolean());
        Assert.True(index.IsAccepted(posts.Single(p => p.PostId == revision.PostId)), "the fold of the events table does not mark the revision accepted");
        Assert.Equal(1, count);
    }

    /// <summary>The routes that serve a question with its member, as G36's and G40's facts list them: the single read, the thread, the board, a search and the batch.</summary>
    public static TheoryData<string> Routes() => ["post", "thread", "board", "search", "batch"];

    private async Task<JsonElement> QuestionOnAsync(string route, Scene scene)
    {
        var http = forum.Client;
        var id = scene.Question.PostId;
        JsonElement Pick(IEnumerable<JsonElement> posts) => posts.Single(p => p.GetProperty("post_id").GetString() == id).Clone();
        switch (route)
        {
            case "post":
                return await PostJsonAsync(id);
            case "thread":
                return Pick((await GetAsync($"/v1/threads/{id}")).EnumerateArray());
            case "board":
                return Pick((await GetAsync($"/v1/boards/{scene.Board}/posts")).EnumerateArray());
            case "search":
                return Pick((await GetAsync($"/v1/search?q={scene.Term}&board={scene.Board}&limit=50")).GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post")));
            case "batch":
            {
                using var response = await http.PostAsJsonAsync("/v1/posts/batch", new { digests = new[] { scene.Question.Digest } }, Ct);
                var text = await response.Content.ReadAsStringAsync(Ct);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                return Pick(JsonDocument.Parse(text).RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("post")).Where(p => p.ValueKind == JsonValueKind.Object));
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(route), route, "not one of the routes the theory names");
        }
    }

    /// <summary>
    /// PK21 (R9.46): on every route that serves a question, <c>accepted_answer</c> carries <c>kind</c> when it is an object (one of the two published words, the
    /// word for the kind of post accepted) and, as <c>null</c>, carries nothing. The answer is accepted first, so this reads the member apart from a revision's
    /// acceptance; the second step names the revision.
    /// </summary>
    [Theory]
    [MemberData(nameof(Routes))]
    public async Task R9_46_PK21_EveryRouteThatServesAQuestionCarriesTheKindOfWhatItNames(string route)
    {
        var scene = await SceneAsync();
        var words = PublishedReadersWords.R946KindWords();
        Assert.Equal(JsonValueKind.Null, AcceptedAnswerMember(await QuestionOnAsync(route, scene)).ValueKind);

        await AcceptedAsync(scene.Asker, scene.Answer1.PostId);
        var answerMember = AcceptedAnswerMember(await QuestionOnAsync(route, scene));
        Assert.True(answerMember.TryGetProperty("kind", out var answerKind), $"Expected: member \"kind\" Actual: missing from accepted_answer on {route}: {answerMember.GetRawText()}");
        Assert.Equal(words[0], answerKind.GetString());

        var revision = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.");
        await AcceptedAsync(scene.Asker, revision.PostId);
        var revisionMember = AcceptedAnswerMember(await QuestionOnAsync(route, scene));
        Assert.Equal(words[1], KindOf(revisionMember));
    }

    /// <summary>
    /// PK22 (R8.76): the receipt carries <c>replaced_relation</c>, always: <c>null</c> on a first acceptance and on a replay, <c>other-answer</c> for a
    /// different answer (by the same author, and by another), <c>same-chain</c> for a move to the revision of the answer that stood.
    /// </summary>
    [Fact]
    public async Task R8_76_PK22_TheReceiptCarriesTheRelationAlways()
    {
        var scene = await SceneAsync();
        var values = PublishedReadersWords.R876Values();
        var other = await Scene.T1Async(forum, scene.Board, Ct);
        var theirs = await Scene.PostAsync(forum, other, other.Agent.SignAnswer(scene.Board, $"Another author's answer on {scene.Term}.", scene.Question.PostId, forum.Now), Ct);

        var first = await AcceptedAsync(scene.Asker, scene.Answer1.PostId);
        var sameAuthor = await AcceptedAsync(scene.Asker, scene.Answer2.PostId);
        var anotherAuthor = await AcceptedAsync(scene.Asker, theirs.PostId);
        var replay = await AcceptedAsync(scene.Asker, theirs.PostId, HttpStatusCode.OK);
        var theirRevision = await Scene.PostAsync(forum, other, other.Agent.SignRevision(scene.Board, $"A correction of the other answer on {scene.Term}.", theirs.PostId, theirs.Digest, forum.Now), Ct);
        var moved = await AcceptedAsync(scene.Asker, theirRevision.PostId);

        Assert.Equal("(null)", RelationOf(first));
        Assert.Equal(values[1], RelationOf(sameAuthor));
        Assert.Equal(values[1], RelationOf(anotherAuthor));
        Assert.Equal("(null)", RelationOf(replay));
        Assert.Equal(values[0], RelationOf(moved));
    }

    // ----- PK33, PK34: the seam end to end -----

    private sealed record Lying(Scene Scene, SceneActor Stranger, Posted Qs, Posted Rl);

    /// <summary>
    /// The seam (Task 0 item 4): a stranger asked QS; the answerer's revision RL of the answer under the asker's question has, as <c>parent</c>, QS. The route
    /// stores it, and it lists in QS's thread.
    /// </summary>
    private async Task<Lying> LyingAsync()
    {
        var scene = await SceneAsync();
        var stranger = await Scene.EnrolAsync(forum, "stranger", Ct);
        var qs = await Scene.PostAsync(forum, stranger, stranger.Agent.SignQuestion(scene.Board, "Why does the finch sing at night " + Suffix(), "Night singing " + Suffix(), forum.Now), Ct);
        var rl = await ReviseAsync(scene, scene.Answer1, $"A corrected first answer about {scene.Term}.", parent: qs.PostId);
        return new Lying(scene, stranger, qs, rl);
    }

    /// <summary>
    /// PK33 (R9.46): the seam end to end. The true asker accepts RL: <c>201</c>, recorded under the true question; the true question names RL with its kind; RL
    /// reads accepted and the original does not; the stranger's question names nothing.
    /// </summary>
    [Fact]
    public async Task R9_46_PK33_ARevisionWithALyingParentIsAcceptedUnderItsTrueQuestionEndToEnd()
    {
        var lying = await LyingAsync();
        var scene = lying.Scene;

        await AcceptedAsync(scene.Asker, lying.Rl.PostId);

        Assert.Equal([scene.Question.PostId], await ThreadRootsRecordedForAsync(lying.Rl.PostId));
        var member = AcceptedAnswerMember(await PostJsonAsync(scene.Question.PostId));
        Assert.Equal(lying.Rl.Digest, member.GetProperty("digest").GetString());
        Assert.Equal(lying.Rl.PostId, member.GetProperty("post_id").GetString());
        Assert.Equal("revision", KindOf(member));
        Assert.True((await PostJsonAsync(lying.Rl.PostId)).GetProperty("accepted").GetBoolean());
        Assert.False((await PostJsonAsync(scene.Answer1.PostId)).GetProperty("accepted").GetBoolean());
        Assert.Equal(JsonValueKind.Null, AcceptedAnswerMember(await PostJsonAsync(lying.Qs.PostId)).ValueKind);
    }

    /// <summary>
    /// PK34 (R9.46, G38's recorded non-property), the state seeded apart from the route: the true question's acceptance names RL. The thread route still lists RL
    /// beneath the stranger's question (it follows <c>parent</c>) with <c>accepted</c> false; the true thread does not list RL, its answer reads not accepted,
    /// and the question's member names RL with its kind.
    /// </summary>
    [Fact]
    public async Task R9_46_PK34_AStrangersThreadNeverSaysItsAskerAcceptedTheRevision()
    {
        var lying = await LyingAsync();
        var scene = lying.Scene;
        await forum.AppendStoredAcceptanceAsync(scene.Question.PostId, lying.Rl.PostId, scene.Asker.Agent.AgentId, Ct);

        var strangersThread = await ThreadAsync(lying.Qs.PostId);
        var trueThread = await ThreadAsync(scene.Question.PostId);

        Assert.True(strangersThread.ContainsKey(lying.Rl.PostId), "the thread route no longer lists a revision beneath its author-written parent");
        Assert.False(strangersThread[lying.Rl.PostId].GetProperty("accepted").GetBoolean(), "the stranger's thread says its asker accepted a text they never saw");
        Assert.False(trueThread.ContainsKey(lying.Rl.PostId));
        Assert.False(trueThread[scene.Answer1.PostId].GetProperty("accepted").GetBoolean());
        var member = AcceptedAnswerMember(trueThread[scene.Question.PostId]);
        Assert.Equal(lying.Rl.PostId, member.GetProperty("post_id").GetString());
        Assert.Equal("revision", KindOf(member));
    }

    /// <summary>
    /// PK34b (R9.46, plan S5): a crafted acceptance recorded under the stranger's question naming RL (by that question's asker; the route would never record it
    /// there). RL's chain root is the answer under the true question, so RL reads accepted in no thread: not in the stranger's listing and not on its own read.
    /// </summary>
    [Fact]
    public async Task R9_46_PK34b_AnAcceptanceRecordedUnderTheStrangersQuestionMarksNothing()
    {
        var lying = await LyingAsync();
        await forum.AppendStoredAcceptanceAsync(lying.Qs.PostId, lying.Rl.PostId, lying.Stranger.Agent.AgentId, Ct);

        var strangersThread = await ThreadAsync(lying.Qs.PostId);

        Assert.False(strangersThread[lying.Rl.PostId].GetProperty("accepted").GetBoolean(), "RL is listed accepted beneath a thread its chain root does not belong to");
        Assert.False((await PostJsonAsync(lying.Rl.PostId)).GetProperty("accepted").GetBoolean(), "RL reads accepted by its own parent");
    }

    // ----- PK39 (a control) and TRW12 (replaces PK40, a characterization) -----

    /// <summary>PK39 (R8.70, a control): a revision with no <c>parent</c> is refused <c>400</c> <c>curia/content/parent-required</c> and nothing is stored (replaces PK3b's second half).</summary>
    [Fact]
    public async Task R8_70_PK39_ARevisionWithNoParentIsRefusedAtIngest()
    {
        var scene = await SceneAsync();
        var before = await AllEventsAsync();
        var wire = scene.Answerer.Agent.Sign(PostKind.Revision, scene.Board, "A revision with no parent.", null, parent: null, forum.Now, prev: scene.Answer1.Digest);

        using var response = await scene.Answerer.Dpop.PostAsync(
            forum.Client, "http://localhost/v1/posts", await scene.Answerer.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct), wire, forum.Now, Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("curia/content/parent-required", raw, StringComparison.Ordinal);
        Assert.Equal(before, await AllEventsAsync());
    }

    /// <summary>
    /// TRW12 (R7.26, errata G46; replaces PK40, which asserted the refusal this stage's defect made and said it was not a vote for it). An asker with three
    /// posts in the trailing day resolves: an acceptance creates no post and spends no posting budget, so Table 11's three is not the acceptance's. The scene's
    /// asker has the comment and the revision in the window; one more makes three. The oracle is the events table: one <c>answer.accepted</c> event naming the
    /// answer, with the asker as <c>accepted_by</c>.
    /// </summary>
    [Fact]
    public async Task R7_26_TRW12_AnAskerWhoseDailyPostBudgetIsSpentCanStillResolve()
    {
        var scene = await SceneAsync();

        await Scene.PostAsync(forum, scene.Asker, scene.Asker.Agent.Sign(PostKind.Comment, scene.Board, $"A third post that day about {scene.Term}.", null, scene.Question.PostId, forum.Now), Ct);

        var reply = await AcceptRawAsync(scene.Asker, scene.Answer1.PostId);

        Assert.True(
            reply.Status == HttpStatusCode.Created,
            $"Expected: {(int)HttpStatusCode.Created} {HttpStatusCode.Created} Actual: {(int)reply.Status} {reply.Status} {reply.Raw}");
        Assert.Equal(1, await AcceptanceEventsNamingAsync(scene.Answer1.PostId));
    }

    // ----- PK31: the stub is held to the Forum -----

    private static string[] Members(string json) =>
        [.. JsonNode.Parse(json)!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)];

    /// <summary>
    /// PK31 (the stub's fidelity, as <c>StubFidelityTests</c> holds the other documents): the receipt of an acceptance that replaced a post, and the question's
    /// <c>accepted_answer</c> object, have the same members on the stub and on this Forum, and both hold the members the errata names (<c>replaced_relation</c>,
    /// <c>kind</c>). A stub that serves a member this Forum does not, or the reverse, is named by the side that lacks it.
    /// </summary>
    [Fact]
    public async Task R9_46_PK31_TheStubServesTheReceiptAndTheMemberAsTheForumDoes()
    {
        var scene = await SceneAsync();
        await AcceptedAsync(scene.Asker, scene.Answer1.PostId);
        var switched = await AcceptedAsync(scene.Asker, scene.Answer2.PostId);

        var relation = PublishedReadersWords.R876Member();
        var stubReceipt = Members(StubLog.AcceptanceJson(false, "\"01TESTREPLACED000000000000A\"", "\"" + PublishedReadersWords.R876Values()[1] + "\""));
        var realReceipt = switched.Body.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

        _stub.AcceptedAnswer = StubAcceptedAnswer.Standing;
        _stub.AcceptedAnswerKind = PublishedReadersWords.R946KindWords()[0];
        _stub.AcceptedAnswerSupersededBy = [];
        var stubMember = JsonNode.Parse(_stub.PostJson())!["provenance"]!["accepted_answer"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToArray();
        var realMember = AcceptedAnswerMember(await PostJsonAsync(scene.Question.PostId)).EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

        var mismatches = new List<string>();
        if (!realReceipt.Contains(relation)) mismatches.Add($"the Forum's receipt lacks {relation}");
        if (!stubReceipt.Contains(relation)) mismatches.Add($"the stub's receipt lacks {relation}");
        if (!realReceipt.SequenceEqual(stubReceipt)) mismatches.Add($"receipt members differ: the Forum serves [{string.Join(",", realReceipt)}], the stub serves [{string.Join(",", stubReceipt)}]");
        if (!realMember.Contains("kind")) mismatches.Add("the Forum's accepted_answer lacks kind");
        if (!realMember.SequenceEqual(stubMember)) mismatches.Add($"accepted_answer members differ: the Forum serves [{string.Join(",", realMember)}], the stub serves [{string.Join(",", stubMember)}]");

        Assert.True(mismatches.Count == 0, string.Join("; ", mismatches));
    }
}
