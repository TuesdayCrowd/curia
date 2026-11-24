using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.ConsoleCapture;

namespace Curia.Client.Tests;

/// <summary>
/// R10.100 (errata G76) at the reference reader, probes ANC8, ANC9 (every form but the inbox's rows, which an end-to-end class holds), RVT3 and RVT4 (the printed half), ACA4 and ACA6 (the
/// words). The page is the stub's honest signed question behind <see cref="ProvenanceRewriter"/>, and <b>the oracle is the JSON each row served and the errata's own sentence</b> (R10.100's
/// paragraph is read at test time: the phrase that says what the count counts), never <c>AnswerStanding</c>, which the probes do not call. The expected lines are quoted beside the sentence
/// they pin; a word R10.100 leaves to the build (the gloss beyond the errata's phrase, the pointer's punctuation) is held only by the tokens the sentence itself uses.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AnswersLinesTests : IDisposable
{
    private static readonly string[] Forbidden = ["irrelevant", "obsolete", "outdated", "wrong", "stale", "invalid"];
    private static readonly string RevisionDigest = "sha256:" + new string('b', 64);
    private const string RevisionId = "01ACA4REVISION00000000001";
    private const string Instant = "2026-11-24T00:40:40.7654940+00:00";

    private readonly AnswersWireHarness _wire = new();

    public void Dispose()
    {
        _wire.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>R10.100's phrase for what the count counts, read from the errata (the probe fails if the sentence stops holding it).</summary>
    private static string CountCounts() =>
        G64Words.Phrase(PublishedAcceptBudget.Paragraph("**R10.100**"), "posts the Forum serves");

    private static string Served(int n) => $"{{\"served\":{n.ToString(CultureInfo.InvariantCulture)}}}";

    private async Task<string> RenderedAsync(string postId = "") =>
        (await _wire.PassageAsync(postId.Length == 0 ? StubLog.PostId : postId)).Render();

    private void Accepted(string state)
    {
        switch (state)
        {
            case "accepted":
                _wire.Log.AcceptedAnswer = StubAcceptedAnswer.Standing;
                _wire.Log.AcceptedAnswerSupersededBy = [];
                break;
            case "none accepted":
                _wire.Log.AcceptedAnswer = StubAcceptedAnswer.None;
                break;
            default:
                _wire.Log.AcceptedAnswer = StubAcceptedAnswer.Absent;
                break;
        }
    }

    public static TheoryData<string> AcceptanceStates => new() { "accepted", "none accepted", "NOT STATED" };

    // ---- ANC8 ---------------------------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// ANC8, the count served as N (2) and as 0: <c>answers  2 served, none accepted ...</c> and <c>answers  0 served, none accepted ...</c> (R10.100's own example), and
    /// <c>answers  2 served, accepted: post "..."</c>; the count comes before the acceptance clause, the line says what the count counts (the errata's phrase), and the old
    /// "nothing of whether answers exist" is gone. Red on the tree (no count).
    /// </summary>
    [Theory]
    [InlineData(2, "none accepted")]
    [InlineData(0, "none accepted")]
    [InlineData(2, "accepted")]
    [InlineData(2, "NOT STATED")]
    public async Task R10_100_ANC8_TheLineStatesTheCountBeforeTheAcceptanceClause(int served, string acceptance)
    {
        Accepted(acceptance);
        _wire.Member(StubLog.PostId, "answers", Served(served));

        var lines = AnswersWireHarness.Lines(await RenderedAsync(), "answers  ");

        var line = Assert.Single(lines);
        var lead = $"answers  {served.ToString(CultureInfo.InvariantCulture)} served, ";
        Assert.True(line.StartsWith(lead, StringComparison.Ordinal), $"Expected: a line beginning \"{lead}\" Actual: {line}");
        var clause = acceptance switch { "accepted" => "accepted: post ", "none accepted" => "none accepted", _ => "NOT STATED" };
        Assert.True(line.IndexOf(clause, lead.Length, StringComparison.Ordinal) > 0, $"Expected: the acceptance clause \"{clause}\" after the count Actual: {line}");
        Assert.Contains(CountCounts(), line, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing of whether answers exist", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// ANC8, the count not served: <c>answers  NOT STATED ... none accepted ...</c>, never <c>0 served</c>. The count's own NOT STATED comes first and the acceptance clause still stands.
    /// Red on the tree for the <c>none accepted</c> and <c>accepted</c> cells (the line is the old one, with no word on the count).
    /// </summary>
    [Theory]
    [InlineData("none accepted")]
    [InlineData("accepted")]
    public async Task R10_100_ANC8_ACountTheForumDidNotServeIsNotStatedAndNeverZero(string acceptance)
    {
        Accepted(acceptance);

        var line = Assert.Single(AnswersWireHarness.Lines(await RenderedAsync(), "answers  "));

        Assert.StartsWith("answers  NOT STATED", line, StringComparison.Ordinal);
        Assert.DoesNotContain("0 served", line, StringComparison.Ordinal);
        var clause = acceptance == "accepted" ? "accepted: post " : "none accepted";
        Assert.True(line.IndexOf(clause, StringComparison.Ordinal) > line.IndexOf("NOT STATED", StringComparison.Ordinal), $"Expected: the acceptance clause \"{clause}\" after the count's NOT STATED Actual: {line}");
    }

    /// <summary>ANC8, the cell where both are unstated: the old line (green on the tree), the count's NOT STATED first. A control that the line does not lose the acceptance's own word.</summary>
    [Fact]
    public async Task R10_100_ANC8_Control_BothUnstatedStaysNotStated()
    {
        Accepted("NOT STATED");

        var line = Assert.Single(AnswersWireHarness.Lines(await RenderedAsync(), "answers  "));

        Assert.StartsWith("answers  NOT STATED", line, StringComparison.Ordinal);
        Assert.DoesNotContain("served,", line, StringComparison.Ordinal);
    }

    /// <summary>ANC8: an answer, a comment and a revision have no <c>answers</c> line whatever the count (control; green on the tree).</summary>
    [Theory]
    [InlineData("answer")]
    [InlineData("comment")]
    public async Task R10_100_ANC8_Control_APostThatIsNotAQuestionHasNoAnswersLine(string kind)
    {
        _wire.Edit(StubLog.PostId, post => post["kind"] = kind);

        Assert.Empty(AnswersWireHarness.Lines(await RenderedAsync(), "answers  "));
    }

    // ---- ANC9 ---------------------------------------------------------------------------------------------------------------------------------------------------------------

    private async Task<(int Exit, string Stdout)> SearchAsync(bool titles)
    {
        var args = Args.Parse(titles ? ["search", "x", "--titles"] : ["search", "x"], 1);
        var (exit, stdout, _) = await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, _wire.Client, MarkingMode.None, null, TestContext.Current.CancellationToken));
        Assert.True(exit is 0 or 9, $"curia search exited {exit}");
        return (exit, stdout);
    }

    /// <summary>
    /// ANC9, the default search hit line: it prints no kind today, so the count is on the line of a question hit and only where the member is present. The line that names the hit's id also
    /// states <c>2 served</c>. Red on the tree (FANC6's form: a one-definition edit does not reach this line).
    /// </summary>
    [Fact]
    public async Task R10_100_ANC9_TheDefaultSearchHitLineCarriesTheCountOfAQuestionHit()
    {
        Accepted("none accepted");
        _wire.Member(StubLog.PostId, "answers", Served(2));

        var (_, stdout) = await SearchAsync(titles: false);

        var hit = Assert.Single(stdout.Split('\n'), l => l.Contains(StubLog.PostId, StringComparison.Ordinal));
        Assert.True(hit.Contains("2 served", StringComparison.Ordinal), $"Expected: the hit line states \"2 served\" Actual: {hit}");
    }

    /// <summary>ANC9, a control that is green on the tree: a hit that is not a question carries no count on its line, whatever the page served.</summary>
    [Fact]
    public async Task R10_100_ANC9_Control_TheHitLineOfAnAnswerCarriesNoCount()
    {
        _wire.Edit(StubLog.PostId, post => post["kind"] = "answer");

        var (_, stdout) = await SearchAsync(titles: false);

        var hit = Assert.Single(stdout.Split('\n'), l => l.Contains(StubLog.PostId, StringComparison.Ordinal));
        Assert.DoesNotContain("served", hit, StringComparison.Ordinal);
    }

    /// <summary>ANC9, <c>search --titles</c> (FANC7's form): the row of a question states <c>answers 2 served, none accepted ...</c> (the row form has one space). Red on the tree.</summary>
    [Fact]
    public async Task R10_100_ANC9_TheTitledIndexRowCarriesTheCount()
    {
        Accepted("none accepted");
        _wire.Member(StubLog.PostId, "answers", Served(2));

        var (_, stdout) = await SearchAsync(titles: true);

        var row = Assert.Single(stdout.Split('\n'), l => l.TrimStart().StartsWith("answers ", StringComparison.Ordinal));
        Assert.StartsWith("answers 2 served, none accepted", row.TrimStart(), StringComparison.Ordinal);
    }

    /// <summary>ANC9, the screen's H6 row (FANC8's form): the header row <c>forum says ...</c> of a question carries the count. Red on the tree.</summary>
    [Fact]
    public async Task R10_100_ANC9_TheScreensH6RowCarriesTheCount()
    {
        Accepted("none accepted");
        _wire.Member(StubLog.PostId, "answers", Served(2));
        var passage = await _wire.PassageAsync(StubLog.PostId);

        var h6 = PassageHeader.Rows(passage, 1, 400).Select(r => r.Text).Single(r => r.StartsWith("forum says ", StringComparison.Ordinal));

        Assert.Contains("2 served", h6, StringComparison.Ordinal);
    }

    /// <summary>ANC9, <c>curia read</c> (the passage frame, FANC5a's form) through <see cref="Program.RenderAsync"/>: the printed read carries the count in its frame, above the span.</summary>
    [Fact]
    public async Task R10_100_ANC9_CuriaReadPrintsTheCountInTheFrameAboveTheSpan()
    {
        Accepted("none accepted");
        _wire.Member(StubLog.PostId, "answers", Served(2));
        var read = await _wire.ReadAsync(StubLog.PostId);
        Assert.True(read.TryGetValue(out var post, out _));

        var (exit, stdout, _) = await CaptureAsync(
            () => Program.RenderAsync(_wire.Client, [post!], StubLog.Forum, null, TestContext.Current.CancellationToken));

        Assert.Equal(0, exit);
        var at = stdout.IndexOf("answers  2 served, none accepted", StringComparison.Ordinal);
        Assert.True(at >= 0, $"Expected: curia read prints \"answers  2 served, none accepted\" Actual: {string.Join(" / ", stdout.Split('\n').Where(l => l.StartsWith("answers", StringComparison.Ordinal)))}");
        Assert.True(at < stdout.IndexOf(Curia.Domain.Serving.Datamarking.OpenDelimiter, StringComparison.Ordinal));
    }

    // ---- RVT3 and RVT4: the pointer ----------------------------------------------------------------------------------------------------------------------------------------

    private const string RootId = "01RVT4ROOT00000000000000A";

    private void ServeRevision(string revisesJson)
    {
        _wire.Log.AcceptedAnswer = StubAcceptedAnswer.Absent;
        _wire.Edit(StubLog.PostId, post =>
        {
            post["kind"] = "revision";
            post["parent"] = "01RVT4PARENT0000000000000B";
            post["provenance"]!["revises"] = System.Text.Json.Nodes.JsonNode.Parse(revisesJson);
        });
    }

    /// <summary>
    /// RVT4 (the pointer, design 3): <c>curia read</c> of a revision of a question states that the text revises the post named, that it is a question, that its answers and the answer accepted are
    /// read there and not here, and names <c>curia thread &lt;id&gt;</c>; and it prints no <c>answers</c> line of its own (no count on a revision). Red on the tree.
    /// </summary>
    [Fact]
    public async Task R10_100_RVT4_TheRevisionsFramePointsAtTheQuestionAndNamesTheCommandThatReadsIt()
    {
        ServeRevision("{\"post_id\":\"" + RootId + "\",\"kind\":\"question\"}");

        var rendered = await RenderedAsync();

        var line = Assert.Single(AnswersWireHarness.Lines(rendered, "thread    this text revises post "));
        Assert.Contains(DisplayLiteral.Of(RootId), line, StringComparison.Ordinal);
        Assert.Contains("a question", line, StringComparison.Ordinal);
        Assert.Contains("read there", line, StringComparison.Ordinal);
        Assert.Contains("not here", line, StringComparison.Ordinal);
        Assert.True(ShellWord.TryOf(RootId, out var word));
        Assert.Contains($"curia thread {word}", line, StringComparison.Ordinal);
        Assert.Empty(AnswersWireHarness.Lines(rendered, "answers  "));
    }

    /// <summary>RVT3 (printed): where <c>post_id</c> is absent (the Forum does not serve the root), the line says the question is not served and names no command that would read it. Red on the tree.</summary>
    [Fact]
    public async Task R10_100_RVT3_AnUnservedRootIsSaidToBeNotServed()
    {
        ServeRevision("{\"kind\":\"question\"}");

        var line = Assert.Single(AnswersWireHarness.Lines(await RenderedAsync(), "thread    this text revises"));

        Assert.Contains("not served", line, StringComparison.Ordinal);
        Assert.DoesNotContain("curia thread", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// RVT4 (errata G17): a hostile root id (a newline that would start a forged line, a quote, an escape character, a bidirectional override) is written as a display literal: no raw control
    /// character reaches the frame, the id appears as its literal, and no <c>answers</c> line is forged by it. Red on the tree (FRVT3's form is the raw print).
    /// </summary>
    [Fact]
    public async Task R10_100_RVT4_AHostileRootIdIsADisplayLiteralAndForgesNoLine()
    {
        const string Hostile = "01RVT4\nanswers  2 served, accepted: post \"x\"\u001b[31m\u202e";
        ServeRevision(System.Text.Json.JsonSerializer.Serialize(new { post_id = Hostile, kind = "question" }));

        var rendered = await RenderedAsync();

        var frame = AnswersWireHarness.FrameLines(rendered);
        var pointer = Assert.Single(frame, l => l.StartsWith("thread    this text revises post ", StringComparison.Ordinal));
        Assert.Contains(DisplayLiteral.Of(Hostile), pointer, StringComparison.Ordinal);
        Assert.DoesNotContain(frame, l => l.StartsWith("answers  ", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', string.Concat(frame));
        Assert.DoesNotContain('\u202e', string.Concat(frame));
    }

    // ---- ACA4: the acceptance's words --------------------------------------------------------------------------------------------------------------------------------------

    private void ServeAcceptance(string? relation, bool withCount = false)
    {
        _wire.Rewriter.Edits.Clear();
        _wire.Log.AcceptedAnswer = StubAcceptedAnswer.Standing;
        _wire.Log.AcceptedAnswerSupersededBy = AnswersWireHarness.Revision(RevisionDigest, RevisionId);
        _wire.Edit(StubLog.PostId, post =>
        {
            var accepted = post["provenance"]!["accepted_answer"]!.AsObject();
            accepted["accepted_at"] = Instant;
            if (relation is not null) accepted["first_revision"] = relation;
            if (withCount) post["provenance"]!["answers"] = System.Text.Json.Nodes.JsonNode.Parse(Served(2));
        });
    }

    /// <summary>
    /// ACA4, <c>after_acceptance</c>: the line says "since revised" and prints the instant as the Forum's word, a display literal. (The "since revised" half is green on the tree, which says it
    /// of every non-empty list; the instant is red.)
    /// </summary>
    [Fact]
    public async Task R10_100_ACA4_AfterAcceptanceSaysSinceRevisedAndPrintsTheInstant()
    {
        ServeAcceptance("after_acceptance");

        var line = Assert.Single(AnswersWireHarness.Lines(await RenderedAsync(), "answers  "));

        // Ruling 27: the full fragment, hand-written here and not read from AnswerStanding.
        Assert.Contains("which its author has since revised", line, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(Instant), line, StringComparison.Ordinal);
    }

    /// <summary>
    /// ACA4, <c>before_acceptance</c>: "since revised" appears for no other state, and the line says the asker accepted a text its author had already revised. Red on the tree (which says
    /// "has since revised" of every non-empty list: the word that can be false).
    /// </summary>
    [Fact]
    public async Task R10_100_ACA4_BeforeAcceptanceNeverSaysSinceRevisedAndSaysTheAskerAcceptedARevisedText()
    {
        ServeAcceptance("before_acceptance");

        var line = Assert.Single(AnswersWireHarness.Lines(await RenderedAsync(), "answers  "));

        Assert.DoesNotContain("since revised", line, StringComparison.Ordinal);
        Assert.Contains("which its author had already revised when the asker accepted it", line, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(Instant), line, StringComparison.Ordinal);
    }

    /// <summary>ACA4, the same two words in the one-row forms (the titled row and the screen's H6): the three forms cannot say it differently.</summary>
    [Theory]
    [InlineData("before_acceptance", "which its author had already revised when the asker accepted it", "since revised")]
    [InlineData("after_acceptance", "which its author has since revised", "already revised")]
    public async Task R10_100_ACA4_TheRowFormsSayTheSameAsTheFrame(string relation, string says, string doesNotSay)
    {
        ServeAcceptance(relation);
        var passage = await _wire.PassageAsync(StubLog.PostId);

        var row = Assert.Single(AnswersWireHarness.IndexRows(passage), l => l.TrimStart().StartsWith("answers ", StringComparison.Ordinal));
        var h6 = PassageHeader.Rows(passage, 1, 400).Select(r => r.Text).Single(r => r.StartsWith("forum says ", StringComparison.Ordinal));

        foreach (var text in new[] { row, h6 })
        {
            Assert.Contains(says, text, StringComparison.Ordinal);
            Assert.DoesNotContain(doesNotSay, text, StringComparison.Ordinal);
        }
    }

    // ---- ACA6: the answer's words ------------------------------------------------------------------------------------------------------------------------------------------

    private const string FirstRevisionId = "01ACA6REV000000000000000A";

    private void ServeAnswer(string? questionRevisedJson)
    {
        _wire.Log.AcceptedAnswer = StubAcceptedAnswer.Absent;
        _wire.Edit(StubLog.PostId, post =>
        {
            post["kind"] = "answer";
            if (questionRevisedJson is not null) post["provenance"]!["question_revised"] = System.Text.Json.Nodes.JsonNode.Parse(questionRevisedJson);
        });
    }

    private static string Revised(string relation, string? postId = FirstRevisionId) =>
        "{\"first_revision\":{\"digest\":\"" + RevisionDigest + "\"" + (postId is null ? string.Empty : ",\"post_id\":\"" + postId + "\"") + "},\"answer_posted\":\"" + relation + "\"}";

    /// <summary>
    /// ACA6, <c>before</c> (design 6, ruling 23): <c>question  revised after this answer was posted: this answer was posted against the text before the revision (the Forum's word; it says nothing of
    /// whether the answer fits either text)</c>, the first revision named the way the receipt names it (digest and post id, display literals), outside the span. Red on the tree.
    /// </summary>
    [Fact]
    public async Task R10_100_ACA6_BeforeSaysTheAnswerWasPostedAgainstTheTextBeforeTheRevision()
    {
        ServeAnswer(Revised("before"));

        var rendered = await RenderedAsync();

        var line = Assert.Single(AnswersWireHarness.Lines(rendered, "question  "));
        Assert.StartsWith("question  revised after this answer was posted: ", line, StringComparison.Ordinal);
        Assert.Contains("this answer was posted against the text before the revision", line, StringComparison.Ordinal);
        Assert.Contains("(the Forum's word; it says nothing of whether the answer fits either text)", line, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(RevisionDigest), line, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(FirstRevisionId), line, StringComparison.Ordinal);
        Assert.True(rendered.IndexOf(line, StringComparison.Ordinal) < rendered.IndexOf(Curia.Domain.Serving.Datamarking.OpenDelimiter, StringComparison.Ordinal));
    }

    /// <summary>ACA6, <c>after</c>: <c>question  revised before this answer was posted: which text this answer was written against is not stated</c> (an answer posted after may have been written against either). Red on the tree.</summary>
    [Fact]
    public async Task R10_100_ACA6_AfterSaysWhichTextTheAnswerWasWrittenAgainstIsNotStated()
    {
        ServeAnswer(Revised("after", postId: null));

        var line = Assert.Single(AnswersWireHarness.Lines(await RenderedAsync(), "question  "));

        Assert.StartsWith("question  revised before this answer was posted: ", line, StringComparison.Ordinal);
        Assert.Contains("which text this answer was written against is not stated", line, StringComparison.Ordinal);
        Assert.Contains(DisplayLiteral.Of(RevisionDigest), line, StringComparison.Ordinal);
    }

    /// <summary>ACA6: an answer the Forum served with no <c>question_revised</c> (an older Forum) prints <c>NOT STATED</c> on its <c>question</c> line (R10.100); one served <c>null</c> (no revision) prints no word of a revision. Red on the tree for the first.</summary>
    [Fact]
    public async Task R10_100_ACA6_AbsentIsNotStatedAndNullSaysNothingOfARevision()
    {
        ServeAnswer(null);
        var absent = Assert.Single(AnswersWireHarness.Lines(await RenderedAsync(), "question  "));
        Assert.Contains("NOT STATED", absent, StringComparison.Ordinal);

        ServeAnswer("null");
        var none = AnswersWireHarness.Lines(await RenderedAsync(), "question  ");
        Assert.DoesNotContain(none, l => l.Contains("revised", StringComparison.Ordinal));
    }

    /// <summary>
    /// ACA6, the forbidden words: none of <c>irrelevant</c>, <c>obsolete</c>, <c>outdated</c>, <c>wrong</c>, <c>stale</c>, <c>invalid</c> in any line this PR writes (the answer's <c>question</c> line in both
    /// states, the acceptance's lines in both, the question's count, the revision's pointer): principle P1, no claim of relevance. <b>Non-vacuity:</b> every line is required to exist first.
    /// </summary>
    [Fact]
    public async Task R10_100_ACA6_NoLineThisChangeWritesClaimsRelevanceOrAge()
    {
        var lines = new List<string>();

        ServeAnswer(Revised("before"));
        lines.AddRange(AnswersWireHarness.Lines(await RenderedAsync(), "question  "));
        ServeAnswer(Revised("after"));
        lines.AddRange(AnswersWireHarness.Lines(await RenderedAsync(), "question  "));
        ServeAcceptance("before_acceptance", withCount: true);
        lines.AddRange(AnswersWireHarness.Lines(await RenderedAsync(), "answers  "));
        ServeAcceptance("after_acceptance", withCount: true);
        lines.AddRange(AnswersWireHarness.Lines(await RenderedAsync(), "answers  "));
        ServeRevision("{\"post_id\":\"" + RootId + "\",\"kind\":\"question\"}");
        lines.AddRange(AnswersWireHarness.Lines(await RenderedAsync(), "thread    this text revises"));

        Assert.Equal(5, lines.Count);
        foreach (var line in lines)
            foreach (var word in Forbidden)
                Assert.DoesNotContain(word, line, StringComparison.OrdinalIgnoreCase);
    }
}
