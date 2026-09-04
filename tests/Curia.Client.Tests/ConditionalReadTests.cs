using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R9.11 from the client's side: the validator a previous read returned goes out as
/// <c>If-None-Match</c> exactly as it was received, a 304 comes back as "unchanged" rather than as
/// an unparseable refusal, and a 200 is the post as now served, carrying its new tag.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ConditionalReadTests
{
    private const string KnownTag = "\"representation:" + "abc" + "\"";
    private const string ServedTag = "\"representation:" + "def" + "\"";

    private const string PostBody =
        """
        {"provenance":{"content_type":"agent-authored/untrusted","warning":"w",
        "author":"https://agents.example/alice","owner_verified":true,"signature_valid":true,
        "verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
        "marking_caveat":null,"reader_contract":"http://forum.test/c"},
        "post_id":"01TESTPOSTID0000000000000A","board":"b","kind":"question","parent":null,
        "server_ts":"2026-08-16T12:00:00.0000000+00:00","digest":"sha-256:def",
        "canonical":"{}","signature":"sig","rendered":"r","accepted":false}
        """;

    private static async Task<(ForumResult<PostCheck> Result, string? IfNoneMatch)> CheckAsync(
        HttpStatusCode status, string body)
    {
        using var handler = new CapturingHandler(status, body.ReplaceLineEndings(string.Empty));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://forum.test") };
        var client = new ForumClient(http, new Uri("http://forum.test"));

        var result = await client.GetPostIfChangedAsync("01TESTPOSTID0000000000000A", KnownTag, MarkingMode.None, CancellationToken.None);
        return (result, handler.IfNoneMatch);
    }

    /// <summary>Sent verbatim: the tag is opaque, and a client that rebuilt it from the digest is the defect G6 records.</summary>
    [Fact]
    public async Task R9_11_TheKnownTagIsSentExactlyAsReceived()
    {
        var (_, sent) = await CheckAsync(HttpStatusCode.NotModified, string.Empty);

        Assert.Equal(KnownTag, sent);
    }

    [Fact]
    public async Task R9_11_A304IsUnchangedNotARefusal()
    {
        var (result, _) = await CheckAsync(HttpStatusCode.NotModified, string.Empty);

        Assert.True(result.TryGetValue(out var check, out var refusal), refusal?.Summary);
        Assert.True(check!.Unchanged);
        Assert.Equal(KnownTag, check.EntityTag);
        Assert.Null(check.Post);
    }

    [Fact]
    public async Task R9_11_A200IsThePostAsNowServedWithItsNewTag()
    {
        var (result, _) = await CheckAsync(HttpStatusCode.OK, PostBody);

        Assert.True(result.TryGetValue(out var check, out var refusal), refusal?.Summary);
        Assert.False(check!.Unchanged);
        Assert.Equal(ServedTag, check.EntityTag);
        Assert.Equal("01TESTPOSTID0000000000000A", check.Post!.PostId);
        Assert.Equal(ServedTag, check.Post.EntityTag);
    }

    /// <summary>An unconditional read remembers the tag it was given, so a later re-check has something to send.</summary>
    [Fact]
    public async Task R9_11_AReadCarriesTheTagItWasServed()
    {
        using var handler = new CapturingHandler(HttpStatusCode.OK, PostBody.ReplaceLineEndings(string.Empty));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://forum.test") };
        var client = new ForumClient(http, new Uri("http://forum.test"));

        var result = await client.GetPostAsync("01TESTPOSTID0000000000000A", MarkingMode.None, CancellationToken.None);

        Assert.True(result.TryGetValue(out var post, out var refusal), refusal?.Summary);
        Assert.Equal(ServedTag, post!.EntityTag);
        Assert.Null(handler.IfNoneMatch);
    }

    /// <summary>A withheld post is a not-found, never a 304: gone is the one way a post changes.</summary>
    [Fact]
    public async Task R9_11_AWithheldPostIsNotFound()
    {
        var (result, _) = await CheckAsync(
            HttpStatusCode.NotFound, """{"type":"curia/posts/not-found","title":"No such post","detail":"01TESTPOSTID0000000000000A"}""");

        Assert.False(result.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.NotFound, refusal!.Kind);
    }

    /// <summary>A 304 to a request that did not ask for one is still what it always was: a response this client cannot read.</summary>
    [Fact]
    public async Task AnUnsolicited304IsStillMalformed()
    {
        using var handler = new CapturingHandler(HttpStatusCode.NotModified, string.Empty);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://forum.test") };
        var client = new ForumClient(http, new Uri("http://forum.test"));

        var result = await client.GetPostAsync("01TESTPOSTID0000000000000A", MarkingMode.None, CancellationToken.None);

        Assert.False(result.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.Malformed, refusal!.Kind);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        internal string? IfNoneMatch { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            IfNoneMatch = request.Headers.TryGetValues("If-None-Match", out var values) ? string.Join(", ", values) : null;

            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(ServedTag);
            if (status != HttpStatusCode.NotModified)
                response.Content = new StringContent(body, Encoding.UTF8, "application/json");
            return Task.FromResult(response);
        }
    }
}
