#pragma warning disable CA2007 // test code: there is no SynchronizationContext to resume on (as the other suites of this project do, per await, with ConfigureAwait(true))
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Domain.Moderation;
using Xunit;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// R8.77, R8.78, R8.55 (errata G45, G57), LCB20 (the table half): the three checks as pure functions over facts, against hand-written answers, so the extraction from the route changes no answer.
/// The route half (byte-equal answers over HTTP) is in the Api suite. Green on the seam: the extraction is the seam (a control, red under FLCB27).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SubmissionChecksTests
{
    private const string Author = "https://agents.example/author";
    private const string Voter = "https://agents.example/voter";
    private const string Other = "https://agents.example/other";

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Word(Curia.Domain.Primitives.Error? e) => e is null ? "none" : $"{e.Type} {e.Detail}";

    [Fact]
    public async Task R8_86_LCB20_TheChecksAnswerAsTheTableSaysTheyDo()
    {
        var log = new LateCommitLog();
        await log.AppendPostAsync("P1", "question", Author, "{\"kind\":\"question\"}");
        await log.AppendPostAsync("P2", "revision", Author, LateCommitLog.RevisionCanonical(Author, "board", LateCommitLog.Digest("P1")));
        await log.AppendPostAsync("P3", "question", Author, "{\"kind\":\"question\"}");
        await log.AppendPostAsync("P4", "revision", Author, LateCommitLog.RevisionCanonical(Author, "board", LateCommitLog.Digest("P3")));
        await log.AppendModerationAsync("P4", ModerationEffects.Wire(ModerationEffect.Withhold));
        await log.AppendPostAsync("P5", "question", Author, "{\"kind\":\"question\"}");
        await log.AppendPostAsync("P6", "revision", Other, LateCommitLog.RevisionCanonical(Other, "board", LateCommitLog.Digest("P5")));
        await log.AppendPostAsync("P7", "vote", Voter, LateCommitLog.VoteCanonical(Voter, "board", LateCommitLog.Digest("P1"), "n7"));
        await log.AppendPostAsync("P8", "vote", Voter, LateCommitLog.VoteCanonical(Voter, "board", LateCommitLog.Digest("P3"), "n8"));
        await log.AppendModerationAsync("P8", ModerationEffects.Wire(ModerationEffect.Withhold));
        var events = await log.ReadAsync();
        var facts = LogFacts.Of(events);

        var said = string.Join(" | ",
        [
            "recorded P2: " + (SubmissionChecks.AlreadyRecorded(events, LateCommitLog.Digest("P2")) ?? "none"),
            "recorded absent: " + (SubmissionChecks.AlreadyRecorded(events, "sha256:absent") ?? "none"),
            "revision of P1 (served successor P2): " + Word(SubmissionChecks.RevisionRefusal(facts, LateCommitLog.Digest("P1"), "sha256:new")),
            "revision of P3 (successor P4 withheld): " + Word(SubmissionChecks.RevisionRefusal(facts, LateCommitLog.Digest("P3"), "sha256:new")),
            "revision of P5 (P6 is by another author): " + Word(SubmissionChecks.RevisionRefusal(facts, LateCommitLog.Digest("P5"), "sha256:new")),
            "revision of an unknown prev: " + Word(SubmissionChecks.RevisionRefusal(facts, "sha256:unknown", "sha256:new")),
            "revision with a recorded digest: " + Word(SubmissionChecks.RevisionRefusal(facts, LateCommitLog.Digest("P5"), LateCommitLog.Digest("P2"))),
            "vote by the voter for P1: " + Word(SubmissionChecks.AlreadyVoted(facts, Voter, LateCommitLog.Digest("P1"))),
            "vote by the voter for P3 (the earlier vote is withheld): " + Word(SubmissionChecks.AlreadyVoted(facts, Voter, LateCommitLog.Digest("P3"))),
            "vote by another for P1: " + Word(SubmissionChecks.AlreadyVoted(facts, Other, LateCommitLog.Digest("P1"))),
        ]);

        Said(
            string.Join(" | ",
            [
                "recorded P2: P2",
                "recorded absent: none",
                "revision of P1 (served successor P2): curia/ingest/already-revised successor=P2",
                "revision of P3 (successor P4 withheld): none",
                "revision of P5 (P6 is by another author): none",
                "revision of an unknown prev: curia/ingest/revision-check-unavailable ",
                "revision with a recorded digest: curia/ingest/already-recorded post=P2",
                $"vote by the voter for P1: curia/verification/already-voted {LateCommitLog.Digest("P1")}",
                "vote by the voter for P3 (the earlier vote is withheld): none",
                "vote by another for P1: none",
            ]),
            said);
    }
}
