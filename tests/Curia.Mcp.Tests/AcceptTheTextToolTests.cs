using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// PR 4 (errata G44), Task 0's probes at <c>curia-mcp</c>: what <c>curia_read</c>'s first block states of a thread's acceptance (R11.60, R10.85), what
/// <c>curia_resolve</c> says it takes and how it words a refusal and a receipt (R11.60, R10.85), and the version (R11.60).
///
/// <para><b>Oracles.</b> The stub serves what each fact sets, so the expectation is the post a fact named and the digest and id it served; the words are
/// the errata's (<see cref="PublishedReadersWords"/>), the constants a description and a refusal share are <see cref="AcceptWording"/>'s, and the golden
/// line of an answer is G40's sentence written here, never read from the code under test. Pairs (plan, vacuity notes): <c>PK35</c> and <c>PK36</c> tell a
/// line drawn from the thread's members from one drawn from the thread root's own word; each is green under one of the two wrong builds and red under the
/// other.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptTheTextToolTests : IDisposable
{
    private const string RevisionId = "01TESTACCEPTEDREV00000000A";
    private const string QsId = "01TESTSTRANGERQUESTION0000A";
    private const string LyingId = "01TESTLYINGREVISION000000A";
    private const string OtherRevisionId = "01TESTREVISIONOFREV0000000A";

    private static readonly string RevisionDigest = "sha256:" + new string('7', 64);
    private static readonly string OtherRevisionDigest = "sha256:" + new string('6', 64);

    private readonly StubLog _log = new() { ThreadRouteServesTheThread = true };
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-accept-text-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string FirstBlockOf(CallToolResult read) =>
        string.Join('\n', read.Content.OfType<TextContentBlock>().Select(b => b.Text)).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string[] AcceptedLines(string firstBlock) =>
        [.. firstBlock.Split('\n').Where(l => l.StartsWith("accepted", StringComparison.Ordinal))];

    /// <summary>The first block of <c>curia_read</c> of the post, with the stub as the Forum.</summary>
    private async Task<string> FirstBlockAsync(string postId)
    {
        var read = await new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home)).ReadAsync(postId, Ct);
        return FirstBlockOf(read);
    }

    private static string One(string[] lines, string firstBlock)
    {
        Assert.True(lines.Length == 1, $"the first block held {lines.Length} accepted line(s):\n{firstBlock}");
        return lines[0];
    }

    // ----- PK35 to PK38: curia_read's accepted line is the thread root's word -----

    /// <summary>
    /// PK35 (R11.60): the question the thread belongs to names a revision as its accepted post, and the thread the stub serves lists no such post (the
    /// question and one answer, neither flagged accepted). The first block's line is stated from the question's member, with the kind: it is never
    /// <c>accepted none</c> for a thread whose question names a post.
    /// </summary>
    [Fact]
    public async Task R11_60_PK35_TheAcceptedLineNamesTheRevisionTheQuestionNamesEvenWhenNoMemberIsFlagged()
    {
        _log.AcceptedAnswer = StubAcceptedAnswer.Standing;
        _log.AcceptedAnswerNames = (RevisionDigest, RevisionId);
        _log.AcceptedAnswerKind = "revision";
        _log.AcceptedAnswerSupersededBy = [];

        var block = await FirstBlockAsync(StubLog.PostId);
        var line = One(AcceptedLines(block), block);

        Assert.Equal($"accepted revision={DisplayLiteral.Of(RevisionId)} {AnswerStanding.Basis}", line);
    }

    /// <summary>
    /// The scene of PK36: a revision RL whose <c>parent</c> names another question QS, served with QS's own word, <c>accepted_answer: null</c>, and a thread
    /// for QS that lists QS and RL. <paramref name="flagged"/> serves RL with <c>accepted: true</c> in that listing (a Forum that listed the post beneath a
    /// thread that is not its own, the build FPK35 describes); false serves it as R9.46 requires, not accepted.
    /// </summary>
    private void ServeTheLyingParentScene(bool flagged)
    {
        var question = new PostDraft { Kind = PostKind.Question, Board = "b", Title = "A stranger's question", Body = "Which finch is this?" };
        var signedQuestion = _log.SignDraft(question);
        var revision = new PostDraft { Kind = PostKind.Revision, Board = "b", Parent = QsId, Prev = "sha256:" + new string('e', 64), Body = "Corrected." };
        var signedRevision = _log.SignDraft(revision);
        _log.ServePost(QsId, question, signedQuestion, supersededBy: []);
        _log.ServePost(LyingId, revision, signedRevision, supersededBy: []);
        _log.ServedPosts[QsId] = _log.ServedPosts[QsId].Replace("\"key_status\":\"active\"", "\"key_status\":\"active\",\"accepted_answer\":null", StringComparison.Ordinal);
        _log.ServedThreads[QsId] = "[" + _log.ServedPosts[QsId] + ","
            + (flagged ? _log.ServedPosts[LyingId].Replace("\"accepted\":false", "\"accepted\":true", StringComparison.Ordinal) : _log.ServedPosts[LyingId]) + "]";
    }

    /// <summary>
    /// PK36 (R11.60), the lying-parent scene read through the walk up: <c>curia_read</c> of RL walks up its <c>parent</c> to QS, so the thread shown is QS's,
    /// and QS's own word is <c>accepted_answer: null</c>. <b>The control</b> (green now): the Forum flags no member accepted in QS's thread (R9.46), and the
    /// line says none and does not name RL. PK36b is the other half of the pair.
    /// </summary>
    [Fact]
    public async Task R11_60_PK36_AThreadReachedThroughALyingParentSaysNoneWhenNoMemberIsFlagged()
    {
        ServeTheLyingParentScene(flagged: false);

        var block = await FirstBlockAsync(LyingId);
        var line = One(AcceptedLines(block), block);

        Assert.StartsWith("accepted none", line, StringComparison.Ordinal);
        Assert.DoesNotContain(LyingId, line, StringComparison.Ordinal);
    }

    /// <summary>
    /// PK36b (R11.60): the same scene with RL served flagged accepted in QS's listing anyway. The line is the root's word, <c>accepted none</c>, because
    /// the question QS names nothing; a line drawn from the members' flags (today's) names RL as accepted in a thread whose asker accepted nothing.
    /// </summary>
    [Fact]
    public async Task R11_60_PK36b_TheLineIsTheRootsWordWhateverAMemberIsFlagged()
    {
        ServeTheLyingParentScene(flagged: true);

        var block = await FirstBlockAsync(LyingId);
        var line = One(AcceptedLines(block), block);

        Assert.StartsWith("accepted none", line, StringComparison.Ordinal);
        Assert.DoesNotContain(LyingId, line, StringComparison.Ordinal);
    }

    /// <summary>
    /// PK37 (R11.60): the statement of a revised accepted post is taken from the question's <c>accepted_answer.superseded_by</c>, not from the accepted
    /// post's own list. The two are served apart: the question's member lists a revision while the answer (flagged accepted, as today's line needs) lists
    /// none; an absent list on the member is <c>NOT STATED</c> while the answer's own list is empty. The third row is the control: a thread whose root is
    /// not a question has no member by design and keeps <c>accepted none</c>.
    /// </summary>
    [Fact]
    public async Task R11_60_PK37_TheRevisedStatementComesFromTheQuestionsMemberAndNotFromTheAcceptedPost()
    {
        _log.AcceptedPostIds.Add(StubLog.AnswerPostId);
        _log.AcceptedAnswer = StubAcceptedAnswer.Standing;
        _log.AcceptedAnswerKind = "answer";
        _log.AnswerSupersededBy = [];

        // The member lists a revision; the accepted post's own envelope lists none.
        _log.AcceptedAnswerSupersededBy = [new Successor(OtherRevisionDigest, OtherRevisionId)];
        var listed = One(AcceptedLines(await FirstBlockAsync(StubLog.PostId)), "member lists a revision");
        Assert.Contains("SUPERSEDED", listed, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(OtherRevisionId), listed, StringComparison.Ordinal);

        // The member was served without a list; the accepted post's own list is empty. The line says NOT STATED, never silence.
        _log.AcceptedAnswerSupersededBy = default;
        var notStatedWord = PublishedReadersWords.R1082Tokens()[1];
        var absent = One(AcceptedLines(await FirstBlockAsync(StubLog.PostId)), "member lists none");
        Assert.Contains("revision " + notStatedWord, absent, StringComparison.Ordinal);
    }

    /// <summary>
    /// PK25's other half (R10.85): the first block's statement of a revised accepted post says how the thread's asker accepts the revision, in the tool's
    /// words (<c>curia_resolve</c>), as <c>curia</c>'s statement names its command. The list is served on both the question's member and the answer's own,
    /// so the statement exists whichever of the two a build reads.
    /// </summary>
    [Fact]
    public async Task R10_85_PK25_TheFirstBlocksRevisedStatementNamesTheToolThatAcceptsTheRevision()
    {
        _log.AcceptedPostIds.Add(StubLog.AnswerPostId);
        _log.AcceptedAnswer = StubAcceptedAnswer.Standing;
        _log.AcceptedAnswerKind = "answer";
        _log.AcceptedAnswerSupersededBy = [new Successor(OtherRevisionDigest, OtherRevisionId)];
        _log.AnswerSupersededBy = [new Successor(OtherRevisionDigest, OtherRevisionId)];

        var block = await FirstBlockAsync(StubLog.PostId);
        var line = One(AcceptedLines(block), block);

        Assert.Contains("SUPERSEDED", line, StringComparison.Ordinal);
        Assert.Contains("curia_resolve", line, StringComparison.Ordinal);
    }

    /// <summary>PK37's control: a thread whose root is no question has no <c>accepted_answer</c> by design, and the line is <c>accepted none</c> (green now).</summary>
    [Fact]
    public async Task R11_60_PK37_ARootThatIsNoQuestionKeepsAcceptedNone()
    {
        const string Elsewhere = "01TESTNOPARENT0000000000AA";
        var comment = new PostDraft { Kind = PostKind.Comment, Board = "b", Parent = Elsewhere, Body = "A root that is a comment." };
        var signed = _log.SignDraft(comment);
        _log.ServePost(QsId, comment, signed, supersededBy: []);

        // Served as a root (parent null): the signed form is not touched, and the reader's walk up ends here.
        _log.ServedPosts[QsId] = _log.ServedPosts[QsId].Replace($"\"parent\":\"{Elsewhere}\"", "\"parent\":null", StringComparison.Ordinal);
        _log.ServedThreads[QsId] = "[" + _log.ServedPosts[QsId] + "]";

        var block = await FirstBlockAsync(QsId);

        Assert.StartsWith("accepted none", One(AcceptedLines(block), block), StringComparison.Ordinal);
    }

    /// <summary>PK38 (R11.60): an accepted post of kind <c>answer</c> is stated byte for byte as G40 stated it (<c>answer=&lt;id&gt;</c>, no kind word added). A control, green now.</summary>
    [Fact]
    public async Task R11_60_PK38_AnAcceptedAnswersLineIsG40sByteForByte()
    {
        _log.AcceptedPostIds.Add(StubLog.AnswerPostId);
        _log.AcceptedAnswer = StubAcceptedAnswer.Standing;
        _log.AcceptedAnswerKind = "answer";
        _log.AcceptedAnswerSupersededBy = [];
        _log.AnswerSupersededBy = [];

        var block = await FirstBlockAsync(StubLog.PostId);

        Assert.Equal($"accepted answer={DisplayLiteral.Of(StubLog.AnswerPostId)} {AnswerStanding.Basis}", One(AcceptedLines(block), block));
    }

    // ----- PK26 (MCP half): the receipt -----

    private async Task<string> ReceiptAsync()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using (agent)
        {
            var session = new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System);
            var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), new ForumWriter(agent!, session, TimeProvider.System));
            var result = await tools.ResolveAsync(StubLog.AnswerPostId, Ct);
            return string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        }
    }

    /// <summary>R10.85: <c>curia_resolve</c>'s receipt says whether a replaced acceptance moved between texts of one answer or to another, and <c>NOT STATED</c> without a relation; a replay says nothing of replacement.</summary>
    [Fact]
    public async Task R10_85_PK26_TheReceiptOfCuriaResolveSaysHowTheAcceptanceMoved()
    {
        var phrases = PublishedReadersWords.R1085Phrases();
        var (notStated, moved, another) = (phrases[1], phrases[2], phrases[3]);
        var values = PublishedReadersWords.R876Values();
        _log.AcceptanceAlreadyAccepted = false;
        _log.AcceptanceReplacedAnswerJson = "\"01TESTREPLACED000000000000A\"";

        _log.AcceptanceReplacedRelationJson = $"\"{values[0]}\"";
        var sameChain = await ReceiptAsync();
        _log.AcceptanceReplacedRelationJson = $"\"{values[1]}\"";
        var other = await ReceiptAsync();
        _log.AcceptanceReplacedRelationJson = null;
        var absent = await ReceiptAsync();
        _log.AcceptanceAlreadyAccepted = true;
        _log.AcceptanceReplacedAnswerJson = "null";
        _log.AcceptanceReplacedRelationJson = "null";
        var replay = await ReceiptAsync();

        Assert.DoesNotContain(moved, replay, StringComparison.Ordinal);
        Assert.DoesNotContain(another, replay, StringComparison.Ordinal);
        Assert.Contains(moved, sameChain, StringComparison.Ordinal);
        Assert.DoesNotContain(another, sameChain, StringComparison.Ordinal);
        Assert.Contains(another, other, StringComparison.Ordinal);
        Assert.DoesNotContain(moved, other, StringComparison.Ordinal);
        Assert.Contains(absent.Split('\n'), l => l.Contains(notStated, StringComparison.Ordinal) && !l.StartsWith("replaced", StringComparison.Ordinal));
    }

    // ----- PK28: a refusal worded by its detail -----

    /// <summary>
    /// PK28 (R11.60): <c>curia/accept/not-an-answer</c> is worded from the detail, whose <c>chain=&lt;root kind&gt;</c> names what a revision's chain ended
    /// at; the retired sentence, that only a post of kind answer can be accepted, is never said, whichever detail the Forum gives.
    /// </summary>
    [Theory]
    [InlineData("post=01TESTPOSTID0000000000000B chain=comment kind=revision", "comment")]
    [InlineData("post=01TESTPOSTID0000000000000B chain=question kind=revision", "question")]
    [InlineData("post=01TESTPOSTID0000000000000B kind=comment", "comment")]
    public async Task R11_60_PK28_TheRefusalNamesTheCauseAndNeverSaysOnlyAnAnswerCanBeAccepted(string detail, string rootKind)
    {
        _log.RefusesAcceptanceAsNotAnAnswerWith = detail;
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using (agent)
        {
            var session = new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System);
            var tools = new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home), new ForumWriter(agent!, session, TimeProvider.System));

            var refused = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => tools.ResolveAsync(StubLog.PostId, Ct));

            Assert.DoesNotContain("only a post of kind answer can be accepted", refused.Message, StringComparison.Ordinal);
            Assert.Contains(DisplayLiteral.Of(rootKind), refused.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing was recorded", refused.Message, StringComparison.Ordinal);
        }
    }

    // ----- PK29 (MCP half) and PK30 -----

    /// <summary>R11.60: the description of <c>curia_resolve</c> says what the tool takes and that the acceptance names the text of the id given.</summary>
    [Fact]
    public void R11_60_PK29_TheDescriptionOfCuriaResolveStatesWhatItTakesAndWhatTheAcceptanceNames()
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        using (agent)
        {
            var writer = new ForumWriter(agent!, new ForumSession(client, agent!, _log.Store, TimeProvider.System), TimeProvider.System);
            var tools = ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, new HeadStore(_home), writer));
            var description = tools.Single(t => t.ProtocolTool.Name == "curia_resolve").ProtocolTool.Description;

            Assert.False(string.IsNullOrWhiteSpace(description));
            Assert.Contains(AcceptWording.Target, description, StringComparison.Ordinal);
            Assert.Contains(AcceptWording.NamesTheText, description, StringComparison.Ordinal);
        }
    }

    /// <summary>R11.60: <c>curia-mcp</c> was 0.8.7 here, 0.8.8 since errata G45 and was 0.8.9 under errata G46 and was 0.8.10 under errata G47 and is 0.8.11 since errata G48 (the descriptions and a refusal's wording change; no tool is added): the pin follows the version, and RVO35 holds it too.</summary>
    [Fact]
    public void R11_60_PK30_TheVersionIs089()
    {
        Assert.True(string.Equals(PublishedBudgetReadout.Version(), ToolCatalogue.Version, StringComparison.Ordinal), $"Expected: {PublishedBudgetReadout.Version()} Actual: {ToolCatalogue.Version}");   // 0.9.0 since errata G49: the pin follows the errata's version
    }
}
