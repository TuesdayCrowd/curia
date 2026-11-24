using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Curia.Domain.Content;
using Xunit;

using Scene = Curia.Api.Tests.AcceptedAnswerScene;

namespace Curia.Api.Tests;

/// <summary>
/// R9.70 and R9.71 (errata G76) over the real Forum: probes RVT2 (<c>revises</c> and what carries none), ACA2 (<c>accepted_at</c> is the receipt's string), ACA3 (<c>first_revision</c> iff <c>superseded_by</c> is
/// not empty, by the order the test made the posts in), ACA5 (<c>question_revised</c> on answers). <b>Oracles.</b> The posts the test signed and posted in the order it chose (their receipts), the accept
/// route's receipt string (a different artifact from the index), and the raw responses; never the index. The relation is by log order, so each order is made by calling the routes in that order.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptanceMembersEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private async Task<JsonElement> GetPostAsync(string postId, CancellationToken ct) =>
        await forum.Client.GetFromJsonAsync<JsonElement>(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);

    private async Task<ScenePost> ReviseAnswerAsync(Scene scene, ScenePost answer, string body, CancellationToken ct) =>
        await Scene.PostAsync(forum, scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, body, answer.PostId, answer.Digest, forum.Now), ct);

    /// <summary>The asker accepts <paramref name="answer"/>: the receipt as the route wrote it (the status and the body).</summary>
    private async Task<(HttpStatusCode Status, JsonElement Receipt)> AcceptReceiptAsync(Scene scene, ScenePost answer, CancellationToken ct)
    {
        using var response = await scene.Asker.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{answer.PostId}/accept",
            await scene.Asker.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct),
            Encoding.UTF8.GetBytes("{}"), forum.Now, ct, contentType: "application/json");
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, text);
        return (response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static JsonElement AcceptedAnswerOf(JsonElement question)
    {
        Assert.True(question.GetProperty("provenance").TryGetProperty("accepted_answer", out var member), "provenance.accepted_answer is absent on a question");
        return member;
    }

    // ---- RVT2 -------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// RVT2. <c>revises</c> is on a revision of a question and names the question it revises, <c>{"post_id": "<question>", "kind": "question"}</c>, though the revision's author wrote another post as its
    /// <c>parent</c> (the scene's revision names the question as parent; this row adds a revision whose <c>parent</c> names a comment). Red on the tree.
    /// </summary>
    [Fact]
    public async Task R9_70_RVT2_ARevisionOfAQuestionNamesTheQuestionItRevises()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);

        var served = await GetPostAsync(scene.Revision.PostId, ct);

        Assert.True(served.GetProperty("provenance").TryGetProperty("revises", out var revises), "provenance.revises is absent on a revision of a question");
        Assert.Equal(scene.Question.PostId, revises.GetProperty("post_id").GetString());
        Assert.Equal("question", revises.GetProperty("kind").GetString());
    }

    /// <summary>
    /// RVT2's control (the plan's: it is what makes <c>kind: "question"</c> non-vacuous): a revision of an answer and a revision of a comment carry no <c>revises</c>, and neither does a question, an answer or a comment.
    /// Green on the tree (no member anywhere); it stays green only if the member is not written on every revision.
    /// </summary>
    [Fact]
    public async Task R9_70_RVT2_Control_ARevisionOfAnAnswerOrACommentAndEveryOtherKindCarryNoRevises()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        var answerRevision = await ReviseAnswerAsync(scene, scene.Answer1, "A corrected first answer.", ct);
        var commentRevision = await Scene.PostAsync(forum, scene.Asker, scene.Asker.Agent.SignRevision(scene.Board, "A corrected comment.", scene.Comment.PostId, scene.Comment.Digest, forum.Now), ct);

        foreach (var (id, what) in new[]
        {
            (answerRevision.PostId, "a revision of an answer"), (commentRevision.PostId, "a revision of a comment"),
            (scene.Question.PostId, "a question"), (scene.Answer1.PostId, "an answer"), (scene.Comment.PostId, "a comment"),
        })
        {
            Assert.False((await GetPostAsync(id, ct)).GetProperty("provenance").TryGetProperty("revises", out _), $"{what} carries provenance.revises");
        }
    }

    // ---- ACA2 -------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// ACA2. <c>accepted_at</c> on the question's <c>accepted_answer</c> equals, byte for byte, the <c>accepted_at</c> of the first accept's receipt and of a replay's receipt (R8.68, the receipt's own form).
    /// The receipt is the oracle (the route's string, not the index's). Task 0's K4 measured the two equal on a real store; this holds the test on the fixture's. Red on the tree (no member).
    /// </summary>
    [Fact]
    public async Task R9_71_ACA2_TheAcceptanceInstantIsTheReceiptsStringByteForByte()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);

        var (first, receipt) = await AcceptReceiptAsync(scene, scene.Answer1, ct);
        Assert.Equal(HttpStatusCode.Created, first);
        var (replayStatus, replay) = await AcceptReceiptAsync(scene, scene.Answer1, ct);
        Assert.Equal(HttpStatusCode.OK, replayStatus);
        var expected = receipt.GetProperty("accepted_at").GetString();
        Assert.Equal(expected, replay.GetProperty("accepted_at").GetString());

        var member = AcceptedAnswerOf(await GetPostAsync(scene.Question.PostId, ct));
        Assert.True(member.TryGetProperty("accepted_at", out var served), $"Expected: accepted_answer.accepted_at Actual: {member.GetRawText()}");
        Assert.Equal(expected, served.GetString());
    }

    // ---- ACA3 -------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static string? FirstRevisionOf(JsonElement member) =>
        member.TryGetProperty("first_revision", out var relation) ? relation.GetString() : null;

    /// <summary>ACA3, order one: accept the answer, then its author revises it: <c>after_acceptance</c> (the revision is on the list, so the member is present). Red on the tree.</summary>
    [Fact]
    public async Task R9_71_ACA3_AcceptThenReviseIsAfterAcceptance()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        await AcceptReceiptAsync(scene, scene.Answer1, ct);
        await ReviseAnswerAsync(scene, scene.Answer1, "A corrected first answer.", ct);

        var member = AcceptedAnswerOf(await GetPostAsync(scene.Question.PostId, ct));

        Assert.NotEqual(0, member.GetProperty("superseded_by").GetArrayLength());
        Assert.Equal("after_acceptance", FirstRevisionOf(member));
    }

    /// <summary>ACA3, order two: the author revises the answer, then the asker accepts the original (R8.71 allows it): <c>before_acceptance</c>. Red on the tree.</summary>
    [Fact]
    public async Task R9_71_ACA3_ReviseThenAcceptTheOriginalIsBeforeAcceptance()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        await ReviseAnswerAsync(scene, scene.Answer1, "A corrected first answer.", ct);
        await AcceptReceiptAsync(scene, scene.Answer1, ct);

        var member = AcceptedAnswerOf(await GetPostAsync(scene.Question.PostId, ct));

        Assert.NotEqual(0, member.GetProperty("superseded_by").GetArrayLength());
        Assert.Equal("before_acceptance", FirstRevisionOf(member));
    }

    /// <summary>
    /// ACA3, order three: accept the original, revise it, accept the revision. The member names the revision, whose own list is empty, so no relation is stated (first_revision is present if and only if
    /// <c>superseded_by</c> is not empty). Red on the tree for the instant; the absence is green on the tree (the member does not exist), which is why the instant is asserted first.
    /// </summary>
    [Fact]
    public async Task R9_71_ACA3_AcceptingTheRevisionStatesNoRelation()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        await AcceptReceiptAsync(scene, scene.Answer1, ct);
        var revision = await ReviseAnswerAsync(scene, scene.Answer1, "A corrected first answer.", ct);
        var (_, receipt) = await AcceptReceiptAsync(scene, revision, ct);

        var member = AcceptedAnswerOf(await GetPostAsync(scene.Question.PostId, ct));

        Assert.Equal(revision.PostId, member.GetProperty("post_id").GetString());
        Assert.Equal(0, member.GetProperty("superseded_by").GetArrayLength());
        Assert.True(member.TryGetProperty("accepted_at", out var instant), $"Expected: accepted_at on the revision's acceptance Actual: {member.GetRawText()}");
        Assert.Equal(receipt.GetProperty("accepted_at").GetString(), instant.GetString());
        Assert.False(member.TryGetProperty("first_revision", out _), "first_revision is present though the named post has no revision");
    }

    /// <summary>ACA3's control (green on the tree): an acceptance of an unrevised answer carries no <c>first_revision</c>.</summary>
    [Fact]
    public async Task R9_71_ACA3_Control_AnUnrevisedAnswerCarriesNoRelation()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        await AcceptReceiptAsync(scene, scene.Answer2, ct);

        var member = AcceptedAnswerOf(await GetPostAsync(scene.Question.PostId, ct));

        Assert.False(member.TryGetProperty("first_revision", out _));
    }

    /// <summary>
    /// ACA3, a fork: two revisions of the accepted answer, the acceptance between them. The relation is of the first in log order: <c>before_acceptance</c>. Red on the tree.
    /// <b>The route refuses a second revision of one post</b> (<c>curia/ingest/already-revised</c>, errata G45), so the fork the plan asks for cannot be made through it; the second revision is appended
    /// to the store as <see cref="ForumFixture.AppendStoredPostAsync"/> appends a post accepted before that rule (the shape a fork has in a log this Forum did not write), and the test says so.
    /// </summary>
    [Fact]
    public async Task R9_71_ACA3_AForkIsDecidedByTheFirstRevisionInLogOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        await ReviseAnswerAsync(scene, scene.Answer1, "One correction.", ct);
        await AcceptReceiptAsync(scene, scene.Answer1, ct);
        await forum.AppendStoredPostAsync(scene.Answerer.Agent.SignRevision(scene.Board, "Another correction.", scene.Answer1.PostId, scene.Answer1.Digest, forum.Now), ct);

        var member = AcceptedAnswerOf(await GetPostAsync(scene.Question.PostId, ct));

        Assert.Equal(2, member.GetProperty("superseded_by").GetArrayLength());
        Assert.Equal("before_acceptance", FirstRevisionOf(member));
    }

    // ---- ACA5 -------------------------------------------------------------------------------------------------------------------------------------------------------------

    private static string? AnswerPosted(JsonElement answer, out JsonElement revised)
    {
        Assert.True(answer.GetProperty("provenance").TryGetProperty("question_revised", out revised), "provenance.question_revised is absent on an answer (R9.71 (b): it SHALL NOT be absent)");
        return revised.ValueKind == JsonValueKind.Null ? null : revised.GetProperty("answer_posted").GetString();
    }

    /// <summary>
    /// ACA5, the three orders and two revisions. The test posts, in this order: a question, an answer E, the asker's revision R1, an answer B, the asker's revision R2 (of R1), an answer L. E is <c>before</c>;
    /// B and L are <c>after</c> (the relation is to the FIRST revision, R1, so an answer between the two revisions is after); every one names R1 by digest and post id. Red on the tree.
    /// </summary>
    [Fact]
    public async Task R9_71_ACA5_AnAnswerIsRelatedToTheFirstRevisionByTheOrderItWasPostedIn()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "aca5-" + Guid.NewGuid().ToString("N")[..8];
        var asker = await Scene.EnrolAsync(forum, "asker", ct);
        var question = await Scene.PostAsync(forum, asker, asker.Agent.SignQuestion(board, "How is aca5 settled " + Guid.NewGuid().ToString("N"), "A question that is revised", forum.Now), ct);
        var answerer = await Scene.T1Async(forum, board, ct);
        var early = await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "An answer before the revision.", question.PostId, forum.Now), ct);
        var r1 = await Scene.PostAsync(forum, asker, asker.Agent.SignRevision(board, "The question, revised once.", question.PostId, question.Digest, forum.Now), ct);
        var between = await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "An answer between the revisions.", question.PostId, forum.Now), ct);
        await Scene.PostAsync(forum, asker, asker.Agent.SignRevision(board, "The question, revised twice.", question.PostId, r1.Digest, forum.Now), ct);
        var late = await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "An answer after both.", question.PostId, forum.Now), ct);

        foreach (var (post, expected) in new[] { (early, "before"), (between, "after"), (late, "after") })
        {
            var posted = AnswerPosted(await GetPostAsync(post.PostId, ct), out var revised);
            Assert.Equal(expected, posted);
            Assert.Equal(r1.Digest, revised.GetProperty("first_revision").GetProperty("digest").GetString());
            Assert.Equal(r1.PostId, revised.GetProperty("first_revision").GetProperty("post_id").GetString());
        }
    }

    /// <summary>ACA5: an answer to a question nobody revised carries <c>question_revised</c> as <c>null</c>, present and not absent. Red on the tree (absent).</summary>
    [Fact]
    public async Task R9_71_ACA5_AnAnswerToAnUnrevisedQuestionCarriesNullAndNotNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        var board = "aca5b-" + Guid.NewGuid().ToString("N")[..8];
        var asker = await Scene.EnrolAsync(forum, "asker", ct);
        var question = await Scene.PostAsync(forum, asker, asker.Agent.SignQuestion(board, "How is aca5b settled " + Guid.NewGuid().ToString("N"), "A question nobody revises", forum.Now), ct);
        var answerer = await Scene.T1Async(forum, board, ct);
        var answer = await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "An answer.", question.PostId, forum.Now), ct);

        Assert.Null(AnswerPosted(await GetPostAsync(answer.PostId, ct), out var revised));
        Assert.Equal(JsonValueKind.Null, revised.ValueKind);
        Assert.NotNull(scene);
    }

    /// <summary>ACA5's control (green on the tree): a question, a comment and a revision carry no <c>question_revised</c>; only answers do.</summary>
    [Fact]
    public async Task R9_71_ACA5_Control_NoPostThatIsNotAnAnswerCarriesTheMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);

        foreach (var id in new[] { scene.Question.PostId, scene.Comment.PostId, scene.Revision.PostId })
            Assert.False((await GetPostAsync(id, ct)).GetProperty("provenance").TryGetProperty("question_revised", out _));
    }

    // ---- INX6 -------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// INX6. An anonymous <c>GET /v1/inbox</c> is refused <c>401</c>. The independent artifact that <c>curia_inbox</c>'s description is held against (INX4): a description and a route are two documents of one
    /// fact, and Table 10's <c>thread | search</c> span, which would print "Requires no credential", is not this route's gate. Green on the tree (a control; the route exists).
    /// </summary>
    [Fact]
    public async Task R11_104_INX6_AnAnonymousInboxReadIsRefused401()
    {
        var ct = TestContext.Current.CancellationToken;

        using var response = await forum.Client.GetAsync(new Uri("/v1/inbox", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
