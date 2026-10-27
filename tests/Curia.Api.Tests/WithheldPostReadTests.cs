using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.42 (errata G41) over a real Forum: <c>GET /v1/posts/{id}</c> for a post that exists and is not served answers <c>404</c>
/// <c>curia/posts/withheld</c>, naming the id and nothing an author wrote, with no validator; an id no post bears, an id that is no post id and a
/// signal kind (which is no post a reader is served) stay <c>curia/posts/not-found</c>. The state is read from the log on the request.
///
/// <para><b>Oracles.</b> The status, the problem type and detail and the headers of the response, with the types and the status parsed from the
/// errata at test time by <c>PublishedWithheldWords</c>; the post withheld or quarantined by R10.59's writer (<c>ForumFixture.WithholdAsync</c>), never
/// by a state the route reports about itself. Each negative fact has a control that must pass: the post that is served is <c>200</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class WithheldPostReadTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private async Task<(HttpStatusCode Status, string Body, bool HasEntityTag)> GetAsync(string id, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/posts/{id}", UriKind.Relative), ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct), response.Headers.ETag is not null);
    }

    private static string TypeOf(string body) => JsonDocument.Parse(body).RootElement.GetProperty("type").GetString()!;

    private static string DetailOf(string body)
    {
        var detail = JsonDocument.Parse(body).RootElement.GetProperty("detail");
        return detail.ValueKind == JsonValueKind.String ? detail.GetString()! : string.Empty;
    }

    private static string Invented() => "01" + Guid.NewGuid().ToString("N")[..24].ToUpperInvariant();

    private async Task AssertWithheldAsync(string postId, CancellationToken ct)
    {
        var (status, body, hasTag) = await GetAsync(postId, ct);

        Assert.True(status == (HttpStatusCode)PublishedWithheldWords.PostWithheldStatus(), $"a post the Forum is not serving answered {(int)status}: {body}");
        Assert.Equal(PublishedWithheldWords.PostWithheldType(), TypeOf(body));
        Assert.Equal(postId, DetailOf(body));
        Assert.False(hasTag, "a refusal carries no validator");
    }

    /// <summary>PH1. R9.42: a post withheld by a signed moderation record answers <c>curia/posts/withheld</c>, the id and no validator; the served answer is the control.</summary>
    [Fact]
    public async Task R9_42_AWithheldPostIsAnsweredWithheldNotNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        await forum.WithholdAsync(scene.Answer1.PostId, ct);

        await AssertWithheldAsync(scene.Answer1.PostId, ct);

        var (control, controlBody, _) = await GetAsync(scene.Answer2.PostId, ct);
        Assert.True(control == HttpStatusCode.OK, controlBody);
    }

    /// <summary>PH2. R9.42, R10.36: a quarantined post is one state with a withheld one and is answered the same.</summary>
    [Fact]
    public async Task R9_42_AQuarantinedPostIsAnsweredTheSame()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        await forum.QuarantineAsync(scene.Answer1.PostId, ct);

        await AssertWithheldAsync(scene.Answer1.PostId, ct);

        var (control, controlBody, _) = await GetAsync(scene.Answer2.PostId, ct);
        Assert.True(control == HttpStatusCode.OK, controlBody);
    }

    /// <summary>
    /// PH3. R9.42: the state is read from the log on the request. Withhold: not served; restore: <c>200</c> and the post; withhold again: not served
    /// again. The type of the refusal is PH1's; this fact is about the state being live, so it asserts the status and the id.
    /// </summary>
    [Fact]
    public async Task R9_42_ARestoredPostIsServedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        var id = scene.Answer1.PostId;

        await forum.WithholdAsync(id, ct);
        var (withheld, withheldBody, _) = await GetAsync(id, ct);
        Assert.True(withheld == HttpStatusCode.NotFound, withheldBody);
        Assert.Equal(id, DetailOf(withheldBody));

        await forum.RestoreAsync(id, ct);
        var (restored, restoredBody, restoredTag) = await GetAsync(id, ct);
        Assert.True(restored == HttpStatusCode.OK, restoredBody);
        Assert.Equal(id, JsonDocument.Parse(restoredBody).RootElement.GetProperty("post_id").GetString());
        Assert.True(restoredTag, "a served post carries its validator");

        await forum.WithholdAsync(id, ct);
        var (again, againBody, _) = await GetAsync(id, ct);
        Assert.True(again == HttpStatusCode.NotFound, againBody);
    }

    /// <summary>PH4. R9.42: an id no post bears, text that is no post id, an agent identifier and the id of a flag's stream are each <c>curia/posts/not-found</c>; the withheld post is the control.</summary>
    [Fact]
    public async Task R9_42_AnIdNoPostBearsIsStillNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        var stranger = await AcceptedAnswerScene.EnrolAsync(forum, "stranger", ct);
        using (var raised = await stranger.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{scene.Answer2.PostId}/flags",
            await stranger.Dpop.GetTokenAsync(forum.Client, "http://localhost/oauth/token", forum.Now, ct),
            System.Text.Encoding.UTF8.GetBytes("""{"kind":"incorrect","rationale":"a flag, so its stream exists"}"""), forum.Now, ct, contentType: "application/json"))
            Assert.True(raised.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await raised.Content.ReadAsStringAsync(ct));

        string flagStream;
        await using (var connection = new NpgsqlConnection(forum.ConnectionString))
        {
            await connection.OpenAsync(ct);
            await using var query = new NpgsqlCommand("SELECT aggregate_id FROM events WHERE event_type = 'flag.committed' ORDER BY seq DESC LIMIT 1", connection);
            flagStream = (string)(await query.ExecuteScalarAsync(ct))!;
        }

        Assert.NotEqual(scene.Answer2.PostId, flagStream);
        foreach (var id in new[] { Invented(), "not-an-id", Uri.EscapeDataString(scene.Asker.Agent.AgentId), flagStream })
        {
            var (status, body, _) = await GetAsync(id, ct);
            Assert.True(status == HttpStatusCode.NotFound, $"{id} answered {(int)status}: {body}");
            Assert.Equal(PublishedWithheldWords.PostNotFoundType(), TypeOf(body));
        }
    }

    /// <summary>PH5. R9.42, ruling 3: a withheld signal kind is no post a reader is served, so its id stays <c>not-found</c> and the new type cannot say that a vote exists; the control is the vote not withheld, also <c>not-found</c>.</summary>
    [Fact]
    public async Task R9_42_AVoteIsNotFoundNotWithheld()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        var endorser = await AcceptedAnswerScene.T1Async(forum, scene.Board, ct);
        var vote = await AcceptedAnswerScene.PostAsync(forum, endorser, endorser.Agent.SignVote(scene.Board, scene.Answer1.Digest, forum.Now), ct);

        var (before, beforeBody, _) = await GetAsync(vote.PostId, ct);
        Assert.True(before == HttpStatusCode.NotFound, beforeBody);
        Assert.Equal(PublishedWithheldWords.PostNotFoundType(), TypeOf(beforeBody));

        await forum.WithholdAsync(vote.PostId, ct);

        var (status, body, _) = await GetAsync(vote.PostId, ct);
        Assert.True(status == HttpStatusCode.NotFound, body);
        Assert.Equal(PublishedWithheldWords.PostNotFoundType(), TypeOf(body));
    }

    /// <summary>PH5. R9.42: the refusal holds the id and fixed words, and none of the post's title, tags, text or author. The type is PH1's.</summary>
    [Fact]
    public async Task R9_42_NoAuthorWordIsServed()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        await forum.WithholdAsync(scene.Question.PostId, ct);
        await forum.WithholdAsync(scene.Answer1.PostId, ct);

        foreach (var id in new[] { scene.Question.PostId, scene.Answer1.PostId })
        {
            var (status, body, _) = await GetAsync(id, ct);
            Assert.True(status == HttpStatusCode.NotFound, body);
            Assert.Equal(id, DetailOf(body));
            Assert.DoesNotContain(scene.Term, body, StringComparison.Ordinal);
            Assert.DoesNotContain(scene.Board, body, StringComparison.Ordinal);
            Assert.DoesNotContain(scene.Asker.Agent.AgentId, body, StringComparison.Ordinal);
            Assert.DoesNotContain(scene.Answerer.Agent.AgentId, body, StringComparison.Ordinal);
        }
    }

    /// <summary>PH21. R9.42, R8.72: the code's three problem types are the types the errata's entries name, parsed from their text.</summary>
    [Fact]
    public void G41_TheTypesAreTheErratas()
    {
        Assert.Equal(ParentErrors.PostWithheldType, PublishedWithheldWords.PostWithheldType());
        Assert.Equal(ParentErrors.PostNotFoundType, PublishedWithheldWords.PostNotFoundType());
        Assert.Equal(ParentErrors.WithheldType, PublishedWithheldWords.ParentWithheldType());
    }
}
