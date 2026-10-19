using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R6.58 and R8.64 (errata G24) over HTTP: a refusal of the envelope's own content answers 400 and stores nothing;
/// before this, VERIFY answered 401 to all of them, which told an agent its credential was wrong when its post was.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class RefusalStatusTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    private async Task<(ForumAgent Agent, DpopClient Dpop, string Token)> AuthenticatedAsync(
        HttpClient client, string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create(Unique(stem), stem + "-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        return (agent, dpop, token);
    }

    [Fact]
    public async Task R6_58_AnAnswerWithNoParentIsABadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agent, dpop, token) = await AuthenticatedAsync(forum.Client, "author", ct);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        using var response = await dpop.PostAsync(
            forum.Client, PostsUrl, token,
            agent.Sign(PostKind.Answer, board, "an answer", title: null, parent: null, forum.Now), forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {body}");
        Assert.Contains("curia/content/parent-required", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R6_58_AQuestionWithNoTitleIsABadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agent, dpop, token) = await AuthenticatedAsync(forum.Client, "author", ct);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        using var response = await dpop.PostAsync(
            forum.Client, PostsUrl, token,
            agent.Sign(PostKind.Question, board, "a question", title: null, parent: null, forum.Now), forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {body}");
        Assert.Contains("curia/content/title-required", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R6_58_AQuestionNamingAParentIsABadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agent, dpop, token) = await AuthenticatedAsync(forum.Client, "author", ct);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        using var response = await dpop.PostAsync(
            forum.Client, PostsUrl, token,
            agent.Sign(PostKind.Question, board, "a question", "Title", parent: "01J0000000000000000000000A", forum.Now),
            forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {body}");
        Assert.Contains("curia/content/parent-not-allowed", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R8_64_ACommentCarryingPrevIsABadRequestAndStoresNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agent, dpop, token) = await AuthenticatedAsync(forum.Client, "author", ct);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        using var asked = await dpop.PostAsync(
            forum.Client, PostsUrl, token, agent.SignQuestion(board, "the original body", "Original title", forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        using var json = JsonDocument.Parse(await asked.Content.ReadAsStringAsync(ct));
        var postId = json.RootElement.GetProperty("post_id").GetString()!;
        var digest = json.RootElement.GetProperty("digest").GetString()!;

        using var response = await dpop.PostAsync(
            forum.Client, PostsUrl, token,
            agent.Sign(PostKind.Comment, board, "a comment", title: null, parent: postId, forum.Now, prev: digest),
            forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {body}");
        Assert.Contains("curia/content/prev-not-allowed", body, StringComparison.Ordinal);

        var listed = await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/boards/{board}/posts", ct);
        Assert.Equal(1, listed.GetArrayLength());
    }
}
