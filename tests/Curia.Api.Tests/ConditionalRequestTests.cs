using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.11: conditional requests keyed to the content digest, so "has this changed?" costs a
/// round trip and no body.
///
/// <para>A post's bytes never change -- a revision is a new post with a new digest -- so the one
/// thing a conditional read on a post id can learn is whether the post is still served at all. That
/// is exactly what a citing agent needs to know cheaply, and it is why the withheld case is here
/// beside the happy path rather than in the moderation suite.</para>
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

    /// <summary>The ETag is the digest, quoted as a strong validator, and it is the digest the body carries.</summary>
    [Fact]
    public async Task R9_11_TheETagIsTheContentDigest()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, digest) = await AskAsync(http, ct);

        using var request = Get(postId);
        using var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{digest}\"", response.Headers.ETag?.ToString());

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(digest, body.RootElement.GetProperty("digest").GetString());
    }

    /// <summary>
    /// The current digest earns a 304 with no body, whether presented strong, weak, or as
    /// <c>*</c> -- RFC 9110 §13.1.2 compares If-None-Match weakly, and a client library that sends
    /// <c>W/</c> is not wrong.
    /// </summary>
    [Theory]
    [InlineData("\"{digest}\"")]
    [InlineData("W/\"{digest}\"")]
    [InlineData("\"sha-256:stale\", \"{digest}\"")]
    [InlineData("*")]
    public async Task R9_11_TheCurrentDigestIsNotModified(string ifNoneMatch)
    {
        ArgumentNullException.ThrowIfNull(ifNoneMatch);
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, digest) = await AskAsync(http, ct);

        using var request = Get(postId, ifNoneMatch.Replace("{digest}", digest, StringComparison.Ordinal));
        using var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Equal($"\"{digest}\"", response.Headers.ETag?.ToString());
        Assert.Equal(string.Empty, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>A digest that is not the post's earns the post, in full, with its envelope.</summary>
    [Fact]
    public async Task R9_11_AStaleDigestEarnsTheContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (postId, digest) = await AskAsync(http, ct);

        using var request = Get(postId, "\"sha-256:" + new string('0', 43) + "\"");
        using var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.Equal(digest, body.RootElement.GetProperty("digest").GetString());
        Assert.True(body.RootElement.TryGetProperty("provenance", out _));
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
        var (postId, digest) = await AskAsync(http, ct);

        await forum.WithholdAsync(postId, ct);

        using var request = Get(postId, $"\"{digest}\"");
        using var response = await http.SendAsync(request, ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(response.Headers.ETag);
    }
}
