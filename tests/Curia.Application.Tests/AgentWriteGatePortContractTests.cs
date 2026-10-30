using System.Diagnostics.CodeAnalysis;
using Curia.Application.Budget;
using Curia.Application.Ports;
using Curia.Application.Tests.InMemory;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// What every <see cref="IAgentWriteGate"/> promises (R7.27; errata G48), run against the in-memory adapter here and against Postgres in
/// <c>Curia.Infrastructure.Tests</c> (R11.4). Probes BRB1 to BRB10 and BRB65, the shape of <see cref="RevisionGatePortContractTests"/>.
///
/// <para>The wait and the queue a fact uses are its own small values passed to the gate (the gate takes them at construction); the errata's published
/// numbers are pinned to the code by BRB48 and no fact here reads a production constant (BRB49). The problem types are the errata's, read at test time.
/// A fact never trusts that a task is pending because it has not been asked: <see cref="IsPendingAsync"/> waits a real 150 ms for it to complete.</para>
///
/// <para>The clock is the subclass's: the in-memory run uses a clock whose timers fire when advanced, the Postgres run the real clock, and
/// <see cref="AdvanceAsync"/> is the one place that difference lives.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class AgentWriteGatePortContractTests
{
    /// <summary>The wait every fact's gate is built with.</summary>
    protected static readonly TimeSpan Wait = TimeSpan.FromSeconds(1);

    /// <summary>The queue every fact's gate is built with.</summary>
    protected const int Waiters = 4;

    private TimeProvider? _clock;

    protected TimeProvider Clock => _clock ??= CreateClock();

    protected abstract TimeProvider CreateClock();

    /// <summary>Moves time forward by <paramref name="by"/>: advances a fake clock, or lets real time pass.</summary>
    protected abstract Task AdvanceAsync(TimeSpan by);

    /// <summary>Extra time a real clock needs past a bound before a refusal is certain; zero for a fake clock.</summary>
    protected virtual TimeSpan Slack => TimeSpan.Zero;

    /// <summary>A gate on <see cref="Clock"/> whose holds no other test's gate shares.</summary>
    protected abstract IAgentWriteGate CreateGate(TimeSpan wait, int waiters);

    /// <summary>The agents the gate's queue tracks now.</summary>
    protected abstract int TrackedKeysOf(IAgentWriteGate gate);

    /// <summary>A gate whose store cannot be reached, and the action that makes it reachable again.</summary>
    protected abstract (IAgentWriteGate Gate, Action Recover) CreateGateWithUnreachableStore();

    /// <summary>How many other agents' full queues BRB65 builds: enough to pass any process-wide cap the gate might (wrongly) hold.</summary>
    protected virtual int OtherAgents => 60;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected static AgentId Agent(string name) =>
        AgentId.Parse($"https://agents.example/{name}-{Guid.NewGuid():N}").Match(v => v, e => throw new InvalidOperationException(e.Type));

    private static AgentId Agent(string name, string suffix) =>
        AgentId.Parse($"https://agents.example/{name}-{suffix}").Match(v => v, e => throw new InvalidOperationException(e.Type));

    protected static IAsyncDisposable Require(Result<IAsyncDisposable> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    /// <summary>Asserts two descriptions are equal and says both in full when they are not: a red text is what these probes leave behind.</summary>
    protected static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>True when <paramref name="task"/> has not completed after a real 150 ms.</summary>
    protected static async Task<bool> IsPendingAsync(Task task) =>
        await Task.WhenAny(task, Task.Delay(TimeSpan.FromMilliseconds(150), Ct)).ConfigureAwait(false) != task;

    /// <summary>
    /// The entry's outcome in a word: <c>entered</c> (the hold granted is released at once), <c>cancelled</c>, <c>pending</c> (not done in
    /// <paramref name="within"/>) or the refusal's type.
    /// </summary>
    private static async Task<string> OutcomeOfAsync(Task<Result<IAsyncDisposable>> entry, TimeSpan within)
    {
        var done = await Task.WhenAny(entry, Task.Delay(within, Ct)).ConfigureAwait(false);
        if (done != entry) return "pending";
        if (entry.IsCanceled) return "cancelled";
        var result = await entry.ConfigureAwait(false);
        if (!result.TryGetValue(out var hold, out var error)) return error!.Type;
        await hold!.DisposeAsync().ConfigureAwait(false);
        return "entered";
    }

    /// <summary>BRB1. A second entry for one agent does not complete until the first hold is released, then completes.</summary>
    [Fact]
    public async Task R7_27_BRB1_ASecondEntryForOneAgentWaitsForTheFirstHold()
    {
        var gate = CreateGate(Wait, Waiters);
        var x = Agent("x");

        var first = Require(await gate.EnterAsync(x, Ct));
        var second = gate.EnterAsync(x, Ct);
        var whileHeld = await IsPendingAsync(second) ? "pending" : "completed";
        await first.DisposeAsync();
        var afterRelease = await OutcomeOfAsync(second, TimeSpan.FromSeconds(5));

        Said("second entry pending while held; entered after release", $"second entry {whileHeld} while held; {afterRelease} after release");
    }

    /// <summary>BRB2. Another agent is not held up, nor an agent whose identifier differs by case.</summary>
    [Fact]
    public async Task R7_27_BRB2_AnotherAgentIsNotHeldUp()
    {
        var gate = CreateGate(Wait, Waiters);
        var suffix = Guid.NewGuid().ToString("N");
        var x = Agent("agent-x", suffix);
        var caseVariant = Agent("Agent-X", suffix);
        var y = Agent("y");

        await using var held = Require(await gate.EnterAsync(x, Ct));
        var other = await OutcomeOfAsync(gate.EnterAsync(y, Ct), TimeSpan.FromSeconds(2));
        var variant = await OutcomeOfAsync(gate.EnterAsync(caseVariant, Ct), TimeSpan.FromSeconds(2));

        Said("entered; entered", $"{other}; {variant}");
    }

    /// <summary>BRB3. A property over k = 2 to W: k waiters queued behind a holder in a known order enter in that order, one at a time.</summary>
    [Fact]
    public async Task R7_27_BRB3_WaitersEnterInTheOrderTheyArrived()
    {
        for (var k = 2; k <= Waiters; k++)
        {
            var gate = CreateGate(Wait * 10, Waiters);
            var x = Agent("fifo");
            var entered = new List<int>();
            var order = new object();

            var holder = Require(await gate.EnterAsync(x, Ct));
            var waiting = new List<Task<Result<IAsyncDisposable>>>();
            for (var i = 0; i < k; i++)
            {
                var index = i;
                var entry = gate.EnterAsync(x, Ct);
                waiting.Add(entry);
                _ = entry.ContinueWith(_ => { lock (order) entered.Add(index); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                await Task.Delay(30, Ct);
            }

            string beforeRelease;
            lock (order) beforeRelease = string.Join(",", entered);

            var sequence = new List<int>();
            await holder.DisposeAsync();
            for (var i = 0; i < k; i++)
            {
                var outcome = await OutcomeOfAsync(waiting[i], TimeSpan.FromSeconds(5));
                if (outcome == "entered") sequence.Add(i);
            }

            Said(
                $"k={k}: none entered before the holder released; entered in arrival order {string.Join(",", Enumerable.Range(0, k))}",
                $"k={k}: {(beforeRelease.Length == 0 ? "none" : "waiters " + beforeRelease)} entered before the holder released; entered in order {string.Join(",", sequence)}");
        }
    }

    /// <summary>BRB4. The wait is bounded, on a clock whose timers fire: still pending a tick before the bound, refused at it, the holder untouched, no residue.</summary>
    [Fact]
    public async Task R7_27_BRB4_ThePublishedWaitIsABoundNotAHang()
    {
        var gate = CreateGate(Wait, Waiters);
        var x = Agent("bound");

        var holder = Require(await gate.EnterAsync(x, Ct));
        var waiter = gate.EnterAsync(x, Ct);
        await AdvanceAsync(Wait / 2);
        var beforeBound = await OutcomeOfAsync(waiter, TimeSpan.FromMilliseconds(150));
        await AdvanceAsync((Wait / 2) + Slack);
        var atBound = await OutcomeOfAsync(waiter, TimeSpan.FromSeconds(5));

        await holder.DisposeAsync();
        var next = await OutcomeOfAsync(gate.EnterAsync(x, Ct), TimeSpan.FromSeconds(2));

        Said(
            $"pending before the bound; {PublishedBudgetGate.InFlightType()} at the bound; the next entry after release: entered",
            $"{beforeBound} before the bound; {atBound} at the bound; the next entry after release: {next}");
    }

    /// <summary>BRB5. The queue is bounded, and a waiter that timed out frees its slot.</summary>
    [Fact]
    public async Task R7_27_BRB5_TheQueueIsBoundedAndATimedOutWaiterFreesItsSlot()
    {
        var gate = CreateGate(Wait, Waiters);
        var x = Agent("queue");

        var holder = Require(await gate.EnterAsync(x, Ct));
        var first = gate.EnterAsync(x, Ct);
        await AdvanceAsync(Wait / 2);
        var rest = new List<Task<Result<IAsyncDisposable>>>();
        for (var i = 1; i < Waiters; i++) rest.Add(gate.EnterAsync(x, Ct));

        var overflow = await OutcomeOfAsync(gate.EnterAsync(x, Ct), TimeSpan.FromSeconds(1));
        await AdvanceAsync((Wait / 2) + Slack);
        var firstOutcome = await OutcomeOfAsync(first, TimeSpan.FromSeconds(5));
        var replacement = gate.EnterAsync(x, Ct);
        var replacementOutcome = await OutcomeOfAsync(replacement, TimeSpan.FromMilliseconds(300));

        await holder.DisposeAsync();
        Said(
            $"waiter {Waiters + 1} refused {PublishedBudgetGate.InFlightType()} at once; the first waiter timed out {PublishedBudgetGate.InFlightType()}; a new waiter pending",
            $"waiter {Waiters + 1} {overflow}; the first waiter {firstOutcome}; a new waiter {replacementOutcome}");
    }

    /// <summary>BRB6. A cancelled waiter leaves, frees its slot, and is never granted the permit.</summary>
    [Fact]
    public async Task R7_27_BRB6_ACancelledWaiterLeavesAndIsNeverGrantedThePermit()
    {
        var gate = CreateGate(Wait * 10, Waiters);
        var x = Agent("cancel");

        var holder = Require(await gate.EnterAsync(x, Ct));
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var leaver = gate.EnterAsync(x, cancel.Token);
        await Task.Delay(30, Ct);
        var next = gate.EnterAsync(x, Ct);
        var beforeCancel = await IsPendingAsync(leaver) ? "pending" : "completed";

        await cancel.CancelAsync();
        var leaverOutcome = await OutcomeOfAsync(leaver, TimeSpan.FromSeconds(2));
        await holder.DisposeAsync();
        var nextOutcome = await OutcomeOfAsync(next, TimeSpan.FromSeconds(5));
        var afterwards = await OutcomeOfAsync(gate.EnterAsync(x, Ct), TimeSpan.FromSeconds(2));

        Said(
            "the leaver pending before the cancel, cancelled after; the next in line entered; a later entry entered",
            $"the leaver {beforeCancel} before the cancel, {leaverOutcome} after; the next in line {nextOutcome}; a later entry {afterwards}");
    }

    /// <summary>BRB7. Released on every path: a second dispose of an earlier hold does not release a later holder, and a body that throws releases.</summary>
    [Fact]
    public async Task R7_27_BRB7_AHoldIsReleasedOnEveryPathAndOnlyOnce()
    {
        var gate = CreateGate(Wait * 10, Waiters);
        var x = Agent("release");

        var a = Require(await gate.EnterAsync(x, Ct));
        await a.DisposeAsync();
        var b = Require(await gate.EnterAsync(x, Ct));
        await a.DisposeAsync();
        var third = gate.EnterAsync(x, Ct);
        var whileBHolds = await IsPendingAsync(third) ? "pending" : "completed";
        await b.DisposeAsync();
        var afterB = await OutcomeOfAsync(third, TimeSpan.FromSeconds(5));

        var thrown = "no exception";
        try
        {
            await using (Require(await gate.EnterAsync(x, Ct))) throw new InvalidOperationException("the body failed");
        }
        catch (InvalidOperationException)
        {
            thrown = "exception";
        }

        var afterThrow = await OutcomeOfAsync(gate.EnterAsync(x, Ct), TimeSpan.FromSeconds(2));

        Said(
            "third entry pending while B holds; entered after B; body exception; an entry after the exception entered",
            $"third entry {whileBHolds} while B holds; {afterB} after B; body {thrown}; an entry after the exception {afterThrow}");
    }

    /// <summary>BRB8. Idle entries are dropped: after many agents enter and release, or time out, the queue tracks no key; and while one holds, it tracks that one.</summary>
    [Fact]
    public async Task R7_27_BRB8_IdleEntriesAreDropped()
    {
        var gate = CreateGate(Wait, Waiters);
        var probe = Agent("tracked");
        int whileHeld;
        await using (Require(await gate.EnterAsync(probe, Ct))) whileHeld = TrackedKeysOf(gate);

        for (var i = 0; i < 64; i++)
            await using (Require(await gate.EnterAsync(Agent("fleet"), Ct))) { }
        var afterReleases = TrackedKeysOf(gate);

        var timedOut = new List<Task<Result<IAsyncDisposable>>>();
        var holders = new List<IAsyncDisposable>();
        for (var i = 0; i < 64; i++)
        {
            var agent = Agent("timeout");
            holders.Add(Require(await gate.EnterAsync(agent, Ct)));
            timedOut.Add(gate.EnterAsync(agent, Ct));
        }

        await AdvanceAsync(Wait + Slack);
        foreach (var entry in timedOut) _ = await OutcomeOfAsync(entry, TimeSpan.FromSeconds(5));
        foreach (var holder in holders) await holder.DisposeAsync();
        var afterTimeouts = TrackedKeysOf(gate);

        Said("1 tracked while held; 0 after 64 releases; 0 after 64 timeouts", $"{whileHeld} tracked while held; {afterReleases} after 64 releases; {afterTimeouts} after 64 timeouts");
    }

    /// <summary>BRB9. A lone entry is never delayed: 200 enter-and-release cycles for one agent, the clock never advanced, all enter at once.</summary>
    [Fact]
    public async Task R7_27_BRB9_ALoneEntryIsNeverDelayed()
    {
        var gate = CreateGate(Wait, Waiters);
        var x = Agent("lone");

        var outcomes = new List<string>();
        for (var i = 0; i < 200; i++)
        {
            var entry = gate.EnterAsync(x, Ct);
            outcomes.Add(await OutcomeOfAsync(entry, TimeSpan.FromSeconds(2)));
        }

        var others = outcomes.Where(o => o != "entered").Distinct().ToArray();
        Said("200 of 200 entered", $"{outcomes.Count(o => o == "entered")} of 200 entered{(others.Length == 0 ? string.Empty : " (" + string.Join(",", others) + ")")}");
    }

    /// <summary>BRB10. An unreachable store is its own refusal and holds nothing: once it recovers the next entry is served.</summary>
    [Fact]
    public async Task R7_27_BRB10_AnUnreachableStoreIsItsOwnRefusalAndHoldsNothing()
    {
        var (gate, recover) = CreateGateWithUnreachableStore();
        var x = Agent("store");

        var during = await OutcomeOfAsync(gate.EnterAsync(x, Ct), TimeSpan.FromSeconds(10));
        recover();
        var after = await OutcomeOfAsync(gate.EnterAsync(x, Ct), TimeSpan.FromSeconds(10));
        var afterAgain = await OutcomeOfAsync(gate.EnterAsync(x, Ct), TimeSpan.FromSeconds(10));

        Said(
            $"{PublishedBudgetGate.UnavailableType()} while unreachable; entered after it recovers; entered again once released",
            $"{during} while unreachable; {after} after it recovers; {afterAgain} again once released");
    }

    /// <summary>
    /// BRB65. The queue bound is per agent, not per process: with agent X's queue full and many other agents' queues full, a new agent's second entry waits
    /// and is served, never refused at once, and only X's (W+1)th is refused.
    /// </summary>
    [Fact]
    public async Task R7_27_BRB65_TheQueueBoundIsPerAgentNotPerProcess()
    {
        var gate = CreateGate(Wait * 10, Waiters);
        var held = new List<IAsyncDisposable>();
        var waiting = new List<Task<Result<IAsyncDisposable>>>();

        var x = Agent("full");
        held.Add(Require(await gate.EnterAsync(x, Ct)));
        for (var i = 0; i < Waiters; i++) waiting.Add(gate.EnterAsync(x, Ct));
        for (var a = 0; a < OtherAgents; a++)
        {
            var other = Agent("crowd");
            held.Add(Require(await gate.EnterAsync(other, Ct)));
            for (var i = 0; i < Waiters; i++) waiting.Add(gate.EnterAsync(other, Ct));
        }

        var z = Agent("newcomer");
        held.Add(Require(await gate.EnterAsync(z, Ct)));
        var zWaits = gate.EnterAsync(z, Ct);
        var zOutcome = await OutcomeOfAsync(zWaits, TimeSpan.FromMilliseconds(300));
        var xOverflow = await OutcomeOfAsync(gate.EnterAsync(x, Ct), TimeSpan.FromSeconds(1));

        await held[^1].DisposeAsync();
        var zServed = await OutcomeOfAsync(zWaits, TimeSpan.FromSeconds(5));

        Said(
            $"the newcomer's second entry pending, then entered; X's {Waiters + 1}th refused {PublishedBudgetGate.InFlightType()}",
            $"the newcomer's second entry {zOutcome}, then {zServed}; X's {Waiters + 1}th {xOverflow}");
    }
}

[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "xUnit discovery needs the concrete class public.")]
public sealed class InMemoryAgentWriteGateContractTests : AgentWriteGatePortContractTests
{
    private InMemoryAgentWriteGate? _unreachable;

    protected override TimeProvider CreateClock() => new TimerManualTimeProvider(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

    protected override Task AdvanceAsync(TimeSpan by)
    {
        ((TimerManualTimeProvider)Clock).Advance(by);
        return Task.CompletedTask;
    }

    protected override IAgentWriteGate CreateGate(TimeSpan wait, int waiters) => new InMemoryAgentWriteGate(Clock, wait, waiters);

    protected override int TrackedKeysOf(IAgentWriteGate gate) => ((InMemoryAgentWriteGate)gate).TrackedKeys;

    protected override (IAgentWriteGate Gate, Action Recover) CreateGateWithUnreachableStore()
    {
        _unreachable = new InMemoryAgentWriteGate(Clock, Wait, Waiters) { StoreUnreachable = true };
        return (_unreachable, () => _unreachable.StoreUnreachable = false);
    }
}
