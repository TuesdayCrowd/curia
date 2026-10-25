using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R9.32 and R10.80 from the client's side: <c>provenance.superseded_by</c> is kept when the Forum serves it, an absent member is told apart
/// from an empty list (the first is "not stated", the second is "none"), and a member that is not an array of objects with a digest is a
/// malformed response, as <c>excluded_by_floor</c> is when it is no count (R9.30). The document is scripted, so the oracle is the JSON this
/// fact writes and never the client's reading of it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SupersededDocumentTests
{
    private static readonly string Hex = new('a', 64);

    private static string Post(string provenanceTail) => $$"""
        {"provenance":{"content_type":"application/vnd.curia.post+json;v=1","warning":"w","author":"https://agents.example/alice",
        "owner_verified":true,"signature_valid":true,"verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
        "marking_caveat":null,"reader_contract":"http://forum.test/c","owner":null,"reproductions":[],"contradictions":[],
        "key_status":"active"{{provenanceTail}} },
        "post_id":"01TESTPOSTID0000000000000A","board":"b","kind":"question","parent":null,"server_ts":"2026-08-16T12:00:00.0000000+00:00",
        "digest":"sha256:{{Hex}}","canonical":"{}","signature":"sig","rendered":"{}","accepted":false,"log_index":0}
        """;

    private static async Task<ForumResult<ProvenancePost>> ReadAsync(string body)
    {
        using var handler = new Scripted(body);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://forum.test") };
        var client = new ForumClient(http, new Uri("http://forum.test"));
        return await client.GetPostAsync("01TESTPOSTID0000000000000A", MarkingMode.None, CancellationToken.None);
    }

    [Fact]
    public async Task R9_32_AClientDistinguishesAnAbsentListFromAnEmptyOne()
    {
        var absent = await ReadAsync(Post(""));
        Assert.True(absent.TryGetValue(out var notStated, out var refusal), refusal?.Summary);
        Assert.True(notStated!.Provenance.SupersededBy.IsDefault, "an absent member was read as a list; it must read as not stated");

        var empty = await ReadAsync(Post(""","superseded_by":[]"""));
        Assert.True(empty.TryGetValue(out var none, out refusal), refusal?.Summary);
        Assert.False(none!.Provenance.SupersededBy.IsDefault, "an empty list was read as not stated");
        Assert.Empty(none.Provenance.SupersededBy);
    }

    [Fact]
    public async Task R9_32_AClientKeepsEachRevisionWithAndWithoutItsPostId()
    {
        var other = "sha256:" + new string('b', 64);
        var read = await ReadAsync(Post($$""","superseded_by":[{"digest":"sha256:{{Hex}}","post_id":"01REVISIONID00000000000001"},{"digest":"{{other}}"}]"""));

        Assert.True(read.TryGetValue(out var post, out var refusal), refusal?.Summary);
        Assert.Equal(
            [new Successor("sha256:" + Hex, "01REVISIONID00000000000001"), new Successor(other, null)],
            post!.Provenance.SupersededBy);
    }

    [Theory]
    [InlineData(""","superseded_by":"x" """)]
    [InlineData(""","superseded_by":{}""")]
    [InlineData(""","superseded_by":null""")]
    [InlineData(""","superseded_by":[1]""")]
    [InlineData(""","superseded_by":[{}]""")]
    [InlineData(""","superseded_by":[{"digest":"sha256:abc"}]""")]
    [InlineData(""","superseded_by":[{"digest":7}]""")]
    [InlineData(""","superseded_by":[{"digest":"sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]""")]
    [InlineData(""","superseded_by":[{"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","post_id":7}]""")]
    public async Task R9_32_AMemberThatIsNotAListOfRevisionsIsAMalformedResponse(string tail)
    {
        var read = await ReadAsync(Post(tail));

        Assert.False(read.TryGetValue(out _, out var refusal), $"{tail} was read as a post");
        Assert.Equal(RefusalKind.Malformed, refusal!.Kind);
        Assert.Contains("R9.32", refusal.Error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Control for the rows above: the document without a tail is a post, so a refusal is the member's doing.</summary>
    [Fact]
    public async Task R9_32_TheScriptedDocumentIsAPostWithoutTheMember()
    {
        Assert.True((await ReadAsync(Post(""))).TryGetValue(out var post, out var refusal), refusal?.Summary);
        Assert.Equal("01TESTPOSTID0000000000000A", post!.PostId);
    }

    private sealed class Scripted(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
