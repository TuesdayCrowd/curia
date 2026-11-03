#pragma warning disable CA2007 // test code: there is no SynchronizationContext to resume on (as the other suites of this project do, per await, with ConfigureAwait(true))
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// R6.60 (errata G57), probes LCB1 to LCB7 and LCB9: the append guard's contract, which every <see cref="IEventStore"/> must satisfy. Abstract, as
/// <see cref="EventStorePortContractTests"/> is, so the in-memory adapter and the Postgres adapter run the same cases (LCB10 counts the subclasses). Every expected
/// description is built from what the errata publishes (<see cref="PublishedLateCommitWords"/>) or from the sequences the seeding appends returned, never from the
/// code under test. <b>On the seam both adapters accept the guard and ignore it</b>, which is the defect these probes name, so no red state is built from a stub
/// that is itself a defect (trap 67): a probe is red by its assertion.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public abstract class AppendGuardContractTests
{
    /// <summary>A fresh, empty store.</summary>
    protected abstract IEventStore CreateStore();

    /// <summary>A fresh, empty store whose lifecycle gate is <paramref name="gate"/>.</summary>
    protected abstract IEventStore CreateStore(ILifecycleAppendGate gate);

    /// <summary>Leaves a gap in the sequence numbers by an append that is rolled back after its INSERT; false when the adapter cannot (the in-memory log has no gaps).</summary>
    protected virtual Task<bool> LeaveGapAsync(IEventStore store, CancellationToken cancellationToken) => Task.FromResult(false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static AggregateId Agg(string value) => Require(AggregateId.Create(value));

    private static DomainEvent Ev(string id, string type = "test.event") => new(
        Require(EventId.Create(id)),
        Require(EventType.Create(type)),
        Actor: null,
        Payload: new JsonValue.Object([]));

    private static EventSequence Seq(long value) => Require(EventSequence.From(value));

    private static string Said(Result<IReadOnlyList<AppendedEvent>> result) =>
        result.Match(ok => $"Ok(seq {ok[0].Seq.Value})", e => $"Fail({e.Type} | {e.Title} | {e.Detail})");

    private static void Equal(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Range(IEnumerable<long> seqs)
    {
        var list = seqs.ToArray();
        return list.Length == 0 ? "[]" : list.Length <= 6 ? "[" + string.Join(", ", list) + "]" : $"[{list[0]}..{list[^1]}] ({list.Length})";
    }

    /// <summary>Appends <paramref name="count"/> events across <paramref name="aggregates"/> aggregates, one event per call, round robin; returns the sequences in append order.</summary>
    private static async Task<List<long>> SeedAsync(IEventStore store, int count, int aggregates, string stem = "g")
    {
        var versions = new long[aggregates];
        var seqs = new List<long>();
        for (var i = 0; i < count; i++)
        {
            var a = i % aggregates;
            var appended = Require(await store.AppendAsync(Agg($"{stem}{a}"), Require(AggregateVersion.From(versions[a])), [Ev($"{stem}-{a}-{versions[a]}-{Guid.NewGuid():N}")], Ct));
            versions[a]++;
            seqs.Add(appended[0].Seq.Value);
        }

        return seqs;
    }

    /// <summary>A guard that records every delta it is handed and answers <paramref name="verdict"/>.</summary>
    private static (AppendGuard Guard, List<long[]> Calls) Recording(EventSequence after, Func<GuardVerdict>? verdict = null)
    {
        var calls = new List<long[]>();
        return (new AppendGuard(after, delta =>
        {
            calls.Add([.. delta.Select(e => e.Seq.Value)]);
            return (verdict ?? (() => GuardVerdict.Proceed))();
        }), calls);
    }

    private static string CallsSaid(List<long[]> calls) =>
        calls.Count == 0 ? "the guard was not called" : "delta " + string.Join(" then ", calls.Select(c => Range(c)));

    private static async Task<long> HighestSeqAsync(IEventStore store)
    {
        var all = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: Ct));
        return all.Count == 0 ? 0 : all[^1].Seq.Value;
    }

    private static async Task<int> CountAsync(IEventStore store) =>
        Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: Ct)).Count;

    /// <summary>LCB1. The delta is exactly the events after the position: ascending, across aggregates, none at or before it; at the last sequence the guard is not called.</summary>
    [Fact]
    public async Task R6_60_LCB1_TheDeltaIsExactlyTheEventsAfterThePosition()
    {
        var store = CreateStore();
        var seqs = await SeedAsync(store, 25, 6);
        var cases = new (string Name, long After, long[] Expected)[]
        {
            ("the start", 0, [.. seqs]),
            ("the middle", seqs[12], [.. seqs.Skip(13)]),
            ("the last sequence", seqs[^1], []),
            ("one before the last", seqs[^2], [seqs[^1]]),
        };

        var said = new List<string>();
        var expected = new List<string>();
        foreach (var (name, after, want) in cases)
        {
            var (guard, calls) = Recording(Seq(after));
            Require(await store.AppendAsync(Agg("probe-" + name.Replace(' ', '-')), AggregateVersion.New, [Ev("lcb1-" + Guid.NewGuid().ToString("N"))], null, guard, Ct));
            expected.Add($"{name}: " + (want.Length == 0 ? "the guard was not called" : "delta " + Range(want)));
            said.Add($"{name}: " + CallsSaid(calls));
        }

        Equal(string.Join(" | ", expected), string.Join(" | ", said));
    }

    /// <summary>LCB2. <c>Refuse</c> writes nothing and is the caller's own error; the next append succeeds at once.</summary>
    [Fact]
    public async Task R6_60_LCB2_RefuseWritesNothingAndReturnsTheCallersError()
    {
        var store = CreateStore();
        await SeedAsync(store, 3, 2);
        var before = (Count: await CountAsync(store), Highest: await HighestSeqAsync(store));
        var refusal = new Error("probe/refused", "Refused by the probe", "detail=probe");
        var (guard, _) = Recording(EventSequence.Zero, () => GuardVerdict.Refuse(refusal));

        var result = await store.AppendAsync(Agg("lcb2-target"), AggregateVersion.New, [Ev("lcb2-1")], null, guard, Ct);

        Equal($"Fail({refusal.Type} | {refusal.Title} | {refusal.Detail})", Said(result));
        Equal($"events {before.Count}, highest {before.Highest}, target stream 0", $"events {await CountAsync(store)}, highest {await HighestSeqAsync(store)}, target stream {Require(await store.ReadByAggregateAsync(Agg("lcb2-target"), Ct)).Count}");
        var next = await store.AppendAsync(Agg("lcb2-target"), AggregateVersion.New, [Ev("lcb2-2")], Ct);
        Equal("an append at once succeeds", next.TryGetValue(out _, out _) ? "an append at once succeeds" : "the next append failed");
    }

    /// <summary>LCB3. Not called on an empty delta; called once, not once per event, otherwise.</summary>
    [Fact]
    public async Task R6_60_LCB3_TheGuardIsNotCalledOnAnEmptyDeltaAndIsCalledOnceOtherwise()
    {
        var store = CreateStore();
        await SeedAsync(store, 3, 1);
        var said = new List<string>();

        var (emptyGuard, emptyCalls) = Recording(Seq(await HighestSeqAsync(store)));
        Require(await store.AppendAsync(Agg("lcb3-empty"), AggregateVersion.New, [Ev("lcb3-empty")], null, emptyGuard, Ct));
        said.Add($"empty={emptyCalls.Count}");

        foreach (var size in new[] { 1, 20, 1000 })
        {
            var position = Seq(await HighestSeqAsync(store));
            var batch = Enumerable.Range(0, size).Select(i => Ev($"lcb3-{size}-{i}-{Guid.NewGuid():N}")).ToArray();
            Require(await store.AppendAsync(Agg($"lcb3-grow-{size}"), AggregateVersion.New, batch, Ct));
            var (guard, calls) = Recording(position);
            Require(await store.AppendAsync(Agg($"lcb3-probe-{size}"), AggregateVersion.New, [Ev($"lcb3-probe-{size}")], null, guard, Ct));
            said.Add($"{size}={calls.Count}");
        }

        Equal("empty=0, 1=1, 20=1, 1000=1", string.Join(", ", said));
    }

    /// <summary>LCB4. The existing refusals keep their precedence over the guard: a stale version, a stale condition, an inadmissible payload and a refused lifecycle append are answered as before and the guard is not called.</summary>
    [Fact]
    public async Task R6_60_LCB4_TheExistingConflictsKeepTheirPrecedence()
    {
        var store = CreateStore();
        await SeedAsync(store, 3, 1);
        var refusing = () => GuardVerdict.Refuse(new Error("probe/refused", "Refused by the probe"));
        var said = new List<string>();

        var (g1, c1) = Recording(EventSequence.Zero, refusing);
        var stale = await store.AppendAsync(Agg("g0"), AggregateVersion.New, [Ev("lcb4-1")], null, g1, Ct);
        said.Add("stale version: " + stale.Match(_ => "Ok", e => e.Type) + ", " + CallsSaid(c1));

        var (g2, c2) = Recording(EventSequence.Zero, refusing);
        var condition = new StreamCondition(Agg("g0"), Require(AggregateVersion.From(99)));
        var conditioned = await store.AppendAsync(Agg("lcb4-b"), AggregateVersion.New, [Ev("lcb4-2")], condition, g2, Ct);
        said.Add("stale condition: " + conditioned.Match(_ => "Ok", e => e.Type) + ", " + CallsSaid(c2));

        var (g3, c3) = Recording(EventSequence.Zero, refusing);
        var duplicateKey = new DomainEvent(Require(EventId.Create("lcb4-3")), Require(EventType.Create("test.event")), null, new JsonValue.Object([new("a", new JsonValue.Number(1)), new("a", new JsonValue.Number(2))]));
        var inadmissible = await store.AppendAsync(Agg("lcb4-c"), AggregateVersion.New, [duplicateKey], null, g3, Ct);
        said.Add("inadmissible payload: " + inadmissible.Match(_ => "Ok", e => e.Type) + ", " + CallsSaid(c3));

        var gated = CreateStore(new RefusingGate());
        Require(await gated.AppendAsync(Agg("lcb4-seed"), AggregateVersion.New, [Ev("lcb4-seed")], Ct));
        var (g4, c4) = Recording(EventSequence.Zero, refusing);
        var lifecycle = await gated.AppendAsync(Agg("urn:curia:agent:lcb4"), AggregateVersion.New, [Ev("lcb4-4", LifecycleAppendGuard.KeyRetiredType)], null, g4, Ct);
        said.Add("lifecycle append: " + lifecycle.Match(_ => "Ok", e => e.Type) + ", " + CallsSaid(c4));

        Equal(
            string.Join(" | ",
            [
                $"stale version: {DomainErrors.ConcurrencyConflictType}, the guard was not called",
                $"stale condition: {DomainErrors.ConditionConflictType}, the guard was not called",
                "inadmissible payload: curia/admit/duplicate-key, the guard was not called",
                $"lifecycle append: {DomainErrors.LifecycleAppendRefusedType}, the guard was not called",
            ]),
            string.Join(" | ", said));
    }

    private sealed class RefusingGate : ILifecycleAppendGate
    {
        public bool Enables(LifecycleKind kind) => false;
    }

    /// <summary>LCB5. Position semantics: an empty log, a position beyond the end, a position equal to the last sequence of a log with gaps, a position inside a gap.</summary>
    [Fact]
    public async Task R6_60_LCB5_PositionSemantics()
    {
        var said = new List<string>();
        var expected = new List<string>();

        var empty = CreateStore();
        var (g0, c0) = Recording(EventSequence.Zero);
        Require(await empty.AppendAsync(Agg("lcb5-empty"), AggregateVersion.New, [Ev("lcb5-0")], null, g0, Ct));
        said.Add("empty log: " + CallsSaid(c0));
        expected.Add("empty log: the guard was not called");

        var store = CreateStore();
        var seqs = await SeedAsync(store, 3, 1);
        var (g1, c1) = Recording(Seq(seqs[^1] + 100));
        Require(await store.AppendAsync(Agg("lcb5-beyond"), AggregateVersion.New, [Ev("lcb5-1")], null, g1, Ct));
        said.Add("beyond the end: " + CallsSaid(c1));
        expected.Add("beyond the end: the guard was not called");

        var gapped = CreateStore();
        var before = await SeedAsync(gapped, 3, 1);
        var madeGap = await LeaveGapAsync(gapped, Ct);
        var after = await SeedAsync(gapped, 2, 1, "h");
        var last = after[^1];
        var (g2, c2) = Recording(Seq(last));
        Require(await gapped.AppendAsync(Agg("lcb5-last"), AggregateVersion.New, [Ev("lcb5-2")], null, g2, Ct));
        said.Add("at the last sequence" + (madeGap ? " after a gap" : string.Empty) + ": " + CallsSaid(c2));
        expected.Add("at the last sequence" + (madeGap ? " after a gap" : string.Empty) + ": the guard was not called");

        var inside = madeGap ? before[^1] + 1 : before[^1];
        var (g3, c3) = Recording(Seq(inside));
        Require(await gapped.AppendAsync(Agg("lcb5-inside"), AggregateVersion.New, [Ev("lcb5-3")], null, g3, Ct));
        said.Add((madeGap ? "inside the gap: " : "inside the log: ") + CallsSaid(c3));
        expected.Add((madeGap ? "inside the gap: " : "inside the log: ") + "delta " + Range(after.Append(last + 1)));

        Equal(string.Join(" | ", expected), string.Join(" | ", said));
    }

    /// <summary>LCB6. A throwing function rolls back and propagates, unchanged, and is never read as <c>Proceed</c>.</summary>
    [Fact]
    public async Task R6_60_LCB6_AThrowingFunctionRollsBackAndPropagates()
    {
        var store = CreateStore();
        await SeedAsync(store, 3, 1);
        var before = await CountAsync(store);
        var guard = new AppendGuard(EventSequence.Zero, _ => throw new InvalidOperationException("the probe's function threw"));

        var thrown = await Record.ExceptionAsync(() => store.AppendAsync(Agg("lcb6-target"), AggregateVersion.New, [Ev("lcb6-1")], null, guard, Ct));

        Equal("InvalidOperationException: the probe's function threw", thrown is null ? "no exception; the append was read as Proceed" : $"{thrown.GetType().Name}: {thrown.Message}");
        Equal($"events {before}", $"events {await CountAsync(store)}");
        var next = await store.AppendAsync(Agg("lcb6-target"), AggregateVersion.New, [Ev("lcb6-2")], Ct);
        Equal("the next append succeeds", next.TryGetValue(out _, out _) ? "the next append succeeds" : "the next append failed");
    }

    /// <summary>LCB7. <c>Redecide</c> writes nothing and returns <c>curia/append/redecide</c> (the type read from the errata); the next append succeeds.</summary>
    [Fact]
    public async Task R6_60_LCB7_RedecideWritesNothingAndReturnsTheRedecideError()
    {
        var store = CreateStore();
        await SeedAsync(store, 3, 1);
        var before = await CountAsync(store);
        var (guard, _) = Recording(EventSequence.Zero, () => GuardVerdict.Redecide);

        var result = await store.AppendAsync(Agg("lcb7-target"), AggregateVersion.New, [Ev("lcb7-1")], null, guard, Ct);

        Equal($"Fail({PublishedLateCommitWords.RedecideType})", result.Match(ok => $"Ok(seq {ok[0].Seq.Value})", e => $"Fail({e.Type})"));
        Equal($"events {before}", $"events {await CountAsync(store)}");
        var next = await store.AppendAsync(Agg("lcb7-target"), AggregateVersion.New, [Ev("lcb7-2")], Ct);
        Equal("the next append succeeds", next.TryGetValue(out _, out _) ? "the next append succeeds" : "the next append failed");
    }

    /// <summary>
    /// LCB9. A delta past a page and the cap's two sides (ruling 22). A delta of exactly the cap is delivered complete and ascending, in one call, from the start and from the
    /// middle of a longer log; a delta one over the cap is not read and the function is not called: <c>curia/append/delta-too-large</c>, the log unchanged, the next append succeeds.
    /// </summary>
    [Fact]
    public async Task R6_60_LCB9_ADeltaPastAPageAndTheCapsTwoSides()
    {
        var cap = PublishedLateCommitWords.DeltaCap;
        var said = new List<string>();
        var expected = new List<string>();

        var exact = CreateStore();
        var exactSeqs = await SeedBulkAsync(exact, cap, "exact");
        var (g1, c1) = Recording(EventSequence.Zero);
        Require(await exact.AppendAsync(Agg("lcb9-exact"), AggregateVersion.New, [Ev("lcb9-1")], null, g1, Ct));
        expected.Add($"from the start, a delta of {cap}: one call, complete and ascending");
        said.Add($"from the start, a delta of {cap}: " + ShapeOf(c1, exactSeqs));

        var longer = CreateStore();
        var longSeqs = await SeedBulkAsync(longer, cap + 5000, "long");
        var (g2, c2) = Recording(Seq(longSeqs[4999]));
        Require(await longer.AppendAsync(Agg("lcb9-middle"), AggregateVersion.New, [Ev("lcb9-2")], null, g2, Ct));
        expected.Add($"from the middle, a delta of {cap}: one call, complete and ascending");
        said.Add($"from the middle, a delta of {cap}: " + ShapeOf(c2, longSeqs.Skip(5000).ToList()));

        var before = await CountAsync(longer);
        var (g3, c3) = Recording(Seq(longSeqs[4998]));
        var over = await longer.AppendAsync(Agg("lcb9-over"), AggregateVersion.New, [Ev("lcb9-3")], null, g3, Ct);
        var tooLarge = PublishedLateCommitWords.DeltaTooLarge();
        expected.Add($"one over the cap: Fail({tooLarge.Type}), the guard was not called, the log unchanged");
        said.Add($"one over the cap: {over.Match(ok => $"Ok(seq {ok[0].Seq.Value})", e => $"Fail({e.Type})")}, {CallsSaid(c3)}, the log {(await CountAsync(longer) == before ? "unchanged" : "grew")}");

        Equal(string.Join(" | ", expected), string.Join(" | ", said));
        var next = await longer.AppendAsync(Agg("lcb9-next"), AggregateVersion.New, [Ev("lcb9-4")], Ct);
        Equal("the next append succeeds", next.TryGetValue(out _, out _) ? "the next append succeeds" : "the next append failed");
    }

    private static string ShapeOf(List<long[]> calls, List<long> expected)
    {
        if (calls.Count == 0) return "the guard was not called";
        if (calls.Count != 1) return $"{calls.Count} calls";
        var got = calls[0];
        return got.SequenceEqual(expected) ? "one call, complete and ascending" : $"one call of {got.Length} events, expected {expected.Count}";
    }

    private static async Task<List<long>> SeedBulkAsync(IEventStore store, int count, string stem)
    {
        var seqs = new List<long>();
        var batches = 0;
        for (var done = 0; done < count; done += 2000)
        {
            var size = Math.Min(2000, count - done);
            var events = Enumerable.Range(0, size).Select(i => Ev($"{stem}-{batches}-{i}")).ToArray();
            var appended = Require(await store.AppendAsync(Agg($"{stem}-{batches}"), AggregateVersion.New, events, Ct));
            seqs.AddRange(appended.Select(e => e.Seq.Value));
            batches++;
        }

        return seqs;
    }
}
