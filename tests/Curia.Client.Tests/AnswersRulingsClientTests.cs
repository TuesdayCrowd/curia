using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// PR 9 Amendment 2's probes at the reference reader: ANC11 and ANC12 (ruling 28: the duplicate refusal's count is the document's own list), ACA8 (ruling 27: the acceptance's instant and the order's NOT STATED words), and
/// RVT6's Client leg (ruling 25: an absent <c>revises</c> prints nothing). <b>Oracles:</b> the JSON each row writes and the words ruling 27 fixes, held as hand literals here and never read from
/// <c>AnswerStanding</c>, so the definition cannot satisfy its own probe.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AnswersRulingsClientTests : IDisposable
{
    private static readonly string RevisionDigest = "sha256:" + new string('b', 64);
    private const string RevisionId = "01ACA8REVISION00000000001";

    private readonly AnswersWireHarness _wire = new();
    private readonly StubLog _log = new();

    public void Dispose()
    {
        _wire.Dispose();
        _log.Dispose();
        GC.SuppressFinalize(this);
    }

    private static Curia.Canon.Json.JsonValue.Object Parse(string json)
    {
        var parsed = JsonReader.Parse(Encoding.UTF8.GetBytes(json), ClientJson.Limits);
        Assert.True(parsed.TryGetValue(out var value, out var error), error?.Type);
        return Assert.IsType<Curia.Canon.Json.JsonValue.Object>(value);
    }

    private static DuplicateRefusalDocument? DuplicateOf(JsonObject document)
    {
        var refusal = new Refusal(RefusalKind.Conflict, 409, new Curia.Domain.Primitives.Error("curia/posts/duplicate-question", "duplicate"), Parse(document.ToJsonString()));
        return refusal.AsDuplicate;
    }

    // ---- ANC11 (ruling 28) ----------------------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// ANC11, the CLI leg. A refusal document whose <c>answers</c> array holds two answers this client reads and one it cannot prints <c>answers      3 served</c> (the document's own list, readable and not) and, beneath it,
    /// the line that says one could not be read. <c>Answers.Length</c> alone would print 2 beside a thread that has three. The hand count is the array the probe wrote. FANC11's form.
    /// </summary>
    [Fact]
    public void R9_69_ANC11_TheRefusalCountsTheDocumentsListReadableAndNot()
    {
        var document = JsonNode.Parse(_log.DuplicateRefusalJson())!.AsObject();
        var answers = document["answers"]!.AsArray();
        answers.Add(JsonNode.Parse(answers[0]!.ToJsonString()));
        answers.Add(JsonNode.Parse("{\"post_id\":7}"));
        Assert.Equal(3, answers.Count);

        var duplicate = DuplicateOf(document);
        Assert.NotNull(duplicate);
        var lines = Output.DuplicateFrame(duplicate, []).Split('\n');

        Assert.Equal(2, duplicate.Answers.Length);
        Assert.Equal(1, duplicate.UnreadableAnswers);
        var count = Assert.Single(lines, l => l.StartsWith("answers", StringComparison.Ordinal));
        Assert.StartsWith("answers      3 served", count, StringComparison.Ordinal);
        Assert.Contains(lines, l => l.TrimStart().StartsWith("and 1 this client could not read", StringComparison.Ordinal));
    }

    /// <summary>
    /// ANC12, ruling 28's control: a duplicate-refusal document with no <c>answers</c> array is not read as a duplicate document (the plain refusal is what prints), so the line that says a count is never printed for a
    /// count the Forum did not serve, and no word of the plain refusal says <c>served</c>. The branch ruling 22 named (<c>NOT STATED</c> for the count) is unreachable, not merely untested.
    /// </summary>
    [Fact]
    public void R9_69_ANC12_ADocumentWithNoAnswersArrayIsReadAsNoDuplicateAndSaysNothingServed()
    {
        var document = JsonNode.Parse(_log.DuplicateRefusalJson())!.AsObject();
        document.Remove("answers");
        var refusal = new Refusal(RefusalKind.Conflict, 409, new Curia.Domain.Primitives.Error("curia/posts/duplicate-question", "duplicate"), Parse(document.ToJsonString()));

        Assert.Null(refusal.AsDuplicate);
        Assert.DoesNotContain("served", refusal.Summary, StringComparison.Ordinal);
    }

    // ---- ACA8 (ruling 27) -----------------------------------------------------------------------------------------------------------------------------------------------

    private void ServeAcceptanceWith(Action<JsonObject>? edit)
    {
        _wire.Rewriter.Edits.Clear();
        _wire.Log.AcceptedAnswer = StubAcceptedAnswer.Standing;
        _wire.Log.AcceptedAnswerSupersededBy = AnswersWireHarness.Revision(RevisionDigest, RevisionId);
        if (edit is not null) _wire.Edit(StubLog.PostId, post => edit(post["provenance"]!["accepted_answer"]!.AsObject()));
    }

    /// <summary>
    /// ACA8. An acceptance object the Forum served with no <c>accepted_at</c> prints <c>(accepted at NOT STATED)</c> (R10.100: a member R9.71 says an acceptance carries and the Forum did not serve); a non-empty
    /// <c>superseded_by</c> with no <c>first_revision</c> prints ruling 27's third wording and <b>never</b> "since revised", which could be false there. Hand literals from ruling 27. FACA10's form.
    /// </summary>
    [Fact]
    public async Task R10_100_ACA8_AnAbsentInstantAndAnAbsentOrderAreNotStatedAndNeverSinceRevised()
    {
        ServeAcceptanceWith(null);

        var line = Assert.Single(AnswersWireHarness.Lines((await _wire.PassageAsync(StubLog.PostId)).Render(), "answers  "));

        Assert.Contains("(accepted at NOT STATED)", line, StringComparison.Ordinal);
        Assert.Contains("which its author has revised, and whether that was before or after the asker accepted it is NOT STATED", line, StringComparison.Ordinal);
        Assert.DoesNotContain("since revised", line, StringComparison.Ordinal);
        Assert.DoesNotContain("already revised", line, StringComparison.Ordinal);
    }

    /// <summary>ACA8's control: where both are served the words are ruling 27's own, the instant a display literal and the order's first fragment the one for its relation.</summary>
    [Theory]
    [InlineData("after_acceptance", "which its author has since revised")]
    [InlineData("before_acceptance", "which its author had already revised when the asker accepted it")]
    public async Task R10_100_ACA8_Control_ServedInstantAndOrderUseRuling27sFragments(string relation, string fragment)
    {
        ServeAcceptanceWith(accepted =>
        {
            accepted["accepted_at"] = "2026-11-24T00:40:40.7654940+00:00";
            accepted["first_revision"] = relation;
        });

        var line = Assert.Single(AnswersWireHarness.Lines((await _wire.PassageAsync(StubLog.PostId)).Render(), "answers  "));

        Assert.Contains(" (accepted at \"2026-11-24T00:40:40.7654940+00:00\"), " + fragment + ": SUPERSEDED by", line, StringComparison.Ordinal);
    }

    // ---- RVT6's Client leg (ruling 25) ----------------------------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// RVT6, the Client leg. A revision served with no <c>revises</c> (the stub serves every other member of the revision) prints no line that mentions <c>revises</c> and <c>NOT STATED</c> on no line of its frame:
    /// an absent <c>revises</c> is not a statement (a revision of an answer or a comment carries none, and a reader of one revision cannot tell that from an older Forum's). The pairing control is RVT4, where a
    /// revision served with the member prints the pointer, so the silence is not a reader that never prints. FRVT5's form.
    /// </summary>
    [Fact]
    public async Task R10_100_RVT6_ARevisionServedWithNoRevisesPrintsNoPointerAndNoNotStated()
    {
        _wire.Log.AcceptedAnswer = StubAcceptedAnswer.Absent;
        _wire.Edit(StubLog.PostId, post =>
        {
            post["kind"] = "revision";
            post["parent"] = "01RVT6PARENT0000000000000B";
        });

        var rendered = (await _wire.PassageAsync(StubLog.PostId)).Render();
        var frame = AnswersWireHarness.FrameLines(rendered);

        Assert.Contains(frame, l => l.StartsWith("kind      \"revision\"", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("revises", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(frame, l => l.StartsWith("thread    this text revises", StringComparison.Ordinal));
        Assert.DoesNotContain(frame, l => l.Contains("NOT STATED", StringComparison.Ordinal));
    }
}
