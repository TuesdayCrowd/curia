using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R8.69 (errata G36): the accept route refuses an answer whose parent is not a question, <c>400</c> <c>curia/accept/root-not-a-question</c>, after
/// the ownership check, so a caller who is not the thread's asker is still answered <c>403</c>.
///
/// <para><b>Oracles.</b> The answer is built through the API (ingest still admits an answer under a comment or an answer, D73), the status and the
/// problem's type are read from the response, and "nothing was recorded" is the <c>events</c> table: no <c>answer.accepted</c> row names the answer,
/// read by SQL on the payload and not by any fold of the code under test. The control, an answer under a question, still answers <c>201</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptRootKindTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private async Task<HttpResponseMessage> AcceptAsync(SceneActor actor, string answerId, CancellationToken ct) =>
        await actor.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{answerId}/accept",
            await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct),
            Encoding.UTF8.GetBytes("{}"), forum.Now, ct, contentType: "application/json");

    private async Task<long> AcceptanceEventsNamingAsync(string answerId, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE event_type = 'answer.accepted' AND payload->>'answer_id' = @answer;", connection);
        count.Parameters.AddWithValue("answer", answerId);
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }

    [Theory]
    [InlineData("comment")]
    [InlineData("answer")]
    public async Task R8_69_AnAnswerWhoseParentIsNotAQuestionCannotBeAccepted(string rootKind)
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);

        // The thread root is the asker's own comment, or the answerer's own answer: its author passes the ownership check.
        var (rootPost, owner) = rootKind == "comment"
            ? (scene.Comment, scene.Asker)
            : (scene.Answer1, scene.Answerer);
        var nested = await AcceptedAnswerScene.PostAsync(
            forum, scene.Answerer, scene.Answerer.Agent.SignAnswer(scene.Board, $"Under a {rootKind}, {scene.Term}.", rootPost.PostId, forum.Now), ct);

        using var response = await AcceptAsync(owner, nested.PostId, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("curia/accept/root-not-a-question", body, StringComparison.Ordinal);
        Assert.Contains($"kind={rootKind}", body, StringComparison.Ordinal);
        Assert.Contains($"root={rootPost.PostId}", body, StringComparison.Ordinal);
        Assert.Equal(0, await AcceptanceEventsNamingAsync(nested.PostId, ct));
    }

    /// <summary>
    /// R8.69's order: the root-kind check comes after the ownership check, so a caller who is not the root's author is answered 403 with the
    /// ownership problem and learns nothing of the root's kind.
    /// </summary>
    [Theory]
    [InlineData("comment")]
    [InlineData("answer")]
    public async Task R8_69_ACallerWhoIsNotTheRootsAuthorIsStillToldForbiddenAndNeverTheRootsKind(string rootKind)
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        var rootPost = rootKind == "comment" ? scene.Comment : scene.Answer1;
        var nested = await AcceptedAnswerScene.PostAsync(
            forum, scene.Answerer, scene.Answerer.Agent.SignAnswer(scene.Board, $"Under a {rootKind}, {scene.Term}.", rootPost.PostId, forum.Now), ct);
        var stranger = await AcceptedAnswerScene.EnrolAsync(forum, "stranger", ct);

        using var response = await AcceptAsync(stranger, nested.PostId, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("own-thread-only", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("curia/accept/root-not-a-question", body, StringComparison.Ordinal);
        Assert.DoesNotContain($"kind={rootKind}", body, StringComparison.Ordinal);
        Assert.Equal(0, await AcceptanceEventsNamingAsync(nested.PostId, ct));
    }

    /// <summary>
    /// R8.69 stays after R8.70 (errata G38): a log written before the rule can hold an answer under a comment or an answer, and the accept route is
    /// the second line of defence for it. The nested answer is stored as such a log holds it (<c>AppendStoredPostAsync</c>, the shape of a post accepted
    /// before a rule), because the submit route no longer takes it. The root's author is answered <c>400</c> <c>curia/accept/root-not-a-question</c>
    /// after the ownership check, a stranger <c>403</c>, and no <c>answer.accepted</c> row names it.
    /// </summary>
    [Theory]
    [InlineData("comment")]
    [InlineData("answer")]
    public async Task R8_69_AStoredAnswerUnderANonQuestionIsStillRefusedByTheAcceptRoute(string rootKind)
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        var (rootPost, owner) = rootKind == "comment"
            ? (scene.Comment, scene.Asker)
            : (scene.Answer1, scene.Answerer);
        var (nestedId, _) = await forum.AppendStoredPostAsync(
            scene.Answerer.Agent.SignAnswer(scene.Board, $"Stored under a {rootKind}, {scene.Term}.", rootPost.PostId, forum.Now), ct);
        var stranger = await AcceptedAnswerScene.EnrolAsync(forum, "stranger", ct);

        using var forbidden = await AcceptAsync(stranger, nestedId, ct);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var response = await AcceptAsync(owner, nestedId, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("curia/accept/root-not-a-question", body, StringComparison.Ordinal);
        Assert.Contains($"kind={rootKind}", body, StringComparison.Ordinal);
        Assert.Equal(0, await AcceptanceEventsNamingAsync(nestedId, ct));
    }

    /// <summary>Control: an answer under a question is still accepted, and the log holds the one acceptance.</summary>
    [Fact]
    public async Task R8_69_AnAnswerUnderAQuestionIsStillAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);

        using var response = await AcceptAsync(scene.Asker, scene.Answer1.PostId, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await AcceptanceEventsNamingAsync(scene.Answer1.PostId, ct));
        using var receipt = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(scene.Question.PostId, receipt.RootElement.GetProperty("thread_root").GetString());
    }
}
