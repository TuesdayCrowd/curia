using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R8.72 (errata G41) over a real Forum: an answer or a comment whose <c>parent</c> names a post that exists and that moderation does not serve
/// (withheld or quarantined) is refused <c>400</c> <c>curia/ingest/parent-withheld</c>, naming the parent, after the caller is authorized and after
/// R8.70's existence and kind checks, and before the submission is screened or stored; a revision, a question and a finding are not refused.
///
/// <para><b>Oracles.</b> The <c>events</c> table read by SQL (the <c>post.accepted</c> rows, the test's own moderation events and their order), the
/// status and the problem's type and detail, the stub of a failing log, and the type and status parsed from the errata at test time by
/// <c>PublishedWithheldWords</c>. Never <c>Servable</c>'s report of itself. Each negative fact has a control that must pass: the same
/// submission under a post that is served is <c>201</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class WithheldParentIngestTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private async Task<(HttpStatusCode Status, string Body)> SubmitAsync(SceneActor actor, byte[] wire, CancellationToken ct, HttpClient? client = null)
    {
        client ??= forum.Client;
        using var response = await actor.Dpop.PostAsync(
            client, PostsUrl, await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct), wire, forum.Now, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<long> PostsAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events WHERE event_type = 'post.accepted'", connection);
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }

    private async Task<long> PostsByAsync(string author, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'author' = @author", connection);
        count.Parameters.AddWithValue("author", author);
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }

    private static string TypeOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("type").GetString()!;

    private static string DetailOf(string body)
    {
        var detail = JsonDocument.Parse(body).RootElement.GetProperty("detail");
        return detail.ValueKind == JsonValueKind.String ? detail.GetString()! : string.Empty;
    }

    private byte[] AnswerOf(AcceptedAnswerScene scene, SceneActor author, string parent, string text) =>
        author.Agent.SignAnswer(scene.Board, $"{text} {scene.Term}.", parent, forum.Now);

    private byte[] CommentOf(AcceptedAnswerScene scene, SceneActor author, string parent, string text) =>
        author.Agent.Sign(PostKind.Comment, scene.Board, $"{text} {scene.Term}.", null, parent, forum.Now);

    /// <summary>A second question on the scene's board, by an asker of its own (the scene's asker has spent its day's budget), that nothing withholds.</summary>
    private async Task<string> ServedQuestionAsync(AcceptedAnswerScene scene, CancellationToken ct)
    {
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "asker-served", ct);
        var word = "yarrow" + Suffix();
        return (await AcceptedAnswerScene.PostAsync(forum, asker, asker.Agent.SignQuestion(scene.Board, $"A different question about {word}.", $"About {word}", forum.Now), ct)).PostId;
    }

    private static void AssertRefusedAsWithheld(HttpStatusCode status, string body, string parent, string what)
    {
        Assert.True(status == (HttpStatusCode)PublishedWithheldWords.ParentWithheldStatus(), $"{what} answered {(int)status}: {body}");
        Assert.Equal(PublishedWithheldWords.ParentWithheldType(), TypeOf(body));
        Assert.Equal($"parent={parent}", DetailOf(body));
    }

    /// <summary>PH6. R8.72: an answer under a withheld question is refused <c>parent-withheld</c> naming it, nothing is stored and the author's budget is unspent; the control, an answer under a served question, is <c>201</c>.</summary>
    [Fact]
    public async Task R8_72_AnAnswerUnderAWithheldQuestionIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        var other = await ServedQuestionAsync(scene, ct);
        await forum.WithholdAsync(scene.Question.PostId, ct);
        var all = await PostsAsync(ct);
        var mine = await PostsByAsync(scene.Answerer.Agent.AgentId, ct);

        var (status, body) = await SubmitAsync(scene.Answerer, AnswerOf(scene, scene.Answerer, scene.Question.PostId, "Under a withheld question"), ct);

        AssertRefusedAsWithheld(status, body, scene.Question.PostId, "an answer under a withheld question");
        Assert.Equal(all, await PostsAsync(ct));
        Assert.Equal(mine, await PostsByAsync(scene.Answerer.Agent.AgentId, ct));

        var (controlStatus, controlBody) = await SubmitAsync(scene.Answerer, AnswerOf(scene, scene.Answerer, other, "Under a served question"), ct);
        Assert.True(controlStatus == HttpStatusCode.Created, controlBody);
        Assert.Equal(all + 1, await PostsAsync(ct));
    }

    /// <summary>PH7. R8.72: a comment under a withheld answer, and under a quarantined comment, is refused the same way; the control, a comment under a served answer, is <c>201</c>.</summary>
    [Theory]
    [InlineData("withheld-answer")]
    [InlineData("quarantined-comment")]
    public async Task R8_72_ACommentUnderAWithheldAnswerIsRefused(string row)
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        var parent = row == "withheld-answer" ? scene.Answer1 : scene.Comment;
        if (row == "withheld-answer") await forum.WithholdAsync(parent.PostId, ct);
        else await forum.QuarantineAsync(parent.PostId, ct);
        var all = await PostsAsync(ct);
        var mine = await PostsByAsync(scene.Answerer.Agent.AgentId, ct);

        var (status, body) = await SubmitAsync(scene.Answerer, CommentOf(scene, scene.Answerer, parent.PostId, $"Under a {row}"), ct);

        AssertRefusedAsWithheld(status, body, parent.PostId, $"a comment under a {row}");
        Assert.Equal(all, await PostsAsync(ct));
        Assert.Equal(mine, await PostsByAsync(scene.Answerer.Agent.AgentId, ct));

        var (controlStatus, controlBody) = await SubmitAsync(scene.Answerer, CommentOf(scene, scene.Answerer, scene.Answer2.PostId, "Under a served answer"), ct);
        Assert.True(controlStatus == HttpStatusCode.Created, controlBody);
    }

    /// <summary>
    /// PH8. R8.72, decision 4: the check follows AUTHORIZE, so a caller denied is told why and never whether a post exists. A T0 agent answering under a
    /// withheld question is <c>403</c> <c>table-10/denied</c> with the body it gets under a served one; a T0 agent whose day's budget is spent and who
    /// comments under a withheld question is <c>403</c> with the body it gets under a served one. The control: a T0 comment under a served post is <c>201</c>.
    /// </summary>
    [Fact]
    public async Task R8_72_AnUnderTierCallerIsToldTheTierAndNothingAboutThePost()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        var served = await ServedQuestionAsync(scene, ct);
        await forum.WithholdAsync(scene.Question.PostId, ct);
        var stranger = await AcceptedAnswerScene.EnrolAsync(forum, "stranger", ct);

        var (underWithheld, withheldBody) = await SubmitAsync(stranger, AnswerOf(scene, stranger, scene.Question.PostId, "A T0 answer"), ct);
        var (underServed, servedBody) = await SubmitAsync(stranger, AnswerOf(scene, stranger, served, "A T0 answer"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, underWithheld);
        Assert.Equal(HttpStatusCode.Forbidden, underServed);
        Assert.Contains("table-10/denied", withheldBody, StringComparison.Ordinal);
        Assert.Equal(servedBody, withheldBody);

        var spender = await AcceptedAnswerScene.EnrolAsync(forum, "spender", ct);
        for (var i = 0; i < Curia.Domain.Authorization.TierPolicy.PostsPerDay(Curia.Domain.Authorization.PrincipalTier.T0); i++)
        {
            var (spent, spentBody) = await SubmitAsync(spender, CommentOf(scene, spender, served, $"Budget {i}"), ct);
            Assert.True(spent == HttpStatusCode.Created, spentBody);
        }

        var (budgetWithheld, budgetWithheldBody) = await SubmitAsync(spender, CommentOf(scene, spender, scene.Question.PostId, "Over the budget"), ct);
        var (budgetServed, budgetServedBody) = await SubmitAsync(spender, CommentOf(scene, spender, served, "Over the budget"), ct);
        Assert.Equal(HttpStatusCode.Forbidden, budgetWithheld);
        Assert.Equal(HttpStatusCode.Forbidden, budgetServed);
        Assert.Equal(budgetServedBody, budgetWithheldBody);

        var fresh = await AcceptedAnswerScene.EnrolAsync(forum, "fresh", ct);
        var (control, controlBody) = await SubmitAsync(fresh, CommentOf(scene, fresh, served, "A T0 comment under a served post"), ct);
        Assert.True(control == HttpStatusCode.Created, controlBody);
    }

    /// <summary>
    /// PH9. R8.72, ruling 4: the existence and kind checks come first. An answer under a withheld comment, and under a withheld answer, is
    /// <c>parent-not-a-question</c> naming the kind; a comment and an answer under an invented id are <c>parent-not-found</c>.
    /// </summary>
    [Fact]
    public async Task R8_72_TheExistenceAndKindChecksComeFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        await forum.WithholdAsync(scene.Comment.PostId, ct);
        await forum.WithholdAsync(scene.Answer1.PostId, ct);

        foreach (var (kind, parent) in new[] { ("comment", scene.Comment), ("answer", scene.Answer1) })
        {
            var (status, body) = await SubmitAsync(scene.Answerer, AnswerOf(scene, scene.Answerer, parent.PostId, $"Under a withheld {kind}"), ct);
            Assert.True(status == (HttpStatusCode)PublishedParentRule.Statuses().Refused, $"an answer under a withheld {kind} answered {(int)status}: {body}");
            Assert.Equal(PublishedParentRule.Slugs()[1], TypeOf(body));
            Assert.Contains($"kind={kind}", DetailOf(body), StringComparison.Ordinal);
        }

        var invented = "01" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();
        foreach (var wire in new[] { AnswerOf(scene, scene.Answerer, invented, "Under no post"), CommentOf(scene, scene.Answerer, invented, "Under no post") })
        {
            var (status, body) = await SubmitAsync(scene.Answerer, wire, ct);
            Assert.True(status == (HttpStatusCode)PublishedParentRule.Statuses().Refused, $"a reply under no post answered {(int)status}: {body}");
            Assert.Equal(PublishedParentRule.Slugs()[0], TypeOf(body));
        }
    }

    /// <summary>
    /// PH10. R8.72: a revision is not refused. The author of a withheld answer signs a revision chained to its digest and it is <c>201</c>, stored with
    /// that digest as its <c>prev</c>; a withheld post's own question, signed with the withheld post as its <c>parent</c>, is not consulted either.
    /// </summary>
    [Fact]
    public async Task R8_72_ARevisionOfAWithheldPostIsStillAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        await forum.WithholdAsync(scene.Answer1.PostId, ct);

        var (status, body) = await SubmitAsync(
            scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, $"A corrected first answer about {scene.Term}.", scene.Answer1.PostId, scene.Answer1.Digest, forum.Now), ct);

        Assert.True(status == HttpStatusCode.Created, $"a revision of a withheld answer answered {(int)status}: {body}");
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(ct);
        await using var chained = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'kind' = 'revision' AND (payload->>'canonical')::jsonb->>'prev' = @digest", connection);
        chained.Parameters.AddWithValue("digest", scene.Answer1.Digest);
        Assert.Equal(1L, (long)(await chained.ExecuteScalarAsync(ct))!);
    }

    /// <summary>
    /// PH10. R8.72: only an answer or a comment is consulted. A question may not carry a <c>parent</c> at all, so ADMIT refuses it
    /// (<c>curia/content/parent-not-allowed</c>) before any parent is looked up, withheld or not (falsification row FPH14 is green for
    /// this reason: the widened check is unreachable).
    /// </summary>
    [Fact]
    public async Task R8_72_AQuestionAndAFindingAreNotConsulted()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        await forum.WithholdAsync(scene.Answer1.PostId, ct);
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "asker2", ct);

        var wire = asker.Agent.Sign(PostKind.Question, scene.Board, $"A question that names a parent {Suffix()}.", $"Not a reply {Suffix()}", scene.Answer1.PostId, forum.Now);
        var (status, body) = await SubmitAsync(asker, wire, ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("curia/content/parent-not-allowed", TypeOf(body));
    }

    /// <summary>PH11. R8.72: after a restore the parent takes replies. The state is the log's on the request.</summary>
    [Fact]
    public async Task R8_72_ARestoredParentTakesReplies()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        await forum.WithholdAsync(scene.Question.PostId, ct);
        await forum.RestoreAsync(scene.Question.PostId, ct);

        var (answer, answerBody) = await SubmitAsync(scene.Answerer, AnswerOf(scene, scene.Answerer, scene.Question.PostId, "Under a restored question"), ct);
        var (comment, commentBody) = await SubmitAsync(scene.Answerer, CommentOf(scene, scene.Answerer, scene.Question.PostId, "Under a restored question"), ct);

        Assert.True(answer == HttpStatusCode.Created, $"an answer under a restored question answered {(int)answer}: {answerBody}");
        Assert.True(comment == HttpStatusCode.Created, $"a comment under a restored question answered {(int)comment}: {commentBody}");
    }

    /// <summary>PH11. R8.72: a log that cannot be read for the parent is the Forum's failure, <c>503</c>, and never <c>parent-withheld</c>, withheld parent or not.</summary>
    [Fact]
    public async Task R8_72_AnUnreadableLogIsA503()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        await forum.WithholdAsync(scene.Question.PostId, ct);
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton<IEventReader>(sp => new ParentStreamUnreadable(sp.GetRequiredService<IEventStore>(), scene.Question.PostId))));
        using var client = host.CreateClient();
        var all = await PostsAsync(ct);

        var (status, body) = await SubmitAsync(scene.Answerer, AnswerOf(scene, scene.Answerer, scene.Question.PostId, "While the log cannot be read"), ct, client);

        Assert.True(status == (HttpStatusCode)PublishedParentRule.Statuses().Unreadable, $"an unreadable parent stream answered {(int)status}: {body}");
        Assert.NotEqual(PublishedWithheldWords.ParentWithheldType(), TypeOf(body));
        Assert.Equal(all, await PostsAsync(ct));
    }

    /// <summary>
    /// PH22. R8.72, ruling 5: the moderation state is read from the log on the request, with no restart in between. A reply under a question is
    /// <c>201</c>; the question is withheld by an appended event and the same reply is refused; the withholding is restored by an appended event and it is
    /// <c>201</c> again. The oracle is the test's own events, in the order it appended them.
    /// </summary>
    [Fact]
    public async Task R8_72_TheModerationStateIsReadFromTheLog()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);

        var (before, beforeBody) = await SubmitAsync(scene.Answerer, CommentOf(scene, scene.Answerer, scene.Question.PostId, "Before the withholding"), ct);
        Assert.True(before == HttpStatusCode.Created, beforeBody);

        await forum.WithholdAsync(scene.Question.PostId, ct);
        var (during, duringBody) = await SubmitAsync(scene.Answerer, CommentOf(scene, scene.Answerer, scene.Question.PostId, "During the withholding"), ct);
        AssertRefusedAsWithheld(during, duringBody, scene.Question.PostId, "a comment under a question withheld by an appended event");

        await forum.RestoreAsync(scene.Question.PostId, ct);
        var (after, afterBody) = await SubmitAsync(scene.Answerer, CommentOf(scene, scene.Answerer, scene.Question.PostId, "After the restore"), ct);
        Assert.True(after == HttpStatusCode.Created, afterBody);
    }

    /// <summary>Refuses the read of one stream, the parent's; every other read goes to the real store.</summary>
    private sealed class ParentStreamUnreadable(IEventReader store, string parent) : IEventReader
    {
        private static readonly Error Refused = new("test/log-unreadable", "The test's reader refused the read");

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
            string.Equals(aggregateId.Value, parent, StringComparison.Ordinal)
                ? Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(Refused))
                : store.ReadByAggregateAsync(aggregateId, cancellationToken);

        public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default) =>
            store.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
    }
}
