using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Canon.Json;
using Curia.Domain.Primitives;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 4 (errata G44), Task 0's probes at the reference client: the document it reads (<c>accepted_answer.kind</c>, R9.46; the receipt's
/// <c>replaced_relation</c>, R8.76), the lines it prints (R10.85) and the help it publishes (R11.60).
///
/// <para><b>Oracles.</b> The words are the errata's, parsed at test time (<see cref="PublishedReadersWords"/>: the kind words, the member name and its
/// two values, the reader phrases), and the documents are scripted, so the expectation is the JSON each row writes. The golden lines (an
/// <c>answer</c> member, a replay) are built here from the G36 and G40 sentences and not read from the code under test.</para>
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptTheTextClientTests
{
    private static readonly string Hex = new('a', 64);
    private static readonly string Digest = "sha256:" + new string('1', 64);
    private static readonly string RevisionDigest = "sha256:" + new string('2', 64);
    private const string AnswerId = "01ANSWERID00000000000001";
    private const string RevisionId = "01REVISIONID0000000000001";
    private const string Thread = "01JTHREAD";
    private const string Old = "01JOLD";
    private const string At = "2026-10-27T12:00:00.0000000+00:00";

    private static readonly string Basis = Curia.Client.Passage.AcceptedStatement[Curia.Client.Passage.AcceptedStatement.IndexOf('(', StringComparison.Ordinal)..];

    // ----- PK23: the kind the client reads -----

    private static string Post(string kindTail) => $$"""
        {"provenance":{"content_type":"application/vnd.curia.post+json;v=1","warning":"w","author":"https://agents.example/alice",
        "owner_verified":true,"signature_valid":true,"verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
        "marking_caveat":null,"reader_contract":"http://forum.test/c","owner":null,"reproductions":[],"contradictions":[],
        "key_status":"active","superseded_by":[],"accepted_answer":{"digest":"sha256:{{Hex}}","post_id":"{{RevisionId}}"{{kindTail}},"superseded_by":[]} },
        "post_id":"01TESTPOSTID0000000000000A","board":"b","kind":"question","parent":null,"server_ts":"2026-10-27T12:00:00.0000000+00:00",
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

    /// <summary>R9.46: the client keeps the kind the Forum named, when it is one of the two published words.</summary>
    [Fact]
    public async Task R9_46_PK23_TheClientReadsTheKindOfThePostTheAcceptanceNames()
    {
        foreach (var word in PublishedReadersWords.R946KindWords())
        {
            var read = await ReadAsync(Post($",\"kind\":\"{word}\""));
            Assert.True(read.TryGetValue(out var post, out var refusal), refusal?.Summary);
            Assert.Equal(word, post!.Provenance.AcceptedAnswer!.Kind);
        }

        // An absent kind is a Forum that did not say (an older one): read, and not coerced to either word.
        var absent = await ReadAsync(Post(string.Empty));
        Assert.True(absent.TryGetValue(out var notStated, out var error), error?.Summary);
        Assert.Null(notStated!.Provenance.AcceptedAnswer!.Kind);
    }

    /// <summary>R9.46: any other word, a non-string and a null are a malformed response, and are not coerced to one of the two.</summary>
    [Theory]
    [InlineData("\"comment\"")]
    [InlineData("\"question\"")]
    [InlineData("\"Answer\"")]
    [InlineData("\"\"")]
    [InlineData("7")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task R9_46_PK23_AKindThatIsNeitherPublishedWordFailsThePostAsMalformed(string value)
    {
        var read = await ReadAsync(Post($",\"kind\":{value}"));

        Assert.False(read.TryGetValue(out _, out var refusal), $"kind: {value} was read as a post");
        Assert.Equal(RefusalKind.Malformed, refusal!.Kind);
    }

    // ----- PK24: the question's line -----

    private static string PassageLine(AcceptedAnswerMember member) => Assert.IsType<OwnText>(AnswerStanding.PassageLine("question", member)).Text;

    private static string RowLine(AcceptedAnswerMember member) => Assert.IsType<OwnText>(AnswerStanding.Row("question", member)).Text;

    /// <summary>
    /// R10.85: a question whose accepted post is a revision says so, in its own words outside the span; a question whose accepted post is an answer
    /// has the G36 line byte for byte (nothing extra); a member that names no kind says <c>NOT STATED</c>. The golden line is written here from G36.
    /// </summary>
    [Fact]
    public void R10_85_PK24_TheQuestionsLineStatesARevisionAndKeepsTheAnswerLineByteForByte()
    {
        var golden = $"accepted: post {DisplayLiteral.Of(RevisionId)}, chosen by the thread's asker {Basis}";

        var revision = new AcceptedAnswerMember(Digest, RevisionId, [], "revision");
        var answer = new AcceptedAnswerMember(Digest, RevisionId, [], "answer");
        var unstated = new AcceptedAnswerMember(Digest, RevisionId, []);

        // The control first: an answer's line is the G36 line, in both forms (green now).
        Assert.Equal("answers  " + golden, PassageLine(answer));
        Assert.Equal("answers " + golden, RowLine(answer));

        // A revision says so in both forms.
        Assert.Contains("a revision", PassageLine(revision), StringComparison.Ordinal);
        Assert.Contains("a revision", RowLine(revision), StringComparison.Ordinal);

        // A member the Forum served without a kind is told apart from both words.
        Assert.Contains("NOT STATED", PassageLine(unstated), StringComparison.Ordinal);
        Assert.Contains("NOT STATED", RowLine(unstated), StringComparison.Ordinal);
    }

    /// <summary>R9b of the plan: a revision the Forum does not serve is told by its kind and its digest (the post id is left out, the kind is not).</summary>
    [Fact]
    public void R10_85_PK24_ANotServedRevisionIsToldByItsKindAndItsDigest()
    {
        var line = PassageLine(new AcceptedAnswerMember(Digest, null, [], "revision"));

        Assert.Contains($"the revision with digest {DisplayLiteral.Of(Digest)} ({PublishedReadersLastWords.DefinitionSentence()})", line, StringComparison.Ordinal);
        Assert.Equal(
            $"answers  accepted: the answer with digest {DisplayLiteral.Of(Digest)} ({PublishedReadersLastWords.DefinitionSentence()}), chosen by the thread's asker {Basis}",
            PassageLine(new AcceptedAnswerMember(Digest, null, [], "answer")));
    }

    // ----- PK25: the way to accept the revision -----

    /// <summary>R10.85: the statement for a revised accepted post says how the thread's asker accepts the revision, in <c>curia</c>'s words.</summary>
    [Fact]
    public void R10_85_PK25_TheRevisedStatementSaysHowTheAskerAcceptsTheRevision()
    {
        var command = PublishedReadersWords.R1085Phrases()[0];
        Assert.Equal("curia resolve", command);

        var statement = AnswerStanding.RevisionStatement(DisplayLiteral.Of(AnswerId), [new Successor(RevisionDigest, RevisionId)]).Text;

        Assert.Contains(command, statement, StringComparison.Ordinal);
        Assert.Contains("the acceptance names", statement, StringComparison.Ordinal);
    }

    // ----- PK22 (client half) and PK26: the receipt's relation -----

    private static Result<AcceptanceReceipt> ReadReceipt(string tail)
    {
        var json = """{"thread_root":"01JTHREAD","post_id":"01JANSWER","accepted_at":"2026-10-27T12:00:00.0000000+00:00","already_accepted":false""" + tail + "}";
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), ClientJson.Limits);
        Assert.True(parsed.TryGetValue(out var value, out var parseError), parseError?.Type);
        return ForumDocuments.ReadAcceptance(Assert.IsType<JsonValue.Object>(value));
    }

    /// <summary>R8.76: the client reads the receipt's relation as null, <c>same-chain</c>, <c>other-answer</c>, or not stated for an absent member.</summary>
    [Fact]
    public void R8_76_PK22_TheClientReadsTheRelationInItsThreeStatesAndNotStated()
    {
        var member = PublishedReadersWords.R876Member();
        var values = PublishedReadersWords.R876Values();

        Assert.True(ReadReceipt($",\"replaced_answer\":\"{Old}\",\"{member}\":\"{values[0]}\"").TryGetValue(out var sameChain, out var error), error?.Type);
        Assert.True(ReadReceipt($",\"replaced_answer\":\"{Old}\",\"{member}\":\"{values[1]}\"").TryGetValue(out var other, out error), error?.Type);
        Assert.True(ReadReceipt($",\"replaced_answer\":null,\"{member}\":null").TryGetValue(out var none, out error), error?.Type);
        Assert.True(ReadReceipt($",\"replaced_answer\":\"{Old}\"").TryGetValue(out var absent, out error), error?.Type);

        Assert.Equal(AcceptanceRelation.SameChain, sameChain!.ReplacedRelation);
        Assert.Equal(AcceptanceRelation.OtherAnswer, other!.ReplacedRelation);
        Assert.Equal(AcceptanceRelation.None, none!.ReplacedRelation);
        Assert.Equal(AcceptanceRelation.NotStated, absent!.ReplacedRelation);
    }

    /// <summary>R8.76: a relation that is neither null nor one of the two words is a malformed response, not a value the client picks.</summary>
    [Theory]
    [InlineData("\"same\"")]
    [InlineData("\"\"")]
    [InlineData("7")]
    [InlineData("true")]
    [InlineData("{}")]
    public void R8_76_PK22_ARelationThatIsNoPublishedValueIsAMalformedResponse(string value)
    {
        var read = ReadReceipt($",\"replaced_answer\":\"{Old}\",\"{PublishedReadersWords.R876Member()}\":{value}");

        Assert.False(read.TryGetValue(out _, out var error), $"replaced_relation: {value} was read as a receipt");
        Assert.Equal("curia/client/response-malformed", error!.Type);
    }

    private static async Task<string> ReceiptTextAsync(AcceptanceReceipt receipt)
    {
        var (exit, stdout, _) = await ConsoleCapture.CaptureAsync(() => Task.FromResult(Curia.Client.Cli.Program.WriteAcceptance(receipt)));

        Assert.Equal(0, exit);
        return stdout.Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    /// <summary>
    /// R10.85: <c>curia resolve</c>'s receipt for an acceptance that replaced a post says whether it moved between texts of one answer or to another
    /// answer, and <c>NOT STATED</c> where the Forum served no relation; a replay says nothing of replacement (a control, green now).
    /// </summary>
    [Fact]
    public async Task R10_85_PK26_TheReceiptSaysWhetherTheAcceptanceMovedBetweenTextsOrToAnotherAnswer()
    {
        var phrases = PublishedReadersWords.R1085Phrases();
        var (notStated, moved, another) = (phrases[1], phrases[2], phrases[3]);

        var replay = await ReceiptTextAsync(new AcceptanceReceipt(Thread, AnswerId, At, true, AcceptanceReplaced.None, AcceptanceRelation.None, new BudgetReceipt("acceptance", 10, 3, 7), "answer", AnswerId));
        Assert.DoesNotContain(moved, replay, StringComparison.Ordinal);
        Assert.DoesNotContain(another, replay, StringComparison.Ordinal);
        Assert.DoesNotContain(notStated, replay, StringComparison.Ordinal);

        var sameChain = await ReceiptTextAsync(new AcceptanceReceipt(Thread, AnswerId, At, false, AcceptanceReplaced.Of(Old), AcceptanceRelation.SameChain));
        Assert.Contains(moved, sameChain, StringComparison.Ordinal);
        Assert.DoesNotContain(another, sameChain, StringComparison.Ordinal);

        var other = await ReceiptTextAsync(new AcceptanceReceipt(Thread, AnswerId, At, false, AcceptanceReplaced.Of(Old), AcceptanceRelation.OtherAnswer));
        Assert.Contains(another, other, StringComparison.Ordinal);
        Assert.DoesNotContain(moved, other, StringComparison.Ordinal);

        var absent = await ReceiptTextAsync(new AcceptanceReceipt(Thread, AnswerId, At, false, AcceptanceReplaced.Of(Old), AcceptanceRelation.NotStated, new BudgetReceipt("acceptance", 10, 3, 7)));
        var relationLines = absent.Split('\n').Where(l => l.Contains(notStated, StringComparison.Ordinal) && !l.StartsWith("replaced", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(relationLines);
    }

    // ----- PK29 (help half) -----

    /// <summary>R11.60: <c>curia resolve</c>'s help says what it takes (an answer's or a revision's id) and that the acceptance names the text of the id given.</summary>
    [Fact]
    public void R11_60_PK29_TheHelpOfResolveStatesWhatItTakesAndWhatTheAcceptanceNames()
    {
        var help = Help.For("resolve");

        Assert.NotNull(help);
        Assert.Contains(AcceptWording.Target, help, StringComparison.Ordinal);
        Assert.Contains(AcceptWording.NamesTheText, help, StringComparison.Ordinal);
    }
}
