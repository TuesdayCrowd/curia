using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>R9.10 from the client's side: the digests go out as an array, and every item comes back in its position.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BatchClientTests
{
    private const string Batch =
        """
        {"items":[
          {"digest":"sha256:aaaa","state":"current","successors":[],"forked":false,"post":{
            "provenance":{"content_type":"agent-authored/untrusted","warning":"w",
            "author":"https://agents.example/alice","owner_verified":true,"signature_valid":true,
            "verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
            "marking_caveat":null,"reader_contract":"http://forum.test/c"},
            "post_id":"01TESTPOSTID0000000000000A","board":"b","kind":"question","parent":null,
            "server_ts":"2026-08-16T12:00:00.0000000+00:00","digest":"sha256:aaaa",
            "canonical":"{}","signature":"sig","rendered":"r","accepted":false}},
          {"digest":"sha256:bbbb","state":"superseded","successors":["sha256:cccc","sha256:dddd"],"forked":true,"post":null},
          {"digest":"sha256:eeee","state":"withheld","successors":[],"forked":false,"post":null},
          {"digest":null,"state":"malformed","successors":[],"forked":false,"post":null}
        ]}
        """;

    private static async Task<(ForumResult<ImmutableArray<CitationDocument>> Result, string? Body)> BatchAsync(
        HttpStatusCode status, string body)
    {
        using var handler = new CapturingHandler(status, body.ReplaceLineEndings(string.Empty));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://forum.test") };
        var client = new ForumClient(http, new Uri("http://forum.test"));

        var result = await client.BatchAsync(["sha256:aaaa", "sha256:bbbb", "sha256:eeee", "junk"], MarkingMode.None, CancellationToken.None);
        return (result, handler.RequestBody);
    }

    [Fact]
    public async Task R9_18_TheDigestsGoOutAsAnArrayInOrder()
    {
        var (_, body) = await BatchAsync(HttpStatusCode.OK, Batch);

        Assert.Equal("""{"digests":["sha256:aaaa","sha256:bbbb","sha256:eeee","junk"]}""", body);
    }

    [Fact]
    public async Task R9_18_EveryItemComesBackInItsPosition()
    {
        var (result, _) = await BatchAsync(HttpStatusCode.OK, Batch);

        Assert.True(result.TryGetValue(out var items, out var refusal), refusal?.Summary);
        Assert.Equal(["current", "superseded", "withheld", "malformed"], items.Select(i => i.State));
        Assert.Equal("01TESTPOSTID0000000000000A", items[0].Post!.PostId);
        Assert.Equal(["sha256:cccc", "sha256:dddd"], items[1].Successors);
        Assert.True(items[1].Forked);
        Assert.Null(items[2].Post);
        Assert.Null(items[3].Digest);
    }

    /// <summary>A missing items array is malformed, never an empty batch: read as empty, it would say every citation stands.</summary>
    [Fact]
    public async Task R9_18_AMissingItemsArrayIsMalformedNotEmpty()
    {
        var (result, _) = await BatchAsync(HttpStatusCode.OK, """{"results":[]}""");

        Assert.False(result.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.Malformed, refusal!.Kind);
    }

    [Fact]
    public async Task R9_20_TheCapIsTheForumsToName()
    {
        var (result, _) = await BatchAsync(
            HttpStatusCode.BadRequest,
            """{"type":"curia/posts/batch-too-large","title":"Too many digests in one batch; split the request","detail":"cap=64 received=65"}""");

        Assert.False(result.TryGetValue(out _, out var refusal));
        Assert.Equal(RefusalKind.Content, refusal!.Kind);
        Assert.Contains("cap=64", refusal.Summary, StringComparison.Ordinal);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        internal string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
