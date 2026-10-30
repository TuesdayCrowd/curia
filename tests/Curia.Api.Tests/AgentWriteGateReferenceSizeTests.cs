using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G48 at the size the pair of numbers is justified at, and through the real gate for the workloads two older facts asserted the opposite of
/// (probes BRB57, BRB63, BRB64, BRB67; ruling 15, ruling 22). M3 measured the write at a log of about 2,000 events at 45 to 89 ms; a fixture of a few hundred events
/// would have shown the gate green and the first honest fleet red (trap 62), so BRB57 seeds the log to the reference size through the store seam first.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AgentWriteGateReferenceSizeTests(AgentWriteGateForumFixture forum) : IClassFixture<AgentWriteGateForumFixture>
{
    private const int ReferenceEvents = 2_000;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Said(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.Ordinal)
            ? actual
            : throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");

    private async Task SeedLogAsync()
    {
        await forum.SeedGate.WaitAsync(Ct);
        try
        {
            if (forum.LogSeeded) return;
            var board = Probe.Board();
            var seeder = ForumAgent.Create("https://agents.example/seeder-" + Guid.NewGuid().ToString("N")[..8], "seeder");
            await BudgetBursts.SeedPostsAsync(forum, seeder, board, ReferenceEvents, Ct);
            forum.LogSeeded = true;
        }
        finally
        {
            forum.SeedGate.Release();
        }
    }

    /// <summary>
    /// BRB57, a recorded number and not a gate. With the log at about 2,000 events, a T1 agent with budget to spare sends 17 simultaneous comments (the holder and
    /// the 16 the queue holds). Every one is 201, none is a 5xx, and the table holds 17 new posts; how long the burst took and the p95 of the gate's waits are
    /// written to the test's diagnostics for M3's table and asserted nowhere, because a time bound is a fact about the machine (BRB67 proves the queue's places
    /// without a clock).
    /// </summary>
    [Fact]
    public async Task R7_27_BRB57_AnHonestBurstAtTheReferenceSizeIsServed()
    {
        await SeedLogAsync();
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var before = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
        var width = PublishedBudgetGate.Waiters() + 1;

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var sent = await BudgetBursts.BurstAsync(forum, actor, BudgetBursts.Wires(actor, "T1", board, parent.PostId, width, forum.Now), null, Ct);
        var elapsed = timer.Elapsed;
        var stored = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId) - before;

        var waits = forum.Gate.Waits.OrderBy(w => w).ToArray();
        var p95 = waits.Length == 0 ? TimeSpan.Zero : waits[Math.Min(waits.Length - 1, (int)Math.Ceiling(waits.Length * 0.95) - 1)];
        TestContext.Current.SendDiagnosticMessage(
            $"BRB57 recorded: {ReferenceEvents} events seeded; burst of {width} answered in {elapsed.TotalMilliseconds:0} ms; gate wait p95 {p95.TotalMilliseconds:0} ms of {waits.Length} entries");

        Said($"{width} x 201; {width} new posts; no 5xx", $"{BudgetBursts.Tally(sent)}; {stored} new posts; {(sent.Any(s => s.Status >= 500) ? "a 5xx" : "no 5xx")}");
    }

    /// <summary>
    /// BRB67. The queue's 17 places, without a clock: on a host whose queue clock moves only when the test moves it, 17 simultaneous comments of one agent (the holder and
    /// the 16 the queue holds) arrive, the clock is moved to a millisecond short of the wait, and the appends are released one at a time. Every one is 201, none is
    /// agent-in-flight, and the table holds 17 new posts. No wall time is read: the waits run out only if the test says so.
    /// </summary>
    [Fact]
    public async Task R7_27_BRB67_TheQueuesSeventeenPlacesAreServedWithoutAClock()
    {
        var wait = PublishedBudgetGate.Wait();
        var width = PublishedBudgetGate.Waiters() + 1;
        var clock = new StalledGateClock(forum.Clock);
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
        }));
        using var client = host.CreateClient();
        var gate = AgentWriteGateForumFixture.GateOf(host.Services);
        var store = AgentWriteGateForumFixture.StoreOf(host.Services);

        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var before = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
        var wires = BudgetBursts.Wires(actor, "T1", board, parent.PostId, width, forum.Now);

        store.ParkEvery(e => string.Equals(e.Type.Value, "post.accepted", StringComparison.Ordinal));
#pragma warning disable CA2025 // The burst is awaited below, before the client and the host are disposed.
        var burst = BudgetBursts.BurstAsync(forum, actor, wires, client, Ct);
#pragma warning restore CA2025
        var arrived = await Eventually(() => gate.Entered.Count >= width && store.Parked >= 1);
        clock.Advance(wait - TimeSpan.FromMilliseconds(1));

        var released = 0;
        for (var i = 0; i < width; i++)
        {
            if (!await Eventually(() => store.Parked > i)) break;
            store.ReleaseOne();
            released++;
        }

        var sent = await burst.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        var stored = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId) - before;
        store.Reset();

        Said(
            $"{width} reached the gate; {width} released one at a time; {width} x 201; {width} new posts",
            $"{(arrived ? width.ToString(System.Globalization.CultureInfo.InvariantCulture) : gate.Entered.Count + " of " + width)} reached the gate; {released} released one at a time; {BudgetBursts.Tally(sent)}; {stored} new posts");
    }

    /// <summary>Polls a condition of in-process state, not a wall-clock measurement: a safety bound of 30 s turns a hang into a failure.</summary>
    private static async Task<bool> Eventually(Func<bool> condition)
    {
        var give = Environment.TickCount64 + 30_000;
        while (!condition())
        {
            if (Environment.TickCount64 > give) return false;
            await Task.Delay(5, Ct);
        }

        return true;
    }

    /// <summary>BRB63. RVO13's workload through the real gate: two comments, two answers by a T1 author and two revisions of two of its posts, all at once: every one 201, none agent-in-flight or revision-in-flight, six new posts.</summary>
    [Fact]
    public async Task R7_27_BRB63_RVO13sWorkloadIsServedThroughTheGate()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var first = await Probe.AskAsync(forum, actor, board, Ct);
        var second = await Probe.AskAsync(forum, actor, board, Ct);
        var before = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);

        byte[][] wires =
        [
            BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now),
            BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now),
            BudgetBursts.Wire(actor.Agent, PostKind.Answer, board, parent.PostId, forum.Now),
            BudgetBursts.Wire(actor.Agent, PostKind.Answer, board, parent.PostId, forum.Now),
            actor.Agent.SignRevision(board, "Corrected " + Probe.Unrelated(), first.PostId, first.Digest, forum.Now),
            actor.Agent.SignRevision(board, "Corrected " + Probe.Unrelated(), second.PostId, second.Digest, forum.Now),
        ];
        var sent = await BudgetBursts.BurstAsync(forum, actor, wires, null, Ct);
        var stored = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId) - before;

        Said("6 x 201; 6 new posts", $"{BudgetBursts.Tally(sent)}; {stored} new posts");
    }

    /// <summary>BRB64. RVO45's workload through the real gate: four identical concurrent comments by one author are one 201 and three stored-duplicate refusals (R8.78's type), no 500, one new post.</summary>
    [Fact]
    public async Task R7_27_BRB64_RVO45sWorkloadStoresOnceThroughTheGate()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var before = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
        var wire = BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now);

        var sent = await BudgetBursts.BurstAsync(forum, actor, [wire, wire, wire, wire], null, Ct);
        var stored = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId) - before;
        var duplicate = PublishedRevisionSlugs.AlreadyRecorded();

        Said($"1 x 201, 3 x 409 {duplicate}; 1 new post", $"{BudgetBursts.Tally(sent)}; {stored} new post{(stored == 1 ? string.Empty : "s")}");
    }
}
