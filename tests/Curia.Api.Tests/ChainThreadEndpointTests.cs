using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Domain.Content;
using Npgsql;
using Xunit;

using Posted = Curia.Api.Tests.ScenePost;
using Scene = Curia.Api.Tests.AcceptedAnswerScene;

namespace Curia.Api.Tests;

/// <summary>
/// PR 3 (errata G47, R9.48), probes CHN10 to CHN15, CHN54 and CHN60 against a real Forum on real Postgres: the thread route places a revision by the root
/// of its correction chain and never by its own <c>parent</c> (CHN10, CHN11, CHN13 to CHN15, CHN54), decides membership before the serving filter (CHN12), and
/// ingest still stores a lying revision while refusing one that corrects nothing (CHN13, CHN60).
///
/// <para><b>Oracles.</b> The ids and digests are the receipts of the posts the test made; the members of a listing are the ids the route returns for the
/// threads the test built, compared with the set the construction implies, never with the route's own claim about itself; the events held are counted by
/// SQL over the fixture's connection string (never a rebuilt one, which drops the password). The seam half of each lying case appends the post through
/// <c>AppendStoredPostAsync</c>, a log this code did not write, and asserts the seeding took by the events table.</para>
///
/// <para>CHN13, CHN14 and CHN60 are controls (green on the unmodified tree); the rest are red by assertion.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ChainThreadEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string NoSuchPost = "01M6FFFF000000000000000000";
    private const string PostsUrl = "http://localhost/v1/posts";
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private sealed record Reply(HttpStatusCode Status, string Raw);

    private async Task<JsonElement> GetAsync(string path)
    {
        using var response = await forum.Client.GetAsync(new Uri(path, UriKind.Relative), Ct);
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"GET {path}: {(int)response.StatusCode} {raw}");
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    /// <summary>The members of a thread listing in the route's order, each with its <c>accepted</c> flag.</summary>
    private async Task<List<(string Id, bool Accepted)>> ThreadAsync(string root) =>
        [.. (await GetAsync($"/v1/threads/{root}")).EnumerateArray().Select(p => (p.GetProperty("post_id").GetString()!, p.GetProperty("accepted").GetBoolean()))];

    private async Task<string[]> MembersAsync(string root) => [.. (await ThreadAsync(root)).Select(m => m.Id)];

    /// <summary>A revision of <paramref name="original"/> by <paramref name="actor"/>, as <c>curia revision</c> writes one but with the <c>parent</c> the fact chooses.</summary>
    private Task<Posted> ReviseAsync(Scene scene, SceneActor actor, Posted original, string parent, string? body = null) =>
        Scene.PostAsync(forum, actor, actor.Agent.SignRevision(scene.Board, body ?? $"A corrected text about {scene.Term} {Suffix()}.", parent, original.Digest, forum.Now), Ct);

    private async Task<Reply> TryPostAsync(SceneActor actor, byte[] wire)
    {
        using var response = await actor.Dpop.PostAsync(forum.Client, PostsUrl, await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct), wire, forum.Now, Ct);
        return new Reply(response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task AcceptAsync(Scene scene, Posted post)
    {
        using var response = await scene.Asker.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{post.PostId}/accept",
            await scene.Asker.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
            Encoding.UTF8.GetBytes("{}"), forum.Now, Ct, contentType: "application/json");
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<long> EventsNamingAsync(string eventType, string aggregateId)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events WHERE event_type = @t AND aggregate_id = @a;", connection);
        count.Parameters.AddWithValue("t", eventType);
        count.Parameters.AddWithValue("a", aggregateId);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task<long> AllEventsAsync()
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        return (long)(await count.ExecuteScalarAsync(Ct))!;
    }

    private async Task<string> AcceptedAnswerPostIdAsync(string question) =>
        (await GetAsync($"/v1/posts/{question}")).GetProperty("provenance").GetProperty("accepted_answer").GetProperty("post_id").GetString()!;

    /// <summary>A second question with an answer by the scene's answerer: A8 in Q8's thread, so one author has posts in two threads.</summary>
    private async Task<(Posted Question, Posted Answer)> SecondThreadAsync(Scene scene)
    {
        var stranger = await Scene.EnrolAsync(forum, "stranger", Ct);
        var question = await Scene.PostAsync(forum, stranger, stranger.Agent.SignQuestion(scene.Board, "Why does the finch sing at night " + Suffix(), "Night singing " + Suffix(), forum.Now), Ct);
        var answer = await Scene.PostAsync(forum, scene.Answerer, scene.Answerer.Agent.SignAnswer(scene.Board, $"Because of the {scene.Term} {Suffix()}.", question.PostId, forum.Now), Ct);
        return (question, answer);
    }

    private static string[] Expected(Scene scene, params Posted[] extra) =>
        [scene.Question.PostId, scene.Answer1.PostId, scene.Answer2.PostId, scene.Comment.PostId, scene.Revision.PostId, .. extra.Select(p => p.PostId)];

    /// <summary>
    /// CHN10 (the contributor's F2, end to end). The route stores LP1, a revision of Q5's answer whose <c>parent</c> is Q6; Q5's asker accepts it. Q5's
    /// listing holds LP1 accepted, Q5 names it, and Q6's listing does not hold it. The expected sets are the construction's.
    /// </summary>
    [Fact]
    public async Task R9_48_CHN10_TheAcceptedRevisionIsInTheListingOfTheQuestionThatNamesIt()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var (q6, _) = await SecondThreadAsync(scene);
        var lp1 = await ReviseAsync(scene, scene.Answerer, scene.Answer1, parent: q6.PostId);
        await AcceptAsync(scene, lp1);

        var q5Listing = await ThreadAsync(scene.Question.PostId);
        var q6Listing = await MembersAsync(q6.PostId);

        var problems = new List<string>();
        if (!q5Listing.Select(m => m.Id).Order(StringComparer.Ordinal).SequenceEqual(Expected(scene, lp1).Order(StringComparer.Ordinal))) problems.Add($"Q5's listing is [{string.Join(", ", q5Listing.Select(m => m.Id))}], not the construction's six posts");
        if (!q5Listing.Any(m => m.Id == lp1.PostId && m.Accepted)) problems.Add("LP1 is not in Q5's listing accepted");
        if (await AcceptedAnswerPostIdAsync(scene.Question.PostId) != lp1.PostId) problems.Add("Q5 does not name LP1");
        if (q6Listing.Contains(lp1.PostId)) problems.Add("Q6's listing holds LP1, a text its asker never saw");
        Assert.True(problems.Count == 0, $"Expected: LP1 in Q5's listing accepted, and not in Q6's. Actual: {string.Join("; ", problems)}");
    }

    /// <summary>CHN11. The same, in a log the Forum did not write: LP1 and the acceptance seeded through the store seam, the seeding asserted by the events table.</summary>
    [Fact]
    public async Task R9_48_CHN11_TheSameInALogTheForumDidNotWrite()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var (q6, _) = await SecondThreadAsync(scene);
        var (lp1Id, _) = await forum.AppendStoredPostAsync(
            scene.Answerer.Agent.SignRevision(scene.Board, $"A seeded correction about {scene.Term}.", q6.PostId, scene.Answer1.Digest, forum.Now), Ct);
        await forum.AppendStoredAcceptanceAsync(scene.Question.PostId, lp1Id, scene.Asker.Agent.AgentId, Ct);

        // The seeding took: the log holds the post and the acceptance.
        Assert.Equal(1, await EventsNamingAsync("post.accepted", lp1Id));

        var q5Listing = await ThreadAsync(scene.Question.PostId);
        var q6Listing = await MembersAsync(q6.PostId);

        var problems = new List<string>();
        if (!q5Listing.Any(m => m.Id == lp1Id && m.Accepted)) problems.Add("LP1 is not in Q5's listing accepted");
        if (await AcceptedAnswerPostIdAsync(scene.Question.PostId) != lp1Id) problems.Add("Q5 does not name LP1");
        if (q6Listing.Contains(lp1Id)) problems.Add("Q6's listing holds LP1");
        Assert.True(problems.Count == 0, $"Expected: LP1 in Q5's listing accepted, and not in Q6's. Actual: {string.Join("; ", problems)}");
    }

    /// <summary>CHN12 (a control). A withheld chain root does not detach its revisions: the listing holds the revision and not the withheld answer.</summary>
    [Fact]
    public async Task R9_48_CHN12_AWithheldChainRootDoesNotDetachItsRevisions()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var revision = await ReviseAsync(scene, scene.Answerer, scene.Answer1, parent: scene.Answer1.PostId);
        Assert.Contains(revision.PostId, await MembersAsync(scene.Question.PostId));

        await forum.WithholdAsync(scene.Answer1.PostId, Ct);
        var members = await MembersAsync(scene.Question.PostId);

        Assert.Contains(revision.PostId, members);
        Assert.DoesNotContain(scene.Answer1.PostId, members);
    }

    /// <summary>
    /// CHN13 (a control, ruling 3). Ingest still stores a lying revision: the route answers <c>201</c> for LP1 (another thread's question), LP2 (a parent no post
    /// bears), and for revisions chained to an own post whose <c>parent</c> is a digest string, <c>not-an-id</c> or another own post, and the events table holds each.
    /// </summary>
    [Fact]
    public async Task R9_48_CHN13_IngestStillStoresALyingRevision()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var (q6, a8) = await SecondThreadAsync(scene);
        var extra = new List<Posted>();
        for (var i = 0; i < 4; i++)
            extra.Add(await Scene.PostAsync(forum, scene.Answerer, scene.Answerer.Agent.SignAnswer(scene.Board, $"Another way to settle {scene.Term} {i} {Suffix()}.", scene.Question.PostId, forum.Now), Ct));

        var stored = new List<(string Name, Posted Post)>
        {
            ("another question", await ReviseAsync(scene, scene.Answerer, scene.Answer1, q6.PostId)),
            ("a digest string", await ReviseAsync(scene, scene.Answerer, extra[0], "sha256:" + new string('a', 64))),
            ("not-an-id", await ReviseAsync(scene, scene.Answerer, extra[1], "not-an-id")),
            ("another own post", await ReviseAsync(scene, scene.Answerer, extra[2], a8.PostId)),
            ("LP2: a parent no post bears", await ReviseAsync(scene, scene.Asker, scene.Comment, NoSuchPost)),
        };

        var missing = new List<string>();
        foreach (var (name, post) in stored)
            if (await EventsNamingAsync("post.accepted", post.PostId) != 1) missing.Add(name);
        Assert.True(missing.Count == 0, $"the events table does not hold the stored revision(s): {string.Join(", ", missing)}");
    }

    /// <summary>CHN14 (a control, R9.46). Accepted is relative to the post's own thread: a listing asked for at the answer marks nothing accepted, and the single read of LP1 says accepted.</summary>
    [Fact]
    public async Task R9_48_CHN14_AcceptedIsRelativeToThePostsOwnThread()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var (q6, _) = await SecondThreadAsync(scene);
        var lp1 = await ReviseAsync(scene, scene.Answerer, scene.Answer1, q6.PostId);
        await AcceptAsync(scene, lp1);

        var atAnswer = await ThreadAsync(scene.Answer1.PostId);

        Assert.Contains(atAnswer, m => m.Id == scene.Answer1.PostId);
        Assert.All(atAnswer, m => Assert.False(m.Accepted, $"the listing at the answer marks {m.Id} accepted"));
        Assert.True((await GetAsync($"/v1/posts/{lp1.PostId}")).GetProperty("accepted").GetBoolean());
    }

    /// <summary>
    /// CHN15 (a property over the scene). For every question the scene holds whose <c>accepted_answer</c> names a post the Forum serves, that post is a member
    /// of that question's listing. The questions are those the test made, read from the log by their kind.
    /// </summary>
    [Fact]
    public async Task R9_48_CHN15_WhatAQuestionNamesIsInItsListing()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var (q6, _) = await SecondThreadAsync(scene);
        var lp1 = await ReviseAsync(scene, scene.Answerer, scene.Answer1, q6.PostId);
        await AcceptAsync(scene, lp1);

        var problems = new List<string>();
        var checkedQuestions = 0;
        foreach (var question in new[] { scene.Question.PostId, q6.PostId })
        {
            var member = (await GetAsync($"/v1/posts/{question}")).GetProperty("provenance").GetProperty("accepted_answer");
            if (member.ValueKind != JsonValueKind.Object || !member.TryGetProperty("post_id", out var named)) continue;
            checkedQuestions++;
            var listing = await MembersAsync(question);
            if (!listing.Contains(named.GetString()!)) problems.Add($"question {question} names {named.GetString()}, absent from its own listing");
        }

        Assert.Equal(1, checkedQuestions);
        Assert.True(problems.Count == 0, $"Falsified: {string.Join("; ", problems)}");
    }

    /// <summary>
    /// CHN54, through the route (ruling 19(c)). One author has A5 and A8 in different threads; a revision of A5 whose <c>parent</c> is A8 is stored (<c>201</c>);
    /// it is listed with A5's thread and not A8's.
    /// </summary>
    [Fact]
    public async Task R9_48_CHN54_ARevisionChainedToOnePostAndParentedToAnotherIsListedWithItsChain()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var (q8, a8) = await SecondThreadAsync(scene);
        var via = await ReviseAsync(scene, scene.Answerer, scene.Answer1, a8.PostId);

        var q5 = await MembersAsync(scene.Question.PostId);
        var q8Members = await MembersAsync(q8.PostId);

        Assert.True(q5.Contains(via.PostId) && !q8Members.Contains(via.PostId), $"Expected: the revision in Q5's listing only. Actual: in Q5's {q5.Contains(via.PostId)}, in Q8's {q8Members.Contains(via.PostId)}");
    }

    /// <summary>
    /// CHN60 (a control, ruling 19). The route refuses a revision that corrects nothing: a stranger's revision, one whose <c>prev</c> names no stored post
    /// and one whose <c>prev</c> is another author's post, each <c>403</c> <c>own-resource-only</c>, and stores nothing.
    /// </summary>
    [Fact]
    public async Task R9_48_CHN60_TheRouteRefusesARevisionThatCorrectsNothing()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var stranger = await Scene.EnrolAsync(forum, "stranger", Ct);
        var before = await AllEventsAsync();

        var forms = new (string Name, SceneActor By, byte[] Wire)[]
        {
            ("a stranger's revision", stranger, stranger.Agent.SignRevision(scene.Board, "A stranger's rewrite.", scene.Answer1.PostId, scene.Answer1.Digest, forum.Now)),
            ("a prev that names no stored post", scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, "A rewrite of nothing.", scene.Answer1.PostId, "sha256:" + new string('e', 64), forum.Now)),
            ("another author's post as prev", scene.Asker, scene.Asker.Agent.SignRevision(scene.Board, "An asker's rewrite of an answer.", scene.Answer1.PostId, scene.Answer1.Digest, forum.Now)),
        };

        var problems = new List<string>();
        foreach (var (name, by, wire) in forms)
        {
            var reply = await TryPostAsync(by, wire);
            if (reply.Status != HttpStatusCode.Forbidden || !reply.Raw.Contains("own-resource-only", StringComparison.Ordinal)) problems.Add($"{name}: {(int)reply.Status} {reply.Raw}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
        Assert.Equal(before, await AllEventsAsync());
    }
}
