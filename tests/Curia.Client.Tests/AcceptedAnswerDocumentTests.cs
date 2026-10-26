using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R9.36 and R10.81 from the client's side: <c>provenance.accepted_answer</c> is kept when the Forum serves it, an absent member is told apart from
/// <c>null</c> (the first is "not stated", the second is "none accepted"), a member that is neither <c>null</c> nor an object with a <c>sha256:</c> digest
/// is a malformed response, and so is the member on a post that is not a question. The document is scripted, so the oracle is the JSON this fact writes
/// and never the client's reading of it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptedAnswerDocumentTests
{
    private static readonly string Hex = new('a', 64);

    private static string Post(string provenanceTail, string kind = "question") => $$"""
        {"provenance":{"content_type":"application/vnd.curia.post+json;v=1","warning":"w","author":"https://agents.example/alice",
        "owner_verified":true,"signature_valid":true,"verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
        "marking_caveat":null,"reader_contract":"http://forum.test/c","owner":null,"reproductions":[],"contradictions":[],
        "key_status":"active","superseded_by":[]{{provenanceTail}} },
        "post_id":"01TESTPOSTID0000000000000A","board":"b","kind":"{{kind}}","parent":null,"server_ts":"2026-08-16T12:00:00.0000000+00:00",
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
    public async Task R9_36_AnAbsentMemberIsNotStatedAndNullIsNoneAccepted()
    {
        var absent = await ReadAsync(Post(""));
        Assert.True(absent.TryGetValue(out var notStated, out var refusal), refusal?.Summary);
        Assert.Null(notStated!.Provenance.AcceptedAnswer);

        var none = await ReadAsync(Post(""","accepted_answer":null"""));
        Assert.True(none.TryGetValue(out var accepted, out refusal), refusal?.Summary);
        Assert.True(accepted!.Provenance.AcceptedAnswer is not null, "a null member was read as not stated; expected None");
        Assert.True(accepted.Provenance.AcceptedAnswer!.IsNone, "a null member was not read as none");
    }

    [Fact]
    public async Task R9_36_AClientKeepsTheAnswerWithAndWithoutItsPostId()
    {
        var served = await ReadAsync(Post($$""","accepted_answer":{"digest":"sha256:{{Hex}}","post_id":"01ANSWERID000000000000001"}"""));
        Assert.True(served.TryGetValue(out var post, out var refusal), refusal?.Summary);
        Assert.Equal(new AcceptedAnswerMember("sha256:" + Hex, "01ANSWERID000000000000001", []), post!.Provenance.AcceptedAnswer);

        var unserved = await ReadAsync(Post($$""","accepted_answer":{"digest":"sha256:{{Hex}}"}"""));
        Assert.True(unserved.TryGetValue(out var other, out refusal), refusal?.Summary);
        Assert.Equal(new AcceptedAnswerMember("sha256:" + Hex, null, []), other!.Provenance.AcceptedAnswer);
    }

    [Theory]
    [InlineData(""","accepted_answer":"x" """)]
    [InlineData(""","accepted_answer":[]""")]
    [InlineData(""","accepted_answer":7""")]
    [InlineData(""","accepted_answer":{}""")]
    [InlineData(""","accepted_answer":{"digest":"sha256:abc"}""")]
    [InlineData(""","accepted_answer":{"digest":7}""")]
    [InlineData(""","accepted_answer":{"digest":"sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}""")]
    [InlineData(""","accepted_answer":{"digest":"sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","post_id":7}""")]
    public async Task R9_36_AMemberThatIsNotNullOrAnAnswerIsAMalformedResponse(string tail)
    {
        var read = await ReadAsync(Post(tail));

        Assert.False(read.TryGetValue(out _, out var refusal), $"{tail} was read as a post");
        Assert.Equal(RefusalKind.Malformed, refusal!.Kind);
        Assert.Contains("R9.36", refusal.Error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("answer")]
    [InlineData("comment")]
    [InlineData("revision")]
    public async Task R9_36_TheMemberOnAPostThatIsNotAQuestionIsAMalformedResponse(string kind)
    {
        var withNull = await ReadAsync(Post(""","accepted_answer":null""", kind));
        Assert.False(withNull.TryGetValue(out _, out var refusal), $"a '{kind}' carrying accepted_answer:null was read as a post");
        Assert.Equal(RefusalKind.Malformed, refusal!.Kind);

        // Control: the same kind without the member is a post, so the refusal is the member's doing.
        Assert.True((await ReadAsync(Post("", kind))).TryGetValue(out _, out refusal), refusal?.Summary);
    }

    /// <summary>Control for the rows above: the document without a tail is a post, so a refusal is the member's doing.</summary>
    [Fact]
    public async Task R9_36_TheScriptedDocumentIsAPostWithoutTheMember()
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
