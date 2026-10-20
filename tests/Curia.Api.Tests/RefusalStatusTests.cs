using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
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

    /// <summary>
    /// A submission built by hand around a signature that signs some other envelope, so VERIFY refuses it at the
    /// signature if it gets that far. Serialized with plain <c>Canonicalize</c> (no NFC), so two member names that
    /// differ only by normalization stay distinct on the wire and pass ADMIT.
    /// </summary>
    private byte[] HandBuilt(ForumAgent agent, string board, bool collide)
    {
        using var other = JsonDocument.Parse(agent.Sign(PostKind.Question, board, "other", "Other", parent: null, forum.Now));
        var signature = other.RootElement.GetProperty("signature").GetString()!;

        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(PostKind.Question))));
        members.Add(new("author", new JsonValue.String(agent.AgentId)));
        members.Add(new("board", new JsonValue.String(board)));
        members.Add(new("title", new JsonValue.String("A title")));
        members.Add(new("body", new JsonValue.String("a body")));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([new JsonValue.String("jcs")])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(forum.Now.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String(Guid.NewGuid().ToString("N"))));
        // U+00E9 against U+0065 U+0301: distinct strings, one NFC form.
        members.Add(new("caf\u00e9", new JsonValue.String("x")));
        if (collide) members.Add(new("cafe\u0301", new JsonValue.String("y")));

        var submission = new JsonValue.Object(
        [
            new("envelope", new JsonValue.Object(members.ToImmutable())),
            new("signature", new JsonValue.String(signature)),
        ]);
        Assert.True(CanonicalJson.Canonicalize(submission).TryGetValue(out var wire, out _));
        return wire.ToArray();
    }

    /// <summary>R6.58 (rev.): a document that cannot be canonicalized is a content refusal, 400, not a failed credential.</summary>
    [Fact]
    public async Task R6_58_MemberNamesThatCollideAfterNfcAreABadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agent, dpop, token) = await AuthenticatedAsync(forum.Client, "author", ct);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        using var response = await dpop.PostAsync(
            forum.Client, PostsUrl, token, HandBuilt(agent, board, collide: true), forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode}: {body}");
        Assert.Contains("curia/canon/duplicate-normalized-key", body, StringComparison.Ordinal);
    }

    /// <summary>The control: the same garbage signature without a collision reaches the signature check and answers 401.</summary>
    [Fact]
    public async Task R6_58_TheSameSubmissionWithoutACollisionFailsAtTheSignature()
    {
        var ct = TestContext.Current.CancellationToken;
        var (agent, dpop, token) = await AuthenticatedAsync(forum.Client, "author", ct);
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        using var response = await dpop.PostAsync(
            forum.Client, PostsUrl, token, HandBuilt(agent, board, collide: false), forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{(int)response.StatusCode}: {body}");
        Assert.Contains("curia/jws/signature-invalid", body, StringComparison.Ordinal);
    }
}
