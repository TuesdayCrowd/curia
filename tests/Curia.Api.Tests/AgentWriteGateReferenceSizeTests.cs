using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G48 at the size the pair of numbers is justified at, and through the real gate for the workloads two older facts asserted the opposite of
/// (probes BRB57, BRB63, BRB64; ruling 15, ruling 22). M3 measured the write at a log of about 2,000 events at 45 to 89 ms; a fixture of a few hundred events
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

    private static readonly SemaphoreSlim SeedGate = new(1, 1);
    private static bool _seeded;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Said(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.Ordinal)
            ? actual
            : throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");

    private async Task SeedLogAsync()
    {
        await SeedGate.WaitAsync(Ct);
        try
        {
            if (_seeded) return;
            var board = Probe.Board();
            var seeder = ForumAgent.Create("https://agents.example/seeder-" + Guid.NewGuid().ToString("N")[..8], "seeder");
            await BudgetBursts.SeedPostsAsync(forum, seeder, board, ReferenceEvents, Ct);
            _seeded = true;
        }
        finally
        {
            SeedGate.Release();
        }
    }

    /// <summary>
    /// BRB57. An honest burst at the reference size is served: with the log at about 2,000 events, a T1 agent with budget to spare sends 17 simultaneous comments
    /// (the holder and the 16 the queue holds); every one is 201, none is agent-in-flight, the table holds 17 new posts, and the whole burst is answered inside the
    /// published wait (read from the errata).
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
        var wait = PublishedBudgetGate.Wait();

        Said(
            $"{width} x 201; {width} new posts; answered inside {wait.TotalSeconds:0} s",
            $"{BudgetBursts.Tally(sent)}; {stored} new posts; answered {(elapsed < wait ? "inside" : "after")} {wait.TotalSeconds:0} s{(elapsed < wait ? string.Empty : $" ({elapsed.TotalMilliseconds:0} ms)")}");
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
