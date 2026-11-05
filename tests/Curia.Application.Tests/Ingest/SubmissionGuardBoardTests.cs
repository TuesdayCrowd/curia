#pragma warning disable CA2007 // test code: there is no SynchronizationContext to resume on (as the other suites of this project do, per await, with ConfigureAwait(true))
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// R8.87 (errata G61), RPB17 as amendment 3 rewrote it (ruling 36): the append's guard has no leg for a board. A stored post's board never changes, so nothing a late commit does can
/// invalidate the comparison the route has already made. Asserted from behaviour and not from the guard's member list: for an answer, a comment and a revision signed on a board
/// other than the one the log holds the post they follow on, the guard's verdict over a delta that holds nothing else is <c>Proceed</c>. A <b>control</b> on the tree it was added to;
/// its value is FRPB18 (a guard leg that refuses a board the route already compared).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SubmissionGuardBoardTests
{
    private const string Author = "https://agents.example/alice";
    private static readonly string OriginalDigest = "sha256:" + new string('a', 64);

    [Theory]
    [InlineData(PostKind.Answer)]
    [InlineData(PostKind.Comment)]
    [InlineData(PostKind.Revision)]
    public async Task R8_87_RPB17_TheGuardNeverRefusesAReplyForItsBoard_Control(PostKind kind)
    {
        var submission = await SubmissionOf.KindAsync(kind, Author);
        var log = new LateCommitLog();
        await log.AppendPostAsync("P000", "question", Author, "{\"kind\":\"question\"}", OriginalDigest, board: "board");
        var read = await log.ReadAsync();
        await log.AppendOtherAsync("test.unrelated");
        var delta = (await log.ReadAsync()).Skip(read.Count).ToArray();

        var guard = SubmissionGuard.For(LogFacts.Of(read), submission);
        var verdict = guard.Evaluate(delta).Match(() => "proceed", e => e.Type + " " + e.Detail, () => "redecide");

        var problems = new List<string>();
        if (string.Equals(submission.Envelope.Board, "board", StringComparison.Ordinal)) problems.Add("the submission is on the log's board, so the probe is not one of a reply onto another board");
        if (delta.Length != 1) problems.Add($"the delta holds {delta.Length} event(s), expected the one unrelated event");
        if (verdict != "proceed") problems.Add($"the guard's verdict is {verdict}");
        if (problems.Count > 0)
            throw new Xunit.Sdk.XunitException($"Expected: {kind} on another board than the post it follows: proceed over a delta of nothing else{Environment.NewLine}Actual:   {string.Join("; ", problems)}");
    }
}
