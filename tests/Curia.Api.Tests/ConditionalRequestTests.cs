using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.11 (rev., errata G6): conditional requests on the single-post read, so "has this changed?"
/// costs a round trip and no body.
///
/// <para>The validator is a strong tag over the served <i>representation</i>, not the content
/// digest. The signed bytes never change -- a revision is a new post -- but the envelope around them
/// does: owner verification, verification level and acceptance are projections of the log. The two
/// <c>G6_</c> tests below are the probes that caught a digest-keyed tag answering "unchanged" to
/// both, on the day it shipped; the withheld case sits beside them because gone is the other way a
/// served post changes.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ConditionalRequestTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private async Task<(string PostId, string Digest)> AskAsync(HttpClient http, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/cond-" + suffix, "ck-" + suffix);
        var (dpop, token) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);

        using var response = await dpop.PostAsync(
            http, PostsUrl, token, agent.SignQuestion("board-" + suffix, "Does this change?", "Conditional", forum.Now), forum.Now, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return (json.RootElement.GetProperty("post_id").GetString()!, json.RootElement.GetProperty("digest").GetString()!);
    }

    private static HttpRequestMessage Get(string postId, string? ifNoneMatch = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/posts/{postId}");
        if (ifNoneMatch is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return request;
    }

    private static async Task<HttpResponseMessage> SendGetAsync(
        HttpClient http, string postId, CancellationToken ct, string? ifNoneMatch = null)
    {
        using var request = Get(postId, ifNoneMatch);
        return await http.SendAsync(request, ct);
    }

    /// <summary>
    /// The ETag is a strong validator over the representation: prefixed so it cannot be mistaken
    /// for a citation digest, stable across two reads of an unchanged post, and served with
    /// <c>Cache-Control: no-cache</c> so no intermediary holds a body past R7.14's bound.
    /// </summary>
    [Fact]
    public async Task R9_11_TheETagIsAStrongValidatorOverTheRepresentation()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, digest) = await AskAsync(http, ct);

        using var first = await SendGetAsync(http, postId, ct);
        using var second = await SendGetAsync(http, postId, ct);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var tag = first.Headers.ETag!.ToString();
        Assert.StartsWith("\"representation:", tag, StringComparison.Ordinal);
        Assert.DoesNotContain(digest, tag, StringComparison.Ordinal);
        Assert.Equal(tag, second.Headers.ETag!.ToString());
        Assert.True(first.Headers.CacheControl?.NoCache, "Cache-Control: no-cache");

        using var body = JsonDocument.Parse(await first.Content.ReadAsStringAsync(ct));
        Assert.Equal(digest, body.RootElement.GetProperty("digest").GetString());
    }

    /// <summary>
    /// The current validator earns a 304 with no body, whether presented strong, weak, in a list,
    /// or as <c>*</c> -- RFC 9110 §13.1.2 compares If-None-Match weakly, and a client library that
    /// sends <c>W/</c> is not wrong.
    /// </summary>
    [Theory]
    [InlineData("{etag}")]
    [InlineData("W/{etag}")]
    [InlineData("\"representation:stale\", {etag}")]
    [InlineData("*")]
    public async Task R9_11_TheCurrentValidatorIsNotModified(string ifNoneMatch)
    {
        ArgumentNullException.ThrowIfNull(ifNoneMatch);
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, _) = await AskAsync(http, ct);

        string tag;
        using (var first = await SendGetAsync(http, postId, ct))
            tag = first.Headers.ETag!.ToString();

        using var response = await SendGetAsync(http, postId, ct, ifNoneMatch.Replace("{etag}", tag, StringComparison.Ordinal));

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal(tag, response.Headers.ETag?.ToString());
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>A validator that is not the post's earns the post, in full, with its envelope.</summary>
    [Fact]
    public async Task R9_11_AStaleValidatorEarnsTheContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, digest) = await AskAsync(http, ct);

        using var response = await SendGetAsync(http, postId, ct, "\"representation:" + new string('0', 64) + "\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(digest, body.RootElement.GetProperty("digest").GetString());
        Assert.True(body.RootElement.TryGetProperty("provenance", out _));
    }

    /// <summary>
    /// The representation is more than the content. R10.17's envelope carries <c>owner_verified</c>,
    /// a projection of the log that changes while the signed bytes do not -- so a validator that
    /// tracked only the digest would answer "unchanged" after an attestation, and RFC 9110 §8.8.1
    /// would then license every shared cache to serve the stale envelope to everyone. Found by the
    /// architect's probe against a live Forum on the day the digest-keyed tag shipped (errata G6).
    /// </summary>
    [Fact]
    public async Task G6_AnOwnerAttestationChangesTheValidator()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/cond-" + suffix, "ck-" + suffix);
        var (dpop, token) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);

        string postId;
        using (var posted = await dpop.PostAsync(
            http, PostsUrl, token, agent.SignQuestion("board-" + suffix, "Who vouches?", "Attested", forum.Now), forum.Now, ct))
        {
            using var json = JsonDocument.Parse(await posted.Content.ReadAsStringAsync(ct));
            postId = json.RootElement.GetProperty("post_id").GetString()!;
        }

        string before;
        using (var first = await SendGetAsync(http, postId, ct))
        {
            before = first.Headers.ETag!.ToString();
            using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync(ct));
            Assert.False(json.RootElement.GetProperty("provenance").GetProperty("owner_verified").GetBoolean());
        }

        await forum.AttestOwnerAsync(agent.AgentId, ct);

        using (var after = await SendGetAsync(http, postId, ct))
        {
            using var json = JsonDocument.Parse(await after.Content.ReadAsStringAsync(ct));
            Assert.True(json.RootElement.GetProperty("provenance").GetProperty("owner_verified").GetBoolean());
            Assert.NotEqual(before, after.Headers.ETag!.ToString());
        }

        using var conditional = await SendGetAsync(http, postId, ct, before);
        Assert.Equal(HttpStatusCode.OK, conditional.StatusCode);
    }

    /// <summary>
    /// The case Stage 2 exists for -- "after Stage 10 a thread it belongs to can be resolved. An
    /// agent holding citations has no way to learn either." Accepting an answer changes
    /// <c>accepted</c> in the served representation and nothing in the signed bytes.
    /// </summary>
    [Fact]
    public async Task G6_AnAcceptedAnswerChangesTheValidator()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var board = "board-" + suffix;

        var asker = ForumAgent.Create("https://agents.example/asker-" + suffix, "ak-" + suffix);
        var (askerDpop, askerToken) = await asker.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        string question;
        using (var asked = await askerDpop.PostAsync(
            http, PostsUrl, askerToken, asker.SignQuestion(board, "How are members ordered?", "Ordering", forum.Now), forum.Now, ct))
        {
            using var json = JsonDocument.Parse(await asked.Content.ReadAsStringAsync(ct));
            question = json.RootElement.GetProperty("post_id").GetString()!;
        }

        var answerer = ForumAgent.Create("https://agents.example/answerer-" + suffix, "an-" + suffix);
        var (answererDpop, _) = await answerer.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        await forum.AttestOwnerAsync(answerer.AgentId, ct);
        for (var i = 0; i < 3; i++)
        {
            using var warmup = await answererDpop.PostAsync(
                http, PostsUrl, await answererDpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct),
                answerer.SignQuestion(board, $"warmup {i}", $"Warmup {i}", forum.Now), forum.Now, ct);
            Assert.Equal(HttpStatusCode.Created, warmup.StatusCode);
        }

        forum.Clock.Advance(TimeSpan.FromHours(49));

        string answer;
        using (var answered = await answererDpop.PostAsync(
            http, PostsUrl, await answererDpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct),
            answerer.SignAnswer(board, "By UTF-16 code unit.", question, forum.Now), forum.Now, ct))
        {
            Assert.Equal(HttpStatusCode.Created, answered.StatusCode);
            using var json = JsonDocument.Parse(await answered.Content.ReadAsStringAsync(ct));
            answer = json.RootElement.GetProperty("post_id").GetString()!;
        }

        string before;
        using (var first = await SendGetAsync(http, answer, ct))
        {
            before = first.Headers.ETag!.ToString();
            using var json = JsonDocument.Parse(await first.Content.ReadAsStringAsync(ct));
            Assert.False(json.RootElement.GetProperty("accepted").GetBoolean());
        }

        using (var accepted = await askerDpop.PostAsync(
            http, $"http://localhost/v1/posts/{answer}/accept",
            await askerDpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct),
            System.Text.Encoding.UTF8.GetBytes("{}"), forum.Now, ct, contentType: "application/json"))
        {
            Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        }

        using (var after = await SendGetAsync(http, answer, ct))
        {
            using var json = JsonDocument.Parse(await after.Content.ReadAsStringAsync(ct));
            Assert.True(json.RootElement.GetProperty("accepted").GetBoolean());
            Assert.NotEqual(before, after.Headers.ETag!.ToString());
        }

        using var conditional = await SendGetAsync(http, answer, ct, before);
        Assert.Equal(HttpStatusCode.OK, conditional.StatusCode);
    }

    /// <summary>
    /// A withheld post has changed in the only way a post can: it is gone. Presenting its digest
    /// must never earn a 304, which would tell the citing agent its citation still stands.
    /// </summary>
    [Fact]
    public async Task R9_11_AWithheldPostIsNeverNotModified()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, _) = await AskAsync(http, ct);

        string tag;
        using (var first = await SendGetAsync(http, postId, ct))
            tag = first.Headers.ETag!.ToString();

        await forum.WithholdAsync(postId, ct);

        using var response = await SendGetAsync(http, postId, ct, tag);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.ETag);
    }
}
