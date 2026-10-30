using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using Probe = Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G48 (R7.27) at the surface an agent uses: probes BRB16 to BRB41 and BRB60 (BRB22, BRB61 and BRB62 are in <c>AgentWriteGateTwoHostTests</c>, BRB57, BRB63
/// and BRB64 in <c>AgentWriteGateReferenceSizeTests</c>). The oracle is the events table counted from the fixture's own connection string (<see cref="BudgetOracle"/>)
/// against the limit the white paper (Table 11) and R7.26 publish, never a response's claim about itself; the gate's bound, the two problem types and the
/// budget reasons are the errata's (<see cref="PublishedBudgetGate"/>). Task 0 seam: no route enters the gate yet, so each fact that observes the gate or the
/// count under a burst is red by assertion.
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AgentWriteGateEndpointTests(AgentWriteGateForumFixture forum) : IClassFixture<AgentWriteGateForumFixture>
{
    private const string PostsKind = "post.accepted";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Said(string expected, string actual) =>
        string.Equals(expected, actual, StringComparison.Ordinal)
            ? actual
            : throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");

    private static string Author(DomainEvent e) =>
        e.Payload is JsonValue.Object payload && payload.Members.FirstOrDefault(m => m.Key == "author").Value is JsonValue.String s ? s.Value : string.Empty;

    private static string AcceptedBy(DomainEvent e) =>
        e.Payload is JsonValue.Object payload && payload.Members.FirstOrDefault(m => m.Key == "accepted_by").Value is JsonValue.String s ? s.Value : string.Empty;

    private static Func<DomainEvent, bool> PostBy(string agent) => e => e.Type.Value == PostsKind && Author(e) == agent;

    private static Func<DomainEvent, bool> AcceptBy(string agent) => e => e.Type.Value == "answer.accepted" && AcceptedBy(e) == agent;

    /// <summary>"once" or "N times", so that a green run reads exactly as the expected text does.</summary>
    internal static string Times(int n) => n == 1 ? "once" : $"{n} times";

    private static string Ms(TimeSpan span) => span.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>BRB54, the limits' half: Table 11's three posting limits and R7.26's three acceptance limits parse out of the published texts. A control for the parsers; red if a span is removed.</summary>
    [Fact]
    public void R7_27_BRB54_TheLimitsParse()
    {
        Said(
            "posting 3, 25, 100; acceptance 10, 50, 200",
            $"posting {string.Join(", ", BudgetBursts.Tiers.Select(BudgetBursts.PostLimit))}; acceptance {string.Join(", ", BudgetBursts.Tiers.Select(BudgetBursts.AcceptLimit))}");
    }

    // ---- the forced interleaves (BRB16, BRB17) and their control (BRB20) ------------------------------------------------------------------------------------

    /// <summary>A parked at its append, B sent: what B read and stored while A was parked, and the table after A is released.</summary>
    private sealed record Interleave(string Report, long StoredAfter, string AKind, string BKind);

    private async Task<Interleave> PostInterleaveAsync(bool passThrough)
    {
        forum.ResetSeams();
        forum.Gate.UsePassThrough = passThrough;
        var limit = BudgetBursts.PostLimit("T0");
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "interleave", Ct);
        await BudgetBursts.SeedPostsAsync(forum, actor.Agent, board, limit - 1, Ct);
        var seeded = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
        var token = await actor.Dpop.GetTokenAsync(forum.Client, BudgetBursts.TokenEndpoint, forum.Now, Ct);
        var wireA = BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now);
        var wireB = BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now);

        forum.Store.ParkNext(PostBy(actor.Agent.AgentId));
        try
        {
            var a = Task.Run(() => Probe.SendAsync(forum, actor, wireA, token, Ct), Ct);
            var parked = await forum.Store.WaitForParkedAsync(1, TimeSpan.FromSeconds(15));
            var readsBefore = forum.Store.ForwardReads;
            var b = Task.Run(() => Probe.SendAsync(forum, actor, wireB, token, Ct), Ct);
            await Task.Delay(700, Ct);
            var readsWhileParked = forum.Store.ForwardReads - readsBefore;
            var storedWhileParked = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
            var bDoneWhileParked = b.IsCompleted;

            forum.Store.Release();
            var sentA = await a;
            var sentB = await b;
            var after = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
            var report =
                $"{(parked ? string.Empty : "A never reached its append; ")}seeded {seeded} of {limit - 1}; while A is parked B read the log {readsWhileParked} times, " +
                $"{(bDoneWhileParked ? "was answered" : "was still waiting")} and the table held {storedWhileParked}; after release the table holds {after}, A {BudgetBursts.Kind(sentA)}, B {BudgetBursts.Kind(sentB)}";
            return new Interleave(report, after, BudgetBursts.Kind(sentA), BudgetBursts.Kind(sentB));
        }
        finally
        {
            forum.Store.Release();
        }
    }

    /// <summary>BRB16. The forced interleave, posts: with one slot left and A parked at its append, B reads nothing and stores nothing until A is released, and then is told the budget.</summary>
    [Fact]
    public async Task R7_27_BRB16_ARequestParkedAtItsAppendHoldsTheBudgetForTheNext()
    {
        var limit = BudgetBursts.PostLimit("T0");
        var (posting, _) = PublishedBudgetGate.BudgetReasons();

        var result = await PostInterleaveAsync(passThrough: false);

        Said(
            $"seeded {limit - 1} of {limit - 1}; while A is parked B read the log 0 times, was still waiting and the table held {limit - 1}; after release the table holds {limit}, A 201, B 403 {posting}",
            result.Report);
    }

    private async Task<(string Report, long StoredAfter)> AcceptInterleaveAsync(bool passThrough)
    {
        forum.ResetSeams();
        forum.Gate.UsePassThrough = passThrough;
        var limit = BudgetBursts.AcceptLimit("T0");
        var board = Probe.Board();
        var asker = await AcceptedAnswerScene.EnrolAsync(forum, "accept-interleave", Ct);
        var answerer = ForumAgent.Create("https://agents.example/answerer-" + Guid.NewGuid().ToString("N")[..8], "answerer");
        await BudgetBursts.SeedAcceptsAsync(forum, asker.Agent, answerer, board, limit - 1, Ct);
        var seeded = await BudgetOracle.AcceptsInWindowAsync(forum, asker.Agent.AgentId);
        var threadA = await BudgetBursts.StoredThreadAsync(forum, asker.Agent, answerer, board, Ct);
        var threadB = await BudgetBursts.StoredThreadAsync(forum, asker.Agent, answerer, board, Ct);
        var token = await asker.Dpop.GetTokenAsync(forum.Client, BudgetBursts.TokenEndpoint, forum.Now, Ct);

        forum.Store.ParkNext(AcceptBy(asker.Agent.AgentId));
        try
        {
            var a = Task.Run(() => BudgetBursts.AcceptAsync(forum, asker, threadA, token, null, Ct), Ct);
            var parked = await forum.Store.WaitForParkedAsync(1, TimeSpan.FromSeconds(15));
            var readsBefore = forum.Store.ForwardReads;
            var b = Task.Run(() => BudgetBursts.AcceptAsync(forum, asker, threadB, token, null, Ct), Ct);
            await Task.Delay(700, Ct);
            var readsWhileParked = forum.Store.ForwardReads - readsBefore;
            var storedWhileParked = await BudgetOracle.AcceptsInWindowAsync(forum, asker.Agent.AgentId);
            var bDone = b.IsCompleted;

            forum.Store.Release();
            var sentA = await a;
            var sentB = await b;
            var after = await BudgetOracle.AcceptsInWindowAsync(forum, asker.Agent.AgentId);
            return (
                $"{(parked ? string.Empty : "A never reached its append; ")}seeded {seeded} of {limit - 1}; while A is parked B read the log {readsWhileParked} times, " +
                $"{(bDone ? "was answered" : "was still waiting")} and the table held {storedWhileParked}; after release the table holds {after}, A {BudgetBursts.Kind(sentA)}, B {BudgetBursts.Kind(sentB)}",
                after);
        }
        finally
        {
            forum.Store.Release();
        }
    }

    /// <summary>BRB17. The forced interleave, acceptances: with one acceptance left and A parked at its append, B reads nothing and records nothing until A is released, and then is told the budget.</summary>
    [Fact]
    public async Task R7_27_BRB17_AnAcceptanceParkedAtItsAppendHoldsTheBudgetForTheNext()
    {
        var limit = BudgetBursts.AcceptLimit("T0");
        var (_, acceptance) = PublishedBudgetGate.BudgetReasons();

        var (report, _) = await AcceptInterleaveAsync(passThrough: false);

        Said(
            $"seeded {limit - 1} of {limit - 1}; while A is parked B read the log 0 times, was still waiting and the table held {limit - 1}; after release the table holds {limit}, A 201, B 403 {acceptance}",
            report);
    }

    // ---- the burst grids (BRB18, BRB19) and the control that sees the bug (BRB20) -----------------------------------------------------------------------------

    /// <summary>One cell of BRB18: <paramref name="repetitions"/> bursts of <paramref name="width"/> posts by a fresh agent of <paramref name="tier"/>; the repetitions that did not hold the table at the limit.</summary>
    private async Task<(List<string> Failures, int Overshoots)> PostCellAsync(string tier, int width, int repetitions, bool passThrough = false)
    {
        forum.ResetSeams();
        forum.Gate.UsePassThrough = passThrough;
        var limit = BudgetBursts.PostLimit(tier);
        var slots = Math.Min(limit, width / 4);
        var failures = new List<string>();
        var overshoots = 0;
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);

        for (var rep = 1; rep <= repetitions; rep++)
        {
            var actor = await BudgetBursts.AgentOfAsync(forum, tier, board, Ct);
            await BudgetBursts.SeedPostsAsync(forum, actor.Agent, board, limit - slots, Ct);
            var seeded = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
            if (seeded != limit - slots)
            {
                failures.Add($"rep {rep}: the seed was not taken: {seeded} of {limit - slots}");
                continue;
            }

            var sent = await BudgetBursts.BurstAsync(forum, actor, BudgetBursts.Wires(actor, tier, board, parent.PostId, width, forum.Now), null, Ct);
            var stored = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
            if (stored > limit) overshoots++;
            if (stored != limit || !BudgetBursts.OnlyStoredOrBudget(sent))
                failures.Add($"rep {rep}: {stored} stored of limit {limit} ({BudgetBursts.Tally(sent)})");
        }

        return (failures, overshoots);
    }

    /// <summary>BRB18. The burst, posts, over the grid: tier x width, 20 repetitions each, the table holds exactly the limit and every answer is a stored post or the budget.</summary>
    [Theory]
    [InlineData("T0", 4)]
    [InlineData("T0", 16)]
    [InlineData("T0", 32)]
    [InlineData("T1", 4)]
    [InlineData("T1", 16)]
    [InlineData("T1", 32)]
    [InlineData("T2", 4)]
    [InlineData("T2", 16)]
    [InlineData("T2", 32)]
    public async Task R7_27_BRB18_ABurstOfPostsStoresExactlyTheLimit(string tier, int width)
    {
        var (failures, _) = await PostCellAsync(tier, width, repetitions: 20);

        var shown = failures.Count == 0 ? string.Empty : $"{failures.Count} of 20 repetitions: " + string.Join(" | ", failures.Take(3));
        Said($"{tier}, {width} simultaneous: exactly the limit stored in every one of 20 repetitions, every answer a stored post or the budget", $"{tier}, {width} simultaneous: " + (failures.Count == 0 ? "exactly the limit stored in every one of 20 repetitions, every answer a stored post or the budget" : shown));
    }

    private async Task<(List<string> Failures, int Reps)> AcceptCellAsync(string tier, int repetitions)
    {
        forum.ResetSeams();
        var limit = BudgetBursts.AcceptLimit(tier);
        var slots = tier == "T0" ? limit : 4;
        var board = Probe.Board();
        var asker = await BudgetBursts.AgentOfAsync(forum, tier, board, Ct);
        var answerer = ForumAgent.Create("https://agents.example/answerer-" + Guid.NewGuid().ToString("N")[..8], "answerer");
        var failures = new List<string>();

        for (var rep = 1; rep <= repetitions; rep++)
        {
            forum.Clock.Advance(TimeSpan.FromHours(25));
            await BudgetBursts.SeedAcceptsAsync(forum, asker.Agent, answerer, board, limit - slots, Ct);
            var seeded = await BudgetOracle.AcceptsInWindowAsync(forum, asker.Agent.AgentId);
            if (seeded != limit - slots)
            {
                failures.Add($"rep {rep}: the seed was not taken: {seeded} of {limit - slots}");
                continue;
            }

            var threads = new List<string>();
            for (var i = 0; i < 16; i++) threads.Add(await BudgetBursts.StoredThreadAsync(forum, asker.Agent, answerer, board, Ct));

            var token = await asker.Dpop.GetTokenAsync(forum.Client, BudgetBursts.TokenEndpoint, forum.Now, Ct);
            var sent = await Probe.TogetherAsync(threads.Select(t => (Func<Task<Sent>>)(() => BudgetBursts.AcceptAsync(forum, asker, t, token, null, Ct))));
            var recorded = await BudgetOracle.AcceptsInWindowAsync(forum, asker.Agent.AgentId);
            if (recorded != limit || !BudgetBursts.OnlyStoredOrBudget(sent))
                failures.Add($"rep {rep}: {recorded} recorded of limit {limit} ({BudgetBursts.Tally(sent)})");
        }

        return (failures, repetitions);
    }

    /// <summary>BRB19. The burst, acceptances, over the grid: tier x 16 threads, 20 repetitions each, the table holds exactly the limit and every answer is a recorded acceptance or the budget.</summary>
    [Theory]
    [InlineData("T0")]
    [InlineData("T1")]
    [InlineData("T2")]
    public async Task R7_27_BRB19_ABurstOfAcceptancesRecordsExactlyTheLimit(string tier)
    {
        var (failures, _) = await AcceptCellAsync(tier, repetitions: 20);

        const string Held = "exactly the limit recorded in every one of 20 repetitions, every answer a recorded acceptance or the budget";
        Said($"{tier}, 16 simultaneous accepts: {Held}", $"{tier}, 16 simultaneous accepts: " + (failures.Count == 0 ? Held : $"{failures.Count} of 20 repetitions: " + string.Join(" | ", failures.Take(3))));
    }

    /// <summary>
    /// BRB20. The control that sees the bug: with the gate replaced by the pass-through double, BRB16's interleave stores one more than the limit, BRB17's one
    /// more than its limit, and BRB18's T0 cell at width 16 overshoots in at least one of 20 repetitions. A burst probe whose oracle could not overshoot with the
    /// gate removed would carry no information; this asserts that it can, on every build.
    /// </summary>
    [Fact]
    public async Task R7_27_BRB20_WithTheGateRemovedTheBurstOvershoots()
    {
        var postLimit = BudgetBursts.PostLimit("T0");
        var acceptLimit = BudgetBursts.AcceptLimit("T0");

        var posts = await PostInterleaveAsync(passThrough: true);
        var accepts = await AcceptInterleaveAsync(passThrough: true);
        var (_, overshoots) = await PostCellAsync("T0", 16, repetitions: 20, passThrough: true);

        var actual = $"posts interleave stores {posts.StoredAfter}; acceptances interleave stores {accepts.StoredAfter}; the T0 width-16 cell overshoots in {(overshoots >= 1 ? "at least one" : "none")} of 20 repetitions";
        Said(
            $"posts interleave stores {postLimit + 1}; acceptances interleave stores {acceptLimit + 1}; the T0 width-16 cell overshoots in at least one of 20 repetitions",
            actual);
    }

    // ---- independence, the bound and the queue at the route (BRB21, BRB24 to BRB27) ---------------------------------------------------------------------------

    /// <summary>BRB21. Independence at the route: while A's append is parked, another agent's post is stored and answered 201.</summary>
    [Fact]
    public async Task R7_27_BRB21_AnotherAgentIsServedWhileOneIsParked()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var a = await AcceptedAnswerScene.EnrolAsync(forum, "parked", Ct);
        var other = await AcceptedAnswerScene.EnrolAsync(forum, "other", Ct);

        forum.Store.ParkNext(PostBy(a.Agent.AgentId));
        try
        {
            var parkedSend = Task.Run(() => Probe.SendAsync(forum, a, BudgetBursts.Wire(a.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct), Ct);
            var parked = await forum.Store.WaitForParkedAsync(1, TimeSpan.FromSeconds(15));
            var served = await Probe.SendAsync(forum, other, BudgetBursts.Wire(other.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
            var stored = await BudgetOracle.PostsInWindowAsync(forum, other.Agent.AgentId);
            forum.Store.Release();
            _ = await parkedSend;

            Said("A parked; the other agent answered 201 with 1 stored", $"{(parked ? "A parked" : "A never parked")}; the other agent answered {BudgetBursts.Kind(served)} with {stored} stored");
        }
        finally
        {
            forum.Store.Release();
        }
    }

    /// <summary>BRB24. An honest burst is served: 5 parallel comments by a T1 agent with budget to spare and 3 by a T0 agent with 3 left are every one 201.</summary>
    [Fact]
    public async Task R7_27_BRB24_AnHonestBurstIsServed()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var t1 = await BudgetBursts.AgentOfAsync(forum, "T1", board, Ct);
        var t0 = await BudgetBursts.AgentOfAsync(forum, "T0", board, Ct);

        var fromT1 = await BudgetBursts.BurstAsync(forum, t1, [.. Enumerable.Range(0, 5).Select(_ => BudgetBursts.Wire(t1.Agent, PostKind.Comment, board, parent.PostId, forum.Now))], null, Ct);
        var fromT0 = await BudgetBursts.BurstAsync(forum, t0, [.. Enumerable.Range(0, 3).Select(_ => BudgetBursts.Wire(t0.Agent, PostKind.Comment, board, parent.PostId, forum.Now))], null, Ct);

        Said("T1 5 x 201; T0 3 x 201", $"T1 {BudgetBursts.Tally(fromT1)}; T0 {BudgetBursts.Tally(fromT0)}");
    }

    /// <summary>
    /// BRB25. The bound at the route: A parked and B sent; B is answered 409 agent-in-flight no earlier than the bound and while A is still parked, the table is
    /// unchanged by B, and B sent again after A completes is judged by the budget. The host's gate is the real one built with the published bound.
    /// </summary>
    [Fact]
    public async Task R7_27_BRB25_ARequestThatCannotEnterIsRefusedAtTheBoundWhileTheHolderIsStillParked()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var token = await actor.Dpop.GetTokenAsync(forum.Client, BudgetBursts.TokenEndpoint, forum.Now, Ct);
        var wait = PublishedBudgetGate.Wait();

        forum.Store.ParkNext(PostBy(actor.Agent.AgentId));
        try
        {
            var a = Task.Run(() => Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), token, Ct), Ct);
            await forum.Store.WaitForParkedAsync(1, TimeSpan.FromSeconds(15));
            var before = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var b = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), token, Ct);
            var elapsed = timer.Elapsed;
            var aDone = a.IsCompleted;
            var after = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
            forum.Store.Release();
            _ = await a;

            var timely = elapsed >= wait - TimeSpan.FromMilliseconds(200) && elapsed <= wait + TimeSpan.FromSeconds(3);
            var expected = $"B {PublishedBudgetGate.InFlightStatus()} {PublishedBudgetGate.InFlightType()} after the {Ms(wait)} wait while A is still parked; the table unchanged by B";
            var held = BudgetBursts.Kind(b) == $"{PublishedBudgetGate.InFlightStatus()} {PublishedBudgetGate.InFlightType()}" && timely && !aDone && before == after;
            Said(
                expected,
                held ? expected : $"B {BudgetBursts.Kind(b)} after {Ms(elapsed)} ({(timely ? "at the wait" : "not at the wait")}) while A is {(aDone ? "no longer parked" : "still parked")}; the table {(before == after ? "unchanged" : $"changed from {before} to {after}")} by B");
        }
        finally
        {
            forum.Store.Release();
        }
    }

    /// <summary>BRB26. The queue bound at the route: A parked, W requests waiting, the (W+1)th is answered agent-in-flight at once while A is still parked.</summary>
    [Fact]
    public async Task R7_27_BRB26_AFullQueueIsRefusedAtOnce()
    {
        forum.ResetSeams();
        var waiters = PublishedBudgetGate.Waiters();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var token = await actor.Dpop.GetTokenAsync(forum.Client, BudgetBursts.TokenEndpoint, forum.Now, Ct);

        forum.Store.ParkNext(PostBy(actor.Agent.AgentId));
        try
        {
            var holder = Task.Run(() => Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), token, Ct), Ct);
            await forum.Store.WaitForParkedAsync(1, TimeSpan.FromSeconds(15));
            var waiting = Enumerable.Range(0, waiters)
                .Select(_ => Task.Run(() => Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), token, Ct), Ct))
                .ToArray();
            await Task.Delay(800, Ct);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var overflow = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), token, Ct);
            var elapsed = timer.Elapsed;
            var holderDone = holder.IsCompleted;
            forum.Store.Release();
            _ = await holder;
            _ = await Task.WhenAll(waiting);

            var expected = $"request {waiters + 2} (the holder and {waiters} waiting before it) answered {PublishedBudgetGate.InFlightType()} within 2 s while A is still parked";
            var refusedAtOnce = BudgetBursts.Kind(overflow).EndsWith(PublishedBudgetGate.InFlightType(), StringComparison.Ordinal) && elapsed < TimeSpan.FromSeconds(2) && !holderDone;
            Said(
                expected,
                refusedAtOnce ? expected : $"request {waiters + 2} (the holder and {waiters} waiting before it) answered {BudgetBursts.Kind(overflow)} after {Ms(elapsed)} while A is {(holderDone ? "no longer parked" : "still parked")}");
        }
        finally
        {
            forum.Store.Release();
        }
    }

    private async Task<(Sent Post, Sent Accept, long Stored, long Recorded, int Entries)> UnavailableAsync()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "unavailable", Ct);
        var answerer = ForumAgent.Create("https://agents.example/answerer-" + Guid.NewGuid().ToString("N")[..8], "answerer");
        var thread = await BudgetBursts.StoredThreadAsync(forum, actor.Agent, answerer, board, Ct);
        // The asker's own question is in the thread the scene stored for it: what the refused requests wrote is the difference from here.
        var storedBefore = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);

        forum.Gate.FailEntries = true;
        var post = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var accept = await BudgetBursts.AcceptAsync(forum, actor, thread, ct: Ct);
        var stored = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId) - storedBefore;
        var recorded = await BudgetOracle.AcceptsInWindowAsync(forum, actor.Agent.AgentId);
        var entries = forum.Gate.Entered.Count(e => e == actor.Agent.AgentId);
        forum.Gate.FailEntries = false;
        return (post, accept, stored, recorded, entries);
    }

    /// <summary>BRB27. An unreachable gate store, both paths: a post and an acceptance are 503 gate-unavailable with no detail, nothing stored or recorded; with the double restored the next request is judged on an unchanged count.</summary>
    [Fact]
    public async Task R7_27_BRB27_AnUnreachableGateIsServedAs503WithNothingWritten()
    {
        var (post, accept, stored, recorded, entries) = await UnavailableAsync();
        var type = PublishedBudgetGate.UnavailableType();
        var status = PublishedBudgetGate.UnavailableStatus();

        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "restored", Ct);
        var before = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
        forum.Gate.FailEntries = true;
        _ = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        forum.Gate.FailEntries = false;
        var next = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var after = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);

        Said(
            $"post {status} {type} with no detail, accept {status} {type} with no detail, 0 stored, 0 recorded, the gate reached by both; after it recovers 201 with {before + 1} stored",
            $"post {BudgetBursts.Kind(post)} {(post.Detail is null ? "with no detail" : "with a detail")}, accept {BudgetBursts.Kind(accept)} {(accept.Detail is null ? "with no detail" : "with a detail")}, " +
            $"{stored} stored, {recorded} recorded, the gate reached by {(entries >= 2 ? "both" : entries.ToString(CultureInfo.InvariantCulture))}; after it recovers {BudgetBursts.Kind(next)} with {after} stored");
    }

    /// <summary>BRB23. A mixed burst, one key: 8 posts and 8 acceptances together by one T1 agent with 3 post slots and 5 acceptance slots left record exactly 3 new posts and 5 new acceptances.</summary>
    [Fact]
    public async Task R7_27_BRB23_AMixedBurstIsHeldByOneKey()
    {
        forum.ResetSeams();
        var postLimit = BudgetBursts.PostLimit("T1");
        var acceptLimit = BudgetBursts.AcceptLimit("T1");
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var answerer = ForumAgent.Create("https://agents.example/answerer-" + Guid.NewGuid().ToString("N")[..8], "answerer");
        await BudgetBursts.SeedAcceptsAsync(forum, actor.Agent, answerer, board, acceptLimit - 5, Ct);
        var threads = new List<string>();
        for (var i = 0; i < 8; i++) threads.Add(await BudgetBursts.StoredThreadAsync(forum, actor.Agent, answerer, board, Ct));

        // The threads' questions are the agent's own posts and count against its posting budget: top it up to three slots left.
        var spent = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
        await BudgetBursts.SeedPostsAsync(forum, actor.Agent, board, (int)(postLimit - 3 - spent), Ct);
        var postsBefore = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
        var acceptsBefore = await BudgetOracle.AcceptsInWindowAsync(forum, actor.Agent.AgentId);

        var token = await actor.Dpop.GetTokenAsync(forum.Client, BudgetBursts.TokenEndpoint, forum.Now, Ct);
        var sends = Enumerable.Range(0, 8).Select(_ => BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now))
            .Select(w => (Func<Task<Sent>>)(() => Probe.SendAsync(forum, actor, w, token, Ct)))
            .Concat(threads.Select(t => (Func<Task<Sent>>)(() => BudgetBursts.AcceptAsync(forum, actor, t, token, null, Ct))));
        _ = await Probe.TogetherAsync(sends);

        var newPosts = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId) - postsBefore;
        var newAccepts = await BudgetOracle.AcceptsInWindowAsync(forum, actor.Agent.AgentId) - acceptsBefore;
        Said($"setup left {postLimit - postsBefore} post slots and {acceptLimit - acceptsBefore} acceptance slots: 3 new posts and 5 new acceptances", $"setup left {postLimit - postsBefore} post slots and {acceptLimit - acceptsBefore} acceptance slots: {newPosts} new posts and {newAccepts} new acceptances");
    }

    // ---- who enters, and when (BRB28 to BRB30, BRB38, BRB39) ----------------------------------------------------------------------------------------------

    /// <summary>BRB28. A refusal before the count never enters, and a valid request enters once: a malformed body, an unauthenticated request and a tier denial.</summary>
    [Fact]
    public async Task R7_27_BRB28_ARefusalBeforeTheCountNeverEntersAndAValidRequestEntersOnce()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "enters", Ct);
        var id = actor.Agent.AgentId;

        var valid = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var enteredByValid = forum.Gate.Entered.Count(e => e == id);
        var heldAfterValid = forum.Gate.HeldCount;

        var malformed = await Probe.SendAsync(forum, actor, System.Text.Encoding.UTF8.GetBytes("{\"not\":\"a submission\"}"), null, Ct);
        using var anonymousBody = new ByteArrayContent(BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now));
        using var anonymous = await forum.Client.PostAsync(new Uri(Probe.PostsUrl), anonymousBody, Ct);
        var enteredByRefusals = forum.Gate.Entered.Count(e => e == id) - enteredByValid;

        var denied = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Answer, board, parent.PostId, forum.Now), null, Ct);
        var enteredByDenial = forum.Gate.Entered.Count(e => e == id) - enteredByValid - enteredByRefusals;
        var heldAfterDenial = forum.Gate.HeldCount;

        Said(
            "valid 201 entered once and released; malformed 400 and anonymous 401 entered 0 times; a tier denial 403 entered once and released",
            $"valid {BudgetBursts.Kind(valid)} entered {Times(enteredByValid)} and {(heldAfterValid == 0 ? "released" : "still held")}; malformed {malformed.Status} and anonymous {(int)anonymous.StatusCode} entered {Times(enteredByRefusals)}; " +
            $"a tier denial {denied.Status} entered {Times(enteredByDenial)} and {(heldAfterDenial == 0 ? "released" : "still held")}".Replace("400 and anonymous 401", "400 and anonymous 401", StringComparison.Ordinal));
    }

    /// <summary>BRB29. The key is the authenticated subject: an envelope claiming another agent's id, sent with B's token, enters B's gate and no one else's.</summary>
    [Fact]
    public async Task R7_27_BRB29_TheKeyIsTheAuthenticatedSubject()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var a = await AcceptedAnswerScene.EnrolAsync(forum, "claimed", Ct);
        var b = await AcceptedAnswerScene.EnrolAsync(forum, "sender", Ct);

        var forged = b.Agent.Sign(PostKind.Comment, board, Probe.Unrelated(), null, parent.PostId, forum.Now, authorOverride: a.Agent.AgentId);
        var sent = await Probe.SendAsync(forum, b, forged, null, Ct);

        var expected = $"the gate entered by {b.Agent.AgentId} only";
        var enteredBy = forum.Gate.Entered.Distinct().ToArray();
        Said(
            expected,
            enteredBy.Length == 1 && enteredBy[0] == b.Agent.AgentId ? expected : $"the gate entered by {(forum.Gate.Entered.IsEmpty ? "no one" : string.Join(", ", forum.Gate.Entered.Distinct()))} (the forged envelope answered {BudgetBursts.Kind(sent)})");
    }

    /// <summary>BRB30. The key is the agent, not the token: two sessions of one agent, A on token 1 parked, B on token 2 stores nothing until release.</summary>
    [Fact]
    public async Task R7_27_BRB30_TwoSessionsOfOneAgentSerialize()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var token1 = await actor.Dpop.GetTokenAsync(forum.Client, BudgetBursts.TokenEndpoint, forum.Now, Ct);
        var token2 = await actor.Dpop.GetTokenAsync(forum.Client, BudgetBursts.TokenEndpoint, forum.Now, Ct);

        forum.Store.ParkNext(PostBy(actor.Agent.AgentId));
        try
        {
            var a = Task.Run(() => Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), token1, Ct), Ct);
            await forum.Store.WaitForParkedAsync(1, TimeSpan.FromSeconds(15));
            var b = Task.Run(() => Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), token2, Ct), Ct);
            await Task.Delay(700, Ct);
            var whileParked = await BudgetOracle.PostsInWindowAsync(forum, actor.Agent.AgentId);
            var bDone = b.IsCompleted;
            forum.Store.Release();
            _ = await a;
            _ = await b;

            Said("B on the second token waiting and 0 stored while A is parked", $"B on the second token {(bDone ? "answered" : "waiting")} and {whileParked} stored while A is parked");
        }
        finally
        {
            forum.Store.Release();
        }
    }

    /// <summary>BRB38. Flags are untouched: a flag does not enter the agent gate, and a burst of flags by one raiser keeps R7.22: one in flight, the rest refused at once.</summary>
    [Fact]
    public async Task R7_27_BRB38_AFlagDoesNotEnterTheAgentGate()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var raiser = await AcceptedAnswerScene.EnrolAsync(forum, "raiser", Ct);
        var author = ForumAgent.Create("https://agents.example/flagged-" + Guid.NewGuid().ToString("N")[..8], "flagged");
        var target = await BudgetBursts.StoreAsync(forum, author.SignQuestion(board, Probe.Unrelated(), "Flagged " + Probe.Unrelated(), forum.Now), Ct);
        var valid = await Probe.SendAsync(forum, raiser, BudgetBursts.Wire(raiser.Agent, PostKind.Question, board, target.PostId, forum.Now), null, Ct);
        var enteredBeforeFlag = forum.Gate.Entered.Count;

        var token = await raiser.Dpop.GetTokenAsync(forum.Client, BudgetBursts.TokenEndpoint, forum.Now, Ct);
        using var flag = await raiser.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{target.PostId}/flags", token,
            System.Text.Encoding.UTF8.GetBytes("{\"kind\":\"spam\",\"rationale\":\"a burst of flags\"}"), forum.Now, Ct, contentType: "application/json");

        Said(
            "the post (answered 201) entered the agent gate once; the flag (answered 201) entered it never",
            $"the post (answered {BudgetBursts.Kind(valid)}) entered the agent gate {Times(enteredBeforeFlag)}; the flag (answered {(int)flag.StatusCode}) entered it {(forum.Gate.Entered.Count - enteredBeforeFlag == 0 ? "never" : Times(forum.Gate.Entered.Count - enteredBeforeFlag))}");
    }

    /// <summary>BRB39. Reads are never gated: an anonymous read and an authenticated read are answered 200 while an agent's append is parked.</summary>
    [Fact]
    public async Task R7_27_BRB39_ReadsAreNeverGated()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "reader", Ct);

        forum.Store.ParkNext(PostBy(actor.Agent.AgentId));
        try
        {
            var a = Task.Run(() => Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct), Ct);
            var parked = await forum.Store.WaitForParkedAsync(1, TimeSpan.FromSeconds(15));
            using var read = await forum.Client.GetAsync(new Uri($"/v1/posts/{parent.PostId}", UriKind.Relative), Ct);
            using var listing = await forum.Client.GetAsync(new Uri($"/v1/boards/{board}/posts", UriKind.Relative), Ct);
            forum.Store.Release();
            _ = await a;

            Said("A parked; the post read 200; the board listing 200", $"{(parked ? "A parked" : "A never parked")}; the post read {(int)read.StatusCode}; the board listing {(int)listing.StatusCode}");
        }
        finally
        {
            forum.Store.Release();
        }
    }

    // ---- release (BRB31, BRB32, BRB40, BRB41) -----------------------------------------------------------------------------------------------------------------

    /// <summary>BRB31. Released after the act has committed: at each release the table already holds the post and its vector row, and the acceptance.</summary>
    [Fact]
    public async Task R7_27_BRB31_TheHoldIsReleasedAfterTheActHasCommitted()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "released", Ct);
        var answerer = ForumAgent.Create("https://agents.example/answerer-" + Guid.NewGuid().ToString("N")[..8], "answerer");
        var thread = await BudgetBursts.StoredThreadAsync(forum, actor.Agent, answerer, board, Ct);
        // The asker's own question is in the thread the scene stored for it, so the counts at each release are read against what the table held before the post.
        var baseline = await forum.SnapshotAsync(actor.Agent.AgentId);

        var post = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var accept = await BudgetBursts.AcceptAsync(forum, actor, thread, ct: Ct);
        var releases = forum.Gate.Releases.Where(r => r.Subject == actor.Agent.AgentId)
            .Select(r => new GateSnapshot(r.AtRelease.Posts - baseline.Posts, r.AtRelease.Accepts - baseline.Accepts, r.AtRelease.Vectors - baseline.Vectors)).ToArray();

        var expected = "2 releases: after the post, 1 post and 1 vector row and 0 acceptances; after the acceptance, 1 post and 1 vector row and 1 acceptance";
        string Describe(GateSnapshot s) => $"{s.Posts} post and {s.Vectors} vector row and {s.Accepts} acceptance{(s.Accepts == 1 ? string.Empty : "s")}";
        var actual = $"{releases.Length} releases (post {BudgetBursts.Kind(post)}, accept {BudgetBursts.Kind(accept)}): " + string.Join("; ", releases.Select(Describe));
        _ = Said(expected, releases.Length == 2 && releases[0] is { Posts: 1, Vectors: 1, Accepts: 0 } && releases[1] is { Posts: 1, Vectors: 1, Accepts: 1 } ? expected : actual);
    }

    /// <summary>
    /// BRB32. Released on every path: after a 201, a tier 403, a 409 already-recorded, a budget 403, a store that throws, an index that fails after the append and a
    /// client that cancels, the gate was entered for each, nothing is held, and the next request is served without waiting. (The 422 screening path of the plan is not
    /// driven: no screening pattern is published as a fixture the Api suite may send.)
    /// </summary>
    [Fact]
    public async Task R7_27_BRB32_TheHoldIsReleasedOnEveryPath()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var id = actor.Agent.AgentId;
        var notes = new List<string>();
        var neverEntered = new List<string>();

        async Task PathAsync(string name, Func<Task<Sent>> send)
        {
            var before = forum.Gate.Entered.Count(e => e == id);
            var sent = await send();
            if (forum.Gate.Entered.Count(e => e == id) == before) neverEntered.Add(name);
            notes.Add($"{name}: {BudgetBursts.Kind(sent)}, held {forum.Gate.HeldCount}");
        }

        Task<Sent> Comment() => Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);

        await PathAsync("created", Comment);
        await PathAsync("tier denial", () => Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Finding, board, parent.PostId, forum.Now), null, Ct));
        var wire = BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now);
        _ = await Probe.SendAsync(forum, actor, wire, null, Ct);
        await PathAsync("already recorded", () => Probe.SendAsync(forum, actor, wire, null, Ct));

        forum.Store.ThrowAt(PostBy(id));
        await PathAsync("store throws", Comment);
        forum.Store.Reset();

        forum.Index.FailUpserts = true;
        await PathAsync("index fails", Comment);
        forum.Index.FailUpserts = false;

        var spender = await AcceptedAnswerScene.EnrolAsync(forum, "spender", Ct);
        await BudgetBursts.SeedPostsAsync(forum, spender.Agent, board, BudgetBursts.PostLimit("T0"), Ct);
        var spenderEntries = forum.Gate.Entered.Count(e => e == spender.Agent.AgentId);
        var spent = await Probe.SendAsync(forum, spender, BudgetBursts.Wire(spender.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        if (forum.Gate.Entered.Count(e => e == spender.Agent.AgentId) == spenderEntries) neverEntered.Add("budget spent");
        notes.Add($"budget spent: {BudgetBursts.Kind(spent)}, held {forum.Gate.HeldCount}");

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        forum.Store.ParkNext(PostBy(id));
        var parked = Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, cancel.Token);
        await forum.Store.WaitForParkedAsync(1, TimeSpan.FromSeconds(10));
        await cancel.CancelAsync();
        _ = await parked;
        forum.Store.Reset();
        await Task.Delay(300, Ct);
        notes.Add($"cancelled: held {forum.Gate.HeldCount}");

        var timer = System.Diagnostics.Stopwatch.StartNew();
        _ = await Comment();
        var next = timer.Elapsed;

        var leaked = forum.Gate.HeldCount != 0 || notes.Any(n => !n.EndsWith("held 0", StringComparison.Ordinal));
        var expected = "every path entered the gate and left nothing held; the next request served within 2 s";
        Said(
            expected,
            neverEntered.Count == 0 && !leaked && next < TimeSpan.FromSeconds(2) ? expected : $"{(neverEntered.Count == 0 ? "every path entered the gate" : "never entered the gate: " + string.Join(", ", neverEntered))} and {(leaked ? "left a hold open" : "left nothing held")}; the next request served {(next < TimeSpan.FromSeconds(2) ? "within 2 s" : "in " + Ms(next))} [{string.Join("; ", notes)}]");
    }

    /// <summary>BRB40. A lone request is not delayed: an idle agent's requests enter without waiting.</summary>
    [Fact]
    public async Task R7_27_BRB40_ALoneRequestIsNotDelayed()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "lone", Ct);

        _ = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        _ = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var waits = forum.Gate.Waits.ToArray();

        Said("2 entries, none waiting 250 ms or more", $"{waits.Length} entries{(waits.Any(w => w >= TimeSpan.FromMilliseconds(250)) ? ", one waiting " + Ms(waits.Max()) : ", none waiting 250 ms or more")}");
    }

    /// <summary>BRB41. One hold spans every attempt: a store that loses the first append on the author's stream makes the submission attempt run twice, with the gate entered once and released after the second attempt's append.</summary>
    [Fact]
    public async Task R7_27_BRB41_OneHoldSpansEveryAttempt()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "attempts", Ct);

        var attemptsBefore = forum.Store.ConditionedAppends;
        forum.Store.LoseNextConditionedPostAppend();
        var sent = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var entries = forum.Gate.Entered.Count(e => e == actor.Agent.AgentId);
        var releases = forum.Gate.Releases.Where(r => r.Subject == actor.Agent.AgentId).Select(r => r.AtRelease).ToArray();

        Said(
            "answered 201 after two attempts; the gate entered once; released once, after the post was stored",
            $"answered {BudgetBursts.Kind(sent)} after {(forum.Store.ConditionedAppends - attemptsBefore == 2 ? "two" : (forum.Store.ConditionedAppends - attemptsBefore).ToString(CultureInfo.InvariantCulture))} attempts; the gate entered {Times(entries)}; released {Times(releases.Length)}{(releases.Length == 1 ? ", " + (releases[0].Posts == 1 ? "after the post was stored" : "before the post was stored") : string.Empty)}");
    }

    // ---- the combination with the revision gate (BRB33 to BRB36) and the retry loop (BRB37) -------------------------------------------------------------------

    /// <summary>BRB33. A revision pair: two simultaneous revisions of one post by its author: one 201, the other 409 already-revised, never revision-in-flight; one revision in the table.</summary>
    [Fact]
    public async Task R7_27_BRB33_ARevisionPairIsAlreadyRevisedNeverInFlight()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "reviser", Ct);
        var post = await Probe.AskAsync(forum, actor, board, Ct);

        var sent = await BudgetBursts.BurstAsync(forum, actor,
            [actor.Agent.SignRevision(board, "Corrected " + Probe.Unrelated(), post.PostId, post.Digest, forum.Now), actor.Agent.SignRevision(board, "Corrected " + Probe.Unrelated(), post.PostId, post.Digest, forum.Now)], null, Ct);
        var revisions = await Probe.RevisionsOfAsync(forum, post.Digest);
        var alreadyRevised = PublishedRevisionSlugs.AlreadyRevised();

        var kinds = sent.Select(s => s.Status == 201 ? "201" : $"{s.Status} {s.Type}").Order(StringComparer.Ordinal).ToArray();
        Said(
            $"201, 409 {alreadyRevised}; 1 revision in the table",
            $"{string.Join(", ", kinds)}; {revisions} revision{(revisions == 1 ? string.Empty : "s")} in the table");
    }

    /// <summary>BRB34. The order of gates: for a revision, the agent gate was entered before the revision gate.</summary>
    [Fact]
    public async Task R7_27_BRB34_TheAgentGateIsEnteredBeforeTheRevisionGate()
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "ordered", Ct);
        var post = await Probe.AskAsync(forum, actor, board, Ct);
        forum.Sequence.Clear();
        forum.Gate.Entered.Clear();

        var sent = await Probe.ReviseAsync(forum, actor, board, post, null, null, Ct);
        var order = forum.Sequence.Entries.Select(e => e.StartsWith("agent:", StringComparison.Ordinal) ? "agent gate" : "revision gate").ToArray();

        var inOrder = order.SequenceEqual(["agent gate", "revision gate"]);
        Said("agent gate, revision gate", inOrder ? "agent gate, revision gate" : $"{(order.Length == 0 ? "no gate" : string.Join(", ", order))} (the revision answered {BudgetBursts.Kind(sent)})");
    }

    /// <summary>A host over the fixture's database whose Maximum Pool Size is 4 and connection timeout 3 s: after R7.27's carve the agent pool and the gate pool are one connection each and the Forum's two.</summary>
    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> SmallPoolHost() =>
        forum.WithWebHostBuilder(builder => builder.UseSetting("ConnectionStrings:Events", forum.ConnectionString + ";Maximum Pool Size=4;Timeout=3"));

    /// <summary>BRB35. Nested holds on a one-connection gate pool: a revision, which holds the agent gate and then the revision gate, is 201 and not 503 revision-gate-unavailable.</summary>
    [Fact]
    public async Task R7_27_BRB35_NestedHoldsDoNotStarveASmallPool()
    {
        await using var host = SmallPoolHost();
        using var client = host.CreateClient();
        var board = Probe.Board();
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "nested", Ct);
        var post = await Probe.AskAsync(forum, actor, board, Ct);

        var sent = await Probe.SendViaAsync(forum, client, actor, actor.Agent.SignRevision(board, "Corrected " + Probe.Unrelated(), post.PostId, post.Digest, forum.Now), null, Ct);
        var gateEntries = AgentWriteGateForumFixture.GateOf(host.Services).Entered.Count;

        Said("the revision 201 having entered the agent gate", $"the revision {BudgetBursts.Kind(sent)} having entered the agent gate{(gateEntries == 1 ? string.Empty : " " + Times(gateEntries))}");
    }

    /// <summary>BRB36. The fleet twin of R7.22's fact, for posts: 16 distinct agents each post once on the small pool; no answer is 500, at least one is 201 and each is a 201 or a 503 gate-unavailable, the table holds exactly the 201s, and an anonymous read is answered 200 inside the connection timeout (3 s; a deadlock would stall it to the timeout).</summary>
    [Fact]
    public async Task R7_27_BRB36_ASmallPoolServesAFleetOfDistinctAgents()
    {
        await using var host = SmallPoolHost();
        using var client = host.CreateClient();
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var fleet = new List<SceneActor>();
        for (var i = 0; i < 16; i++) fleet.Add(await AcceptedAnswerScene.EnrolAsync(forum, "fleet", Ct));

#pragma warning disable CA2025 // The burst is awaited below, before the client and the host are disposed.
        var burst = Probe.TogetherAsync(fleet.Select(a => (Func<Task<Sent>>)(() =>
            Probe.SendViaAsync(forum, client, a, BudgetBursts.Wire(a.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct))));
#pragma warning restore CA2025
        await Task.Delay(300, Ct);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        int readStatus;
        using (var read = await client.GetAsync(new Uri($"/v1/posts/{parent.PostId}", UriKind.Relative), Ct)) readStatus = (int)read.StatusCode;
        var readTime = timer.Elapsed;
        var sent = await burst;

        var created = sent.Count(s => s.Status == 201);
        var stored = 0L;
        foreach (var a in fleet) stored += await BudgetOracle.PostsInWindowAsync(forum, a.Agent.AgentId);
        // A one-connection agent pool serializes the fleet, and a request that cannot get that connection inside the connection timeout is told gate-unavailable
        // (503, ruling 4, BRB14): the Forum saying it is overloaded, as R7.22's fleet fact allows its raiser gate to. Any other 5xx is a fault.
        var unavailable = sent.Count(s => s.Status == PublishedBudgetGate.UnavailableStatus() && s.Type == PublishedBudgetGate.UnavailableType());
        var faults = sent.Count(s => s.Status >= 500) - unavailable;
        // The floor: the pool serves the fleet, so at least one agent is created, and every request is a 201 or an honest 503 (nothing else, no hang).
        var accounted = created >= 1 && created + unavailable == fleet.Count;
        Said(
            "no 500; at least one 201 and every request a 201 or a gate-unavailable; the table holds exactly the 201s; a read answered 200 in under 3000 ms",
            $"{(faults == 0 ? "no 500" : faults + " x 500 or other 5xx (" + BudgetBursts.Tally(sent) + ")")}; {(accounted ? "at least one 201 and every request a 201 or a gate-unavailable" : "only " + created + " x 201 and " + unavailable + " x gate-unavailable of " + fleet.Count + " (" + BudgetBursts.Tally(sent) + ")")}; the table holds {(stored == created ? "exactly the 201s" : stored + " for " + created + " x 201")}; a read answered {readStatus} in {(readTime < TimeSpan.FromMilliseconds(3000) ? "under 3000 ms" : Ms(readTime))}");
    }

    /// <summary>BRB37. The retry loop is the second line: eight and sixteen simultaneous accepts alternating two answers of one thread record two events, no 500, and the store raised no concurrency conflict.</summary>
    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    public async Task R7_27_BRB37_ASerializedAcceptBurstRaisesNoConcurrencyConflict(int width)
    {
        forum.ResetSeams();
        var board = Probe.Board();
        var asker = await AcceptedAnswerScene.T1Async(forum, board, Ct);
        var answerer = ForumAgent.Create("https://agents.example/answerer-" + Guid.NewGuid().ToString("N")[..8], "answerer");
        var conflicts = 0;
        var faults = new List<string>();
        for (var rep = 1; rep <= 5; rep++)
        {
            forum.Clock.Advance(TimeSpan.FromHours(25));
            var question = await BudgetBursts.StoreAsync(forum, asker.Agent.SignQuestion(board, Probe.Unrelated(), "Thread " + Probe.Unrelated(), forum.Now), Ct);
            var answers = new[]
            {
                await BudgetBursts.StoreAsync(forum, answerer.SignAnswer(board, Probe.Unrelated(), question.PostId, forum.Now), Ct),
                await BudgetBursts.StoreAsync(forum, answerer.SignAnswer(board, Probe.Unrelated(), question.PostId, forum.Now), Ct),
            };

            var before = forum.Store.ConcurrencyConflicts;
            var token = await asker.Dpop.GetTokenAsync(forum.Client, BudgetBursts.TokenEndpoint, forum.Now, Ct);
            var sent = await Probe.TogetherAsync(Enumerable.Range(0, width).Select(i => (Func<Task<Sent>>)(() => BudgetBursts.AcceptAsync(forum, asker, answers[i % 2].PostId, token, null, Ct))));
            conflicts += forum.Store.ConcurrencyConflicts - before;
            faults.AddRange(sent.Where(s => s.Status >= 500).Select(s => $"rep {rep}: {s.Status} {s.Type}"));
        }

        Said(
            $"width {width}: no 5xx and no concurrency conflict in 5 repetitions",
            $"width {width}: {(faults.Count == 0 ? "no 5xx" : string.Join(", ", faults.Distinct()))} and {(conflicts == 0 ? "no concurrency conflict" : conflicts + " concurrency conflicts")} in 5 repetitions");
    }

    // ---- a committed post whose index fails (BRB60) --------------------------------------------------------------------------------------------------------

    /// <summary>
    /// BRB60. A committed post whose index fails releases the gate and counts: the table holds the post, nothing is held, the agent's next request is served without
    /// waiting, and a request beyond the limit is refused as the budget. The present answer (500, curia/retrieval/index-unavailable) is pinned and EXPIRES: D92's
    /// PR flips it to 201 with a member stating the index did not take the post.
    /// </summary>
    [Fact]
    public async Task R7_27_BRB60_ACommittedPostWhoseIndexFailsReleasesTheGateAndCounts()
    {
        forum.ResetSeams();
        var limit = BudgetBursts.PostLimit("T0");
        var board = Probe.Board();
        var parent = await BudgetBursts.StoredQuestionAsync(forum, board, Ct);
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "indexed", Ct);
        var id = actor.Agent.AgentId;

        forum.Index.FailUpserts = true;
        var failed = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        forum.Index.FailUpserts = false;
        var storedAfterFailure = await BudgetOracle.PostsInWindowAsync(forum, id);
        var held = forum.Gate.HeldCount;
        var entries = forum.Gate.Entered.Count(e => e == id);

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var next = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var nextTime = timer.Elapsed;
        var third = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var fourth = await Probe.SendAsync(forum, actor, BudgetBursts.Wire(actor.Agent, PostKind.Comment, board, parent.PostId, forum.Now), null, Ct);
        var (posting, _) = PublishedBudgetGate.BudgetReasons();

        var expected = $"the failed index answered 500 {PublishedStatusQuo.IndexUnavailable} (expiring: D92 flips it) with the post stored (1), the gate entered once and nothing held; the next request 201 inside 2 s; the request beyond the limit of {limit} 403 {posting}";
        var pinned = $"{failed.Status} {failed.Type}" == $"500 {PublishedStatusQuo.IndexUnavailable}" && storedAfterFailure == 1 && entries == 1 && held == 0
            && BudgetBursts.Kind(next).StartsWith("201", StringComparison.Ordinal) && nextTime < TimeSpan.FromSeconds(2) && BudgetBursts.Kind(fourth) == $"403 {posting}";
        Said(
            expected,
            pinned ? expected : $"the failed index answered {failed.Status} {failed.Type} (expiring: D92 flips it) with the post {(storedAfterFailure == 1 ? "stored (1)" : $"counted {storedAfterFailure}")}, the gate entered {Times(entries)} and {(held == 0 ? "nothing held" : held + " held")}; " +
            $"the next request {BudgetBursts.Kind(next)} in {Ms(nextTime)}; the request beyond the limit of {limit} {BudgetBursts.Kind(fourth)} (the third {BudgetBursts.Kind(third)})");
    }
}

/// <summary>Words the status quo pins that are not the errata's (they expire with the PR that flips them).</summary>
internal static class PublishedStatusQuo
{
    /// <summary>The problem type a failed vector index is answered with today (D92: a post committed and answered 500).</summary>
    internal const string IndexUnavailable = "curia/retrieval/index-unavailable";
}
