using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R10.100 (errata G76) at the adapter, probes ANC10, ACA4's MCP copy, ACA6's MCP leg and RVT5, each MCP copy with its own row (items 347 and 351: the copy left unseen is the MCP's). The Forum is
/// <see cref="StubLog"/> behind <see cref="ProvenanceRewriter"/>; <b>the oracle is the JSON each row served</b> and the CLI's words for the same members (<c>answers  2 served, none accepted</c>,
/// R10.100's own example). The inbox leg compares the line inside the titled row to the passage's line: one definition, so the claim is that the three forms cannot differ.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AnswersCountToolTests : IDisposable
{
    private readonly StubLog _log = new() { ThreadRouteServesTheThread = true };
    private readonly ProvenanceRewriter _rewriter;
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-answers-count-").FullName;
    private readonly List<IDisposable> _owned = [];

    public AnswersCountToolTests() => _rewriter = new ProvenanceRewriter(_log.Handler);

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        _rewriter.Dispose();
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private ForumTools Tools()
    {
        var http = new HttpClient(_rewriter, disposeHandler: false) { BaseAddress = StubLog.Forum };
        _owned.Add(http);
        var client = new ForumClient(http, StubLog.Forum);
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        return new ForumTools(client, MarkingMode.None, new HeadStore(_home), new ForumWriter(agent!, new ForumSession(client, agent!, _log.Store, TimeProvider.System), TimeProvider.System));
    }

    private static string[] Passages(CallToolResult result) =>
        [.. result.Content.OfType<EmbeddedResourceBlock>().Select(b => ((TextResourceContents)b.Resource).Text)];

    private static string FirstBlock(CallToolResult result) => result.Content.OfType<TextContentBlock>().First().Text;

    private static string[] Lines(string passage, string prefix) =>
        [.. passage.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').TakeWhile(l => l.Length > 0).Where(l => l.TrimStart().StartsWith(prefix, StringComparison.Ordinal))];

    private static string QuestionPassage(string[] passages) =>
        Assert.Single(passages, p => p.StartsWith($"post      \"{StubLog.PostId}\"", StringComparison.Ordinal));

    private void Serve(Action<JsonObject> edit) => _rewriter.Edits[StubLog.PostId] = edit;

    private void ServeQuestionWithCount(string? servedJson)
    {
        _log.AcceptedAnswer = StubAcceptedAnswer.None;
        Serve(post =>
        {
            if (servedJson is not null) post["provenance"]!["answers"] = JsonNode.Parse(servedJson);
        });
    }

    private void ServeInboxOfTheStubQuestion() =>
        _log.InboxJson = "{\"results\":[" + _log.PostJson() + "],\"next_cursor\":null,\"open_before_exclusions\":1,\"excluded_as_own\":0,\"excluded_as_already_answered\":0}";

    // ---- ANC10 ------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// ANC10. A question passage from <c>curia_search</c>, <c>curia_read</c> and <c>curia_inbox</c> carries the same <c>answers</c> line, byte for byte: <c>answers  2 served, none accepted ...</c> as R10.100 words
    /// it; the inbox's titled row writes the one-row form of the same statement (<c>answers 2 served, none accepted ...</c>, one space). Red on the tree.
    /// </summary>
    [Fact]
    public async Task R10_100_ANC10_TheThreeToolsCarryTheSameCountLine()
    {
        ServeQuestionWithCount("{\"served\":2}");
        ServeInboxOfTheStubQuestion();
        var tools = Tools();

        var search = Lines(QuestionPassage(Passages(await tools.SearchAsync(new SearchCriteria(), TestContext.Current.CancellationToken))), "answers  ");
        var read = Lines(QuestionPassage(Passages(await tools.ReadAsync(StubLog.PostId, TestContext.Current.CancellationToken))), "answers  ");

        var searchLine = Assert.Single(search);
        var readLine = Assert.Single(read);
        Assert.StartsWith("answers  2 served, none accepted", searchLine, StringComparison.Ordinal);
        Assert.Equal(searchLine, readLine);

        await using var session = await McpProtocolSession.StartAsync(tools, "https://agents.example/alice");
        Assert.True((await session.ListAsync()).Any(t => t.Name == "curia_inbox"), "Expected: curia_inbox listed Actual: absent");
        var inbox = await session.CallAsync("curia_inbox", []);
        Assert.False(inbox.IsError, inbox.Text);
        var inboxLine = Assert.Single(Lines(Assert.Single(inbox.Blocks.Skip(1)), "answers "));
        Assert.Equal(searchLine["answers  ".Length..], inboxLine.TrimStart()["answers ".Length..]);
    }

    /// <summary>ANC10's MCP copy of the unserved count (FANC5b's row): a question served without <c>answers</c> reads <c>NOT STATED</c> in the passage of both reads and never <c>0 served</c>. Red on the tree (the old line says neither).</summary>
    [Fact]
    public async Task R10_100_ANC10_AnUnservedCountIsNotStatedInTheToolsPassages()
    {
        ServeQuestionWithCount(null);
        var tools = Tools();

        foreach (var passages in new[] { Passages(await tools.SearchAsync(new SearchCriteria(), TestContext.Current.CancellationToken)), Passages(await tools.ReadAsync(StubLog.PostId, TestContext.Current.CancellationToken)) })
        {
            var line = Assert.Single(Lines(QuestionPassage(passages), "answers  "));
            Assert.StartsWith("answers  NOT STATED", line, StringComparison.Ordinal);
            Assert.DoesNotContain("0 served", line, StringComparison.Ordinal);
        }
    }

    // ---- ACA4's MCP copy (FACA8) ---------------------------------------------------------------------------------------------------------------------------------------

    private const string RevisionId = "01ACA4REVISION00000000001";

    private void ServeAcceptance(string relation)
    {
        _log.AcceptedAnswer = StubAcceptedAnswer.Standing;
        _log.AcceptedAnswerSupersededBy = [new Successor("sha256:" + new string('b', 64), RevisionId)];
        Serve(post =>
        {
            var accepted = post["provenance"]!["accepted_answer"]!.AsObject();
            accepted["accepted_at"] = "2026-11-24T00:40:40.7654940+00:00";
            accepted["first_revision"] = relation;
        });
    }

    /// <summary>
    /// ACA4's MCP copy: the first block of <c>curia_read</c> composes its own <c>accepted</c> line (<c>AcceptedLine</c>), a copy of the passage's facts. For <c>before_acceptance</c> it must say the asker accepted a
    /// text its author had already revised and not "since revised"; for <c>after_acceptance</c> it says "since revised". (The words are the passage's, held by ACA4's Client leg: the plan names the probe and
    /// not its words, so these tokens are this probe's assumption.) Red on the tree: the line says neither.
    /// </summary>
    [Theory]
    [InlineData("before_acceptance", "which its author had already revised when the asker accepted it", "since revised")]
    [InlineData("after_acceptance", "which its author has since revised", "already revised")]
    public async Task R10_100_ACA4_TheFirstBlocksAcceptedLineStatesTheOrder(string relation, string says, string doesNotSay)
    {
        ServeAcceptance(relation);

        var first = FirstBlock(await Tools().ReadAsync(StubLog.PostId, TestContext.Current.CancellationToken));

        var line = Assert.Single(first.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'), l => l.StartsWith("accepted ", StringComparison.Ordinal));
        Assert.Contains(says, line, StringComparison.Ordinal);
        Assert.DoesNotContain(doesNotSay, line, StringComparison.Ordinal);
    }

    // ---- ACA6's MCP leg --------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// ACA6's MCP leg: an answer served with <c>question_revised</c> is printed in <c>curia_read</c>'s passage as the line of design 6, both words, and the forbidden words (<c>irrelevant</c>, <c>obsolete</c>,
    /// <c>outdated</c>, <c>wrong</c>, <c>stale</c>, <c>invalid</c>) are in neither. Red on the tree.
    /// </summary>
    [Theory]
    [InlineData("before", "question  revised after this answer was posted: ")]
    [InlineData("after", "question  revised before this answer was posted: ")]
    public async Task R10_100_ACA6_TheAnswersPassageStatesWhichSideOfTheRevisionItWasPostedOn(string relation, string lead)
    {
        _rewriter.Edits[StubLog.AnswerPostId] = post => post["provenance"]!["question_revised"] = JsonNode.Parse(
            "{\"first_revision\":{\"digest\":\"sha256:" + new string('b', 64) + "\",\"post_id\":\"01ACA6REV000000000000000A\"},\"answer_posted\":\"" + relation + "\"}");

        var passages = Passages(await Tools().ReadAsync(StubLog.PostId, TestContext.Current.CancellationToken));

        var answer = Assert.Single(passages, p => p.StartsWith($"post      \"{StubLog.AnswerPostId}\"", StringComparison.Ordinal));
        var line = Assert.Single(Lines(answer, "question  "));
        Assert.StartsWith(lead, line, StringComparison.Ordinal);
        foreach (var word in new[] { "irrelevant", "obsolete", "outdated", "wrong", "stale", "invalid" })
            Assert.DoesNotContain(word, line, StringComparison.OrdinalIgnoreCase);
    }

    // ---- RVT5 -------------------------------------------------------------------------------------------------------------------------------------------------------------

    private const string Root = "01RVT5ROOT00000000000000A";
    private const string Revision = "01RVT5REVISION000000000A1";

    private string ServeRevisedThread()
    {
        var question = new PostDraft { Kind = PostKind.Question, Board = "b", Title = "A question about finches", Body = "Which finch is this?" };
        var signedQuestion = _log.SignDraft(question);
        var revisionDraft = new PostDraft { Kind = PostKind.Revision, Board = "b", Parent = Root, Prev = signedQuestion.PrefixedDigest, Body = "Which finch is this, exactly?" };
        var signedRevision = _log.SignDraft(revisionDraft);
        _log.ServePost(Root, question, signedQuestion, [new Successor(signedRevision.PrefixedDigest, Revision)]);
        _log.ServePost(Revision, revisionDraft, signedRevision, []);
        _log.ServedThreads[Root] = "[" + _log.ServedPosts[Root] + "," + _log.ServedPosts[Revision] + "]";
        return signedQuestion.PrefixedDigest;
    }

    /// <summary>
    /// RVT5, the control: <c>curia_read</c> of a revision of a question returns the root's thread, and its first block is R11.62's lines unchanged: the thread line, the accepted line (the Forum served none: <c>NOT STATED</c>)
    /// and the root's <c>SUPERSEDED</c> line naming the revision. Hand-written from R11.41, R11.60 and R11.62's words; green on the tree, and it must stay green unedited (FRVT4 removes the pointer and must not move it).
    /// </summary>
    [Fact]
    public async Task R10_100_RVT5_Control_CuriaReadOfARevisionKeepsTheRootsFirstBlock()
    {
        ServeRevisedThread();

        var result = await Tools().ReadAsync(Revision, TestContext.Current.CancellationToken);

        var lines = FirstBlock(result).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        Assert.Equal($"thread root={DisplayLiteral.Of(Root)} posts=2", lines[0]);
        Assert.StartsWith("accepted NOT STATED", lines[1], StringComparison.Ordinal);
        Assert.Contains("SUPERSEDED", lines[2], StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(Revision), lines[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// RVT5, the other half: the pointer is a passage line only. The revision's own passage (inside the thread <c>curia_read</c> returns) carries the line <c>thread    this text revises post ...</c> that
    /// <c>curia read</c> prints, and the first block carries no such line. Red on the tree (no pointer), while the control above is green.
    /// </summary>
    [Fact]
    public async Task R10_100_RVT5_ThePointerIsAPassageLineAndNeverInTheFirstBlock()
    {
        ServeRevisedThread();
        _rewriter.Edits[Revision] = post => post["provenance"]!["revises"] = JsonNode.Parse("{\"post_id\":\"" + Root + "\",\"kind\":\"question\"}");

        var result = await Tools().ReadAsync(Revision, TestContext.Current.CancellationToken);

        var revisionPassage = Assert.Single(Passages(result), p => p.StartsWith($"post      \"{Revision}\"", StringComparison.Ordinal));
        Assert.Single(Lines(revisionPassage, "thread    this text revises post "));
        Assert.DoesNotContain("this text revises", FirstBlock(result), StringComparison.Ordinal);
    }

    // ---- ACA9 (ruling 27): the first block and the passage say the acceptance in the same words ------------------------------------------------------------------------------

    /// <summary>
    /// ACA9. For one served acceptance the first block's <c>accepted</c> line of <c>curia_read</c> and the passage's <c>answers</c> line (the CLI's frame, which the adapter renders through the same <c>Passage</c>) carry the
    /// same three fragments of ruling 27: the instant, the order's words for its relation, and that the answer is named. The expected substrings are hand-written here once, from the ruling, and both outputs are asked
    /// to contain them, so the two forms are one definition and not two that agree today. FACA11's form: the MCP composing the order words itself.
    /// </summary>
    [Theory]
    [InlineData("before_acceptance", "which its author had already revised when the asker accepted it")]
    [InlineData("after_acceptance", "which its author has since revised")]
    public async Task R10_100_ACA9_TheFirstBlockAndThePassageSayTheAcceptanceInTheSameWords(string relation, string order)
    {
        ServeAcceptance(relation);

        var result = await Tools().ReadAsync(StubLog.PostId, TestContext.Current.CancellationToken);
        var accepted = Assert.Single(FirstBlock(result).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'), l => l.StartsWith("accepted ", StringComparison.Ordinal));
        var passage = Assert.Single(Lines(QuestionPassage(Passages(result)), "answers  "));

        var instant = " (accepted at \"2026-11-24T00:40:40.7654940+00:00\"), ";
        foreach (var form in new[] { accepted, passage })
        {
            Assert.Contains(instant + order + ": SUPERSEDED by", form, StringComparison.Ordinal);
            Assert.Contains($"\"{RevisionId}\"", form, StringComparison.Ordinal);
        }
    }

    // ---- RVT6's MCP leg (ruling 25) ---------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// RVT6, the MCP leg (its own row: items 347 and 351). The revision's passage inside the thread <c>curia_read</c> returns, served with no <c>revises</c>, carries no line that mentions <c>revises</c> and
    /// <c>NOT STATED</c> on no line of its frame; the first block is unchanged (RVT5's control). FRVT6's form: the adapter's passage path printing <c>NOT STATED</c> for it.
    /// </summary>
    [Fact]
    public async Task R10_100_RVT6_ARevisionServedWithNoRevisesPrintsNoPointerAndNoNotStatedInThePassage()
    {
        ServeRevisedThread();

        var result = await Tools().ReadAsync(Revision, TestContext.Current.CancellationToken);

        var revisionPassage = Assert.Single(Passages(result), p => p.StartsWith($"post      \"{Revision}\"", StringComparison.Ordinal));
        var frame = revisionPassage.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').TakeWhile(l => l.Length > 0).ToArray();
        Assert.Contains(frame, l => l.StartsWith("kind      \"revision\"", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("revises", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(frame, l => l.Contains("NOT STATED", StringComparison.Ordinal));
    }
}
