using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// G41 decision 6, pinned: <c>GET /v1/threads/{root}</c> is unchanged by the withheld-post PR. A reply stands in the thread of a withheld root and the
/// root itself is not served; a root withheld with no reply to show is <c>curia/threads/not-found</c>. The oracle is the route's own document and the
/// ids the test received as receipts when it posted.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class WithheldThreadTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private async Task<(HttpStatusCode Status, string Body)> GetThreadAsync(string root, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/threads/{root}", UriKind.Relative), ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>PH12, row one. A root withheld with replies standing: <c>200</c>, the replies served, the root not.</summary>
    [Fact]
    public async Task G41_TheThreadRouteIsUnchangedForAWithheldRootWithReplies()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await AcceptedAnswerScene.BuildAsync(forum, ct);
        await forum.WithholdAsync(scene.Question.PostId, ct);

        var (status, body) = await GetThreadAsync(scene.Question.PostId, ct);

        Assert.True(status == HttpStatusCode.OK, $"the thread of a withheld root with replies answered {(int)status}: {body}");
        var served = JsonDocument.Parse(body).RootElement.EnumerateArray().Select(p => p.GetProperty("post_id").GetString()!).ToArray();
        Assert.Contains(scene.Answer1.PostId, served);
        Assert.Contains(scene.Answer2.PostId, served);
        Assert.DoesNotContain(scene.Question.PostId, served);
    }

    /// <summary>PH12, row two. A root withheld with no reply to show: <c>404</c> <c>curia/threads/not-found</c>; the control, the same question served, is <c>200</c> and holds itself.</summary>
    [Fact]
    public async Task G41_TheThreadRouteIsUnchangedForAWithheldRootAlone()
    {
        var ct = TestContext.Current.CancellationToken;
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "alone", ct);
        var word = "quillwort" + Guid.NewGuid().ToString("N")[..8];
        var question = await AcceptedAnswerScene.PostAsync(forum, asker, asker.Agent.SignQuestion("board-" + word, $"Is {word} a thread of one?", $"About {word}", forum.Now), ct);

        var (control, controlBody) = await GetThreadAsync(question.PostId, ct);
        Assert.True(control == HttpStatusCode.OK, controlBody);
        Assert.Contains(question.PostId, controlBody, StringComparison.Ordinal);

        await forum.WithholdAsync(question.PostId, ct);

        var (status, body) = await GetThreadAsync(question.PostId, ct);
        Assert.True(status == HttpStatusCode.NotFound, $"the thread of a withheld root alone answered {(int)status}: {body}");
        Assert.Equal("curia/threads/not-found", JsonDocument.Parse(body).RootElement.GetProperty("type").GetString());
    }
}
