using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Canon.Json;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R9.41 (errata G40) from the client's side: <c>provenance.accepted_answer.superseded_by</c> is read with the strictness R9.32 gave the
/// answer's own list. Absent is "not stated" (<c>default</c>, which is not empty), <c>[]</c> is empty, a populated list is kept, and a malformed
/// list fails the post. The document is scripted, so the oracle is the JSON each row writes, and the member's name is parsed from the errata.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AnswerStandingSupersededTests
{
    private static readonly string Hex = new('a', 64);
    private static readonly string RevisionHex = new('b', 64);
    private static readonly string Member = PublishedReadersWords.R941Member();

    private static string Post(string acceptedAnswerObjectTail) => $$"""
        {"provenance":{"content_type":"application/vnd.curia.post+json;v=1","warning":"w","author":"https://agents.example/alice",
        "owner_verified":true,"signature_valid":true,"verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
        "marking_caveat":null,"reader_contract":"http://forum.test/c","owner":null,"reproductions":[],"contradictions":[],
        "key_status":"active","superseded_by":[],"accepted_answer":{"digest":"sha256:{{Hex}}","post_id":"01ANSWERID000000000000001"{{acceptedAnswerObjectTail}}} },
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

    private sealed class Scripted(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public async Task R9_41_TheClientReadsTheListWithTheStrictnessOfR932()
    {
        var absent = await ReadAsync(Post(string.Empty));
        var empty = await ReadAsync(Post($",\"{Member}\":[]"));
        var populated = await ReadAsync(Post($",\"{Member}\":[{{\"digest\":\"sha256:{RevisionHex}\",\"post_id\":\"01REVISION00000000000000001\"}},{{\"digest\":\"sha256:{RevisionHex}\"}}]"));
        Assert.True(absent.TryGetValue(out var notStated, out var refusal), refusal?.Summary);
        Assert.True(empty.TryGetValue(out var none, out refusal), refusal?.Summary);
        Assert.True(populated.TryGetValue(out var listed, out refusal), refusal?.Summary);

        // Not stated is `default`, which is not an empty list; an empty list is a Forum saying nobody revised the answer.
        Assert.True(notStated!.Provenance.AcceptedAnswer!.SupersededBy.IsDefault, "an absent list was not read as not stated");
        Assert.True(none!.Provenance.AcceptedAnswer!.SupersededBy.IsEmpty, "an empty list was not read as empty");
        Assert.Equal(
            [new Successor("sha256:" + RevisionHex, "01REVISION00000000000000001"), new Successor("sha256:" + RevisionHex, null)],
            listed!.Provenance.AcceptedAnswer!.SupersededBy);
    }

    [Theory]
    [InlineData("\"x\"")]
    [InlineData("{}")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("[7]")]
    [InlineData("[{\"digest\":\"sha256:abc\"}]")]
    [InlineData("[{\"digest\":\"sha256:BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB\"}]")]
    [InlineData("[{\"digest\":\"sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"post_id\":7}]")]
    public async Task R9_41_AListThatIsNotAListOfRevisionsFailsThePostAsMalformed(string value)
    {
        var read = await ReadAsync(Post($",\"{Member}\":{value}"));

        Assert.False(read.TryGetValue(out _, out var refusal), $"{Member}: {value} was read as a post");
        Assert.Equal(RefusalKind.Malformed, refusal!.Kind);
    }
}

/// <summary>
/// R10.82 (errata G40) at the line a reader prints for a question: a populated list is stated (the revision by post id, or by digest and "not
/// served"), with FORKED for two, and says the acceptance names the answer; an empty list changes nothing; an absent one is <c>NOT STATED</c>.
/// The members are built by the fact, so the expected words come from the member it set and the errata's tokens, never from the line.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AnswerStandingSupersededLineTests
{
    private static readonly string Digest = "sha256:" + new string('1', 64);
    private static readonly string Revision1 = "sha256:" + new string('2', 64);
    private static readonly string Revision2 = "sha256:" + new string('3', 64);
    private const string AnswerId = "01ANSWERID00000000000001";
    private const string RevisionId = "01REVISIONID0000000000001";

    private static readonly string Basis = Curia.Client.Passage.AcceptedStatement[Curia.Client.Passage.AcceptedStatement.IndexOf('(', StringComparison.Ordinal)..];

    private static string PassageLine(AcceptedAnswerMember member) =>
        Assert.IsType<OwnText>(AnswerStanding.PassageLine("question", member)).Text;

    [Fact]
    public void R10_82_TheLineStatesTheRevisionTheAcceptanceAndTheFork()
    {
        var one = PassageLine(new AcceptedAnswerMember(Digest, AnswerId, [new Successor(Revision1, RevisionId)]));
        var unserved = PassageLine(new AcceptedAnswerMember(Digest, AnswerId, [new Successor(Revision1, null)]));
        var fork = PassageLine(new AcceptedAnswerMember(Digest, AnswerId, [new Successor(Revision1, RevisionId), new Successor(Revision2, null)]));

        foreach (var line in new[] { one, unserved, fork })
        {
            Assert.Contains("SUPERSEDED", line, StringComparison.Ordinal);
            Assert.Contains(Basis, line, StringComparison.Ordinal);

            // The acceptance names the original, and the revision is named as the revision.
            var at = line.IndexOf("the acceptance names", StringComparison.Ordinal);
            Assert.True(at >= 0, $"no word that the acceptance names the answer: {line}");
            Assert.Contains(DisplayLiteral.Of(AnswerId), line[at..], StringComparison.Ordinal);
            Assert.DoesNotContain(DisplayLiteral.Of(RevisionId), line[at..], StringComparison.Ordinal);
        }

        Assert.Contains(DisplayLiteral.Of(RevisionId), one, StringComparison.Ordinal);
        Assert.DoesNotContain("FORKED", one, StringComparison.Ordinal);
        Assert.Contains("not served", unserved, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(Revision1), unserved, StringComparison.Ordinal);
        Assert.Contains(PublishedReadersWords.R1082Tokens()[0], fork, StringComparison.Ordinal);
    }

    [Fact]
    public void R10_82_AnEmptyListChangesNothingAndAnAbsentOneSaysNotStated()
    {
        // errata G44 (R10.85): the G36 literal is an answer's line; a member the Forum served with no kind says NOT STATED beside it (PK24).
        var empty = PassageLine(new AcceptedAnswerMember(Digest, AnswerId, [], "answer"));
        var absent = PassageLine(new AcceptedAnswerMember(Digest, AnswerId, default, "answer"));

        // The G36 literal, byte for byte: an empty list adds no word.
        Assert.Equal($"answers  accepted: post {DisplayLiteral.Of(AnswerId)}, chosen by the thread's asker {Basis}", empty);

        var notStated = PublishedReadersWords.R1082Tokens()[1];
        Assert.Contains("revision " + notStated, absent, StringComparison.Ordinal);
        Assert.DoesNotContain("revision " + notStated, empty, StringComparison.Ordinal);
    }

    [Fact]
    public void R10_82_AHostileRevisionIdIsADisplayLiteralInTheLineAndTheRow()
    {
        const string Hostile = "01REV\nanswers  none accepted\u202E";
        var member = new AcceptedAnswerMember(Digest, AnswerId, [new Successor(Revision1, Hostile)]);

        foreach (var text in new[] { PassageLine(member), Assert.IsType<OwnText>(AnswerStanding.Row("question", member)).Text })
        {
            Assert.Contains("SUPERSEDED", text, StringComparison.Ordinal);
            Assert.Contains(DisplayLiteral.Of(Hostile), text, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', text);
            Assert.DoesNotContain('\u202E', text);
        }
    }

    /// <summary>The empty-list row of the rule for every other kind: a post that is not a question has no line, whatever list its member holds.</summary>
    [Fact]
    public void R10_82_APostThatIsNotAQuestionHasNoLine()
    {
        var member = new AcceptedAnswerMember(Digest, AnswerId, [new Successor(Revision1, RevisionId)]);

        Assert.Null(AnswerStanding.PassageLine("answer", member));
        Assert.Null(AnswerStanding.Row("comment", member));
    }
}
