using System.Diagnostics.CodeAnalysis;
using System.Net;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G49 against the agent write gate's seams (<see cref="AgentWriteGateForumFixture"/>: the host's event store parks an append on demand and its
/// gate records every entry): SEB19, the readout is not gated, and SEB21, the receipt is computed inside the hold.
///
/// <para><b>No wall-clock claim.</b> Each fact parks an append and asserts on completion order and on what was read and stored, never on elapsed time. The
/// bounded waits below only wait for a request to <i>reach</i> a seam; a request that never arrives fails the fact by its own message.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetReadoutGateEndpointTests(AgentWriteGateForumFixture forum) : IClassFixture<AgentWriteGateForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static bool IsPost(Curia.Domain.DomainEvent e) => string.Equals(e.Type.Value, "post.accepted", StringComparison.Ordinal);

    private async Task WaitForEntriesAsync(int count)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);
        while (forum.Gate.Entered.Count < count && DateTimeOffset.UtcNow < deadline) await Task.Delay(10, Ct);
        Assert.True(forum.Gate.Entered.Count >= count, $"Expected: {count} requests at the agent gate Actual: {forum.Gate.Entered.Count}");
    }

    /// <summary>
    /// SEB19. Not gated: agent A's append is parked and a second write by A is queued behind it. A's readout is <c>200</c> (not <c>409</c>), excludes the
    /// parked act, and is answered while the append is still parked (completion order, never elapsed time); after the release a readout counts both; the
    /// gate's observer records no entry for the readout.
    /// </summary>
    [Fact]
    public async Task R7_28_SEB19_TheReadoutIsNotGatedAndExcludesAParkedAct()
    {
        forum.ResetSeams();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "parked", Ct);
        var board = Probe.Board();
        try
        {
            forum.Store.ParkNext(IsPost);
            var first = BudgetReadouts.AskAsync(forum, actor, board);
            Assert.True(await forum.Store.WaitForParkedAsync(1, TimeSpan.FromSeconds(20)), "Expected: the first append parked Actual: it never reached the store");
            var second = BudgetReadouts.AskAsync(forum, actor, board);
            await WaitForEntriesAsync(2);

            var during = await BudgetReadouts.GetAsync(forum, actor);

            Assert.True(during.Status == HttpStatusCode.OK, $"Expected: 200 while an append is parked and a write is queued Actual: {during.Says}");
            Assert.False(first.IsCompleted || second.IsCompleted, "Expected: the readout answered while the append was still parked");
            Probe.Said("posting used=0", $"posting used={during.Entry("posting").GetProperty("used").GetInt32()}");
            Assert.Equal(2, forum.Gate.Entered.Count);

            forum.Store.Release();
            var statuses = new[] { (await first).Status, (await second).Status };
            Assert.Equal("201 201", $"{statuses[0]} {statuses[1]}");
            var after = await BudgetReadouts.ReadAsync(forum, actor);
            Probe.Said("posting used=2", $"posting used={after.Entry("posting").GetProperty("used").GetInt32()}");
            Assert.Equal(2, forum.Gate.Entered.Count);
        }
        finally
        {
            forum.ResetSeams();
        }
    }

    /// <summary>
    /// SEB21. The receipt is inside the hold: agent A has one of three posts used; A's append is parked and B (the same agent) is queued behind it. After
    /// the release the receipts say <c>used</c> 2 then 3 and <c>remaining</c> 1 then 0, the events table holds 3, and the whole-log reads the two requests
    /// made are what the same two requests make one after the other (no second read for the receipt). The control, the pass-through gate: the same interleave stores the
    /// same three posts and the receipts read the same count twice (2 and 2), so the fact sees what a count outside the hold would say.
    /// </summary>
    [Fact]
    public async Task R7_29_SEB21_TheReceiptIsInsideTheHold()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        try
        {
            // The control for the reads: the same two requests, one after the other, make the reads that two requests make; the interleave must make no more.
            var sequential = await AcceptedAnswerScene.EnrolAsync(forum, "sequential", Ct);
            await BudgetBursts.SeedPostsAsync(forum, sequential.Agent, board, 1, Ct);
            var before = forum.Store.ForwardReads;
            Assert.Equal(201, (await BudgetReadouts.AskAsync(forum, sequential, board)).Status);
            Assert.Equal(201, (await BudgetReadouts.AskAsync(forum, sequential, board)).Status);
            var readsForTwoPosts = forum.Store.ForwardReads - before;

            var (real, reads) = await InterleaveAsync(board, passThrough: false);
            Probe.Said(
                "receipts posting used 2 remaining 1 then posting used 3 remaining 0",
                $"receipts {Says(real.First)} then {Says(real.Second)}");
            Assert.True(reads <= readsForTwoPosts, $"Expected: no more whole-log reads than the same two requests make one after the other ({readsForTwoPosts}) Actual: {reads}");

            forum.ResetSeams();
            var (control, _) = await InterleaveAsync(board, passThrough: true);
            // The control reads the same count twice: the expected text is the receipts alone (as written first it carried this parenthetical and could never equal the actual).
            Probe.Said("receipts used 2 then used 2", $"receipts used {Used(control.First)} then used {Used(control.Second)}");
        }
        finally
        {
            forum.ResetSeams();
        }
    }

    private static string Says(Act act) =>
        act.Budget.ValueKind == System.Text.Json.JsonValueKind.Object
            ? $"{act.Budget.GetProperty("name").GetString()} used {act.Budget.GetProperty("used").GetInt32()} remaining {act.Budget.GetProperty("remaining").GetInt32()}"
            : "no budget member";

    private static string Used(Act act) => act.Budget.ValueKind == System.Text.Json.JsonValueKind.Object ? act.Budget.GetProperty("used").GetInt32().ToString(System.Globalization.CultureInfo.InvariantCulture) : "absent";

    private async Task<((Act First, Act Second), int Reads)> InterleaveAsync(string board, bool passThrough)
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "interleave", Ct);
        await BudgetBursts.SeedPostsAsync(forum, actor.Agent, board, 1, Ct);
        forum.Gate.UsePassThrough = passThrough;
        var reads = forum.Store.ForwardReads;

        forum.Store.ParkNext(IsPost);
        var first = BudgetReadouts.AskAsync(forum, actor, board);
        Assert.True(await forum.Store.WaitForParkedAsync(1, TimeSpan.FromSeconds(20)), "Expected: the first append parked Actual: it never reached the store");
        var second = BudgetReadouts.AskAsync(forum, actor, board);
        if (passThrough) await second;
        else await WaitForEntriesAsync(2);

        forum.Store.Release();
        var pair = (await first, await second);
        var stored = await forum.SnapshotAsync(actor.Agent.AgentId);
        Assert.True(stored.Posts == 3, $"Expected: 3 posts stored for the agent Actual: {stored.Posts} ({pair.Item1.Says}; {pair.Item2.Says})");
        return (pair, forum.Store.ForwardReads - reads);
    }
}
