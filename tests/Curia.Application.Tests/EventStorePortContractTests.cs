using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using CsCheck;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// The <see cref="IEventStore"/> port contract every implementation must satisfy, independent of
/// how (or whether) it persists anything durably. <see cref="InMemoryEventStoreTests"/> below is
/// the Stage 1 subclass. Stage 2's Postgres-backed adapter is meant to get its own
/// <c>Curia.Infrastructure.Tests</c> project that references this project and subclasses this
/// exact class rather than writing a parallel suite -- that reuse is the Stage 1 brief's whole
/// point ("Put it where the test projects can use it and Stage 2's Postgres adapter can be
/// checked against the *same* suite").
/// </summary>
public abstract class EventStorePortContractTests
{
    /// <summary>A fresh, empty store. Called once per test (and, for the property test below,
    /// once per generated case), so tests never see state another test left behind.</summary>
    protected abstract IEventStore CreateStore();

    private static DomainEvent NewEvent(string id, string type = "test.event", JsonValue? payload = null) => new(
        Require(EventId.Create(id)),
        Require(EventType.Create(type)),
        Actor: null,
        Payload: payload ?? new JsonValue.Object([]));

    private static AggregateId Agg(string value) => Require(AggregateId.Create(value));

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    [Fact]
    public async Task AppendThenReadRoundTrips()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-round-trip");
        var proposed = NewEvent("evt-1");

        var appended = Require(await store.AppendAsync(aggregateId, AggregateVersion.New, [proposed], ct));
        Assert.Single(appended);
        Assert.Equal(proposed.Id, appended[0].Event.Id);
        Assert.Equal(aggregateId, appended[0].AggregateId);

        var read = Require(await store.ReadByAggregateAsync(aggregateId, ct));
        Assert.Single(read);
        Assert.Equal(appended[0].Seq, read[0].Seq);
        Assert.Equal(proposed.Id, read[0].Event.Id);
    }

    [Fact]
    public async Task AppendingAnEmptyBatchFailsWithoutMutatingTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-empty");

        var result = await store.AppendAsync(aggregateId, AggregateVersion.New, [], ct);

        Assert.False(result.IsOk);
        var read = Require(await store.ReadByAggregateAsync(aggregateId, ct));
        Assert.Empty(read);
    }

    [Fact]
    public async Task AppendWithWrongExpectedVersionFailsWithoutMutatingTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-conflict");
        await store.AppendAsync(aggregateId, AggregateVersion.New, [NewEvent("first")], ct);

        // The aggregate now has one event, so New (0) is stale -- not an exception, a Result.
        var conflict = await store.AppendAsync(aggregateId, AggregateVersion.New, [NewEvent("second")], ct);

        Assert.False(conflict.IsOk);
        var error = conflict.Match(
            _ => throw new InvalidOperationException("expected a concurrency failure, got a success"),
            e => e);
        Assert.Equal("curia/domain/concurrency-conflict", error.Type);

        var stream = Require(await store.ReadByAggregateAsync(aggregateId, ct));
        Assert.Single(stream); // the conflicting append wrote nothing -- no silent overwrite
    }

    /// <summary>
    /// The payload-admissibility promise <see cref="IEventStore.AppendAsync"/> states (R6.42,
    /// errata E10), asserted here rather than in either adapter's own suite because it is a
    /// promise of the port: a payload with no RFC 8785 canonical form cannot be digested, cannot
    /// be signed, and does not re-parse to one unambiguous document, so it is not a fact a
    /// system of record can faithfully hold whichever adapter is underneath.
    ///
    /// This case exists because the adapters had drifted in exactly the direction a shared
    /// contract suite is meant to make impossible. <c>PostgresEventStore</c> refused a
    /// duplicate-membered payload -- <c>jsonb</c>'s input conversion resolves duplicate keys
    /// last-wins, so accepting one silently stored a different document than the caller
    /// appended -- while <c>InMemoryEventStore</c> accepted it and handed it back losslessly,
    /// because an in-process object graph physically can. Nothing failed, because this suite had
    /// no case for it. A fake more permissive than the real store is the worst shape the drift
    /// can take: code passes against the fake and loses data in production, which is what
    /// R11.4's in-memory adapter exists to prevent rather than create.
    ///
    /// The payload is built as a <see cref="JsonValue"/> tree rather than parsed from text, and
    /// that is the point, not a shortcut: every parse path already rejects duplicate member
    /// names, so a duplicate can only reach a store from a caller holding a tree nothing
    /// inspected -- and a <see cref="DomainEvent"/>'s payload is exactly such a tree.
    /// </summary>
    [Fact]
    public async Task AppendRefusesAPayloadWhoseRootObjectCarriesDuplicateMemberNames()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-duplicate-root");

        var payload = new JsonValue.Object(
        [
            new("dup", new JsonValue.String("FIRST")),
            new("dup", new JsonValue.String("SECOND")),
        ]);

        var result = await store.AppendAsync(
            aggregateId, AggregateVersion.New, [NewEvent("dup-root", payload: payload)], ct);

        AssertRefusedAsDuplicateKey(result);
        await AssertNothingWasWrittenAsync(store, aggregateId, ct);
    }

    /// <summary>
    /// Nested inside an array inside the payload, because an adapter that inspected only the
    /// payload's root object would pass the test above and still accept -- and, through
    /// <c>jsonb</c>, silently collapse -- this one. The rule is a property of every object in
    /// the tree, at any depth.
    /// </summary>
    [Fact]
    public async Task AppendRefusesADuplicateMemberNestedAnywhereInThePayload()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-duplicate-nested");

        var payload = new JsonValue.Object(
        [
            new("outer", new JsonValue.Array(
            [
                new JsonValue.Object(
                [
                    new("dup", new JsonValue.String("FIRST")),
                    new("dup", new JsonValue.String("SECOND")),
                ]),
            ])),
        ]);

        var result = await store.AppendAsync(
            aggregateId, AggregateVersion.New, [NewEvent("dup-nested", payload: payload)], ct);

        AssertRefusedAsDuplicateKey(result);
        await AssertNothingWasWrittenAsync(store, aggregateId, ct);
    }

    /// <summary>
    /// One inadmissible payload refuses the whole batch, including the events ahead of it that
    /// were fine on their own. This is the case that catches an adapter checking payloads inside
    /// its append loop instead of before it: such an adapter passes both tests above (their
    /// batches are one event long) and leaves a partially written batch behind here -- in an
    /// append-only log, where there is nothing to roll it back with.
    /// </summary>
    [Fact]
    public async Task ADuplicateLaterInABatchRefusesTheEventsAheadOfItToo()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var aggregateId = Agg("agg-duplicate-batch");

        var payload = new JsonValue.Object(
        [
            new("dup", new JsonValue.String("FIRST")),
            new("dup", new JsonValue.String("SECOND")),
        ]);

        var result = await store.AppendAsync(
            aggregateId,
            AggregateVersion.New,
            [NewEvent("batch-admissible"), NewEvent("batch-inadmissible", payload: payload)],
            ct);

        AssertRefusedAsDuplicateKey(result);
        await AssertNothingWasWrittenAsync(store, aggregateId, ct);
    }

    private static void AssertRefusedAsDuplicateKey(Result<IReadOnlyList<AppendedEvent>> result)
    {
        Assert.False(result.IsOk);

        // The slug names the condition, not the layer that noticed it (R6.42, R6.40): the same
        // curia/admit/duplicate-key ADMIT reports, because it is the same defect reached by a
        // caller ADMIT never ran for.
        Assert.Equal(
            "curia/admit/duplicate-key",
            result.Match(_ => "<the append succeeded>", e => e.Type));
    }

    /// <summary>
    /// Both read paths, because "this aggregate is empty" and "the log is empty" are different
    /// claims and an append-only store has to satisfy the second one too: an adapter could
    /// refuse to attach the events to the target aggregate's stream and still have appended
    /// them to the log every replay reads (R11.9), which is the copy that matters.
    /// </summary>
    private static async Task AssertNothingWasWrittenAsync(
        IEventStore store, AggregateId aggregateId, CancellationToken ct)
    {
        // ConfigureAwait(false) only because this helper is not itself a [Fact] and so is not
        // exempt from CA2007 the way every test method above is; xUnit v3's TestContext flows
        // through AsyncLocal, not a SynchronizationContext, so nothing here depends on resuming
        // on the captured context.
        Assert.Empty(Require(await store.ReadByAggregateAsync(aggregateId, ct).ConfigureAwait(false)));
        Assert.Empty(Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct).ConfigureAwait(false)));
    }

    [Fact]
    public async Task ReadByAggregateOnAnUntouchedAggregateIsEmptyNotAFailure()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var read = Require(await store.ReadByAggregateAsync(Agg("never-appended"), ct));
        Assert.Empty(read);
    }

    [Fact]
    public async Task ReadForwardFromZeroReplaysEverythingInAscendingSeqOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var a = Agg("agg-forward-a");
        var b = Agg("agg-forward-b");
        await store.AppendAsync(a, AggregateVersion.New, [NewEvent("a-1"), NewEvent("a-2")], ct);
        await store.AppendAsync(b, AggregateVersion.New, [NewEvent("b-1")], ct);

        var all = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct));

        Assert.Equal(3, all.Count);
        Assert.True(all[0].Seq < all[1].Seq);
        Assert.True(all[1].Seq < all[2].Seq);
    }

    [Fact]
    public async Task ReadForwardAfterASeqExcludesThatEventAndEverythingBeforeIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var a = Agg("agg-forward-exclusive");
        var appended = Require(await store.AppendAsync(
            a, AggregateVersion.New, [NewEvent("z-1"), NewEvent("z-2"), NewEvent("z-3")], ct));

        var tail = Require(await store.ReadForwardAsync(appended[0].Seq, cancellationToken: ct));

        Assert.Equal(2, tail.Count);
        Assert.DoesNotContain(tail, e => e.Event.Id == appended[0].Event.Id);
    }

    [Fact]
    public async Task ReadForwardHonoursMaxCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        var a = Agg("agg-page");
        await store.AppendAsync(a, AggregateVersion.New, [NewEvent("p-1"), NewEvent("p-2"), NewEvent("p-3")], ct);

        var page = Require(await store.ReadForwardAsync(EventSequence.Zero, maxCount: 2, cancellationToken: ct));

        Assert.Equal(2, page.Count);
    }

    /// <summary>
    /// Stage 1's headline property: across arbitrarily interleaved appends to several
    /// aggregates, every <c>seq</c> the store ever hands out is strictly greater than every
    /// <c>seq</c> handed out before it -- Appendix D's <c>seq</c> is one global IDENTITY column,
    /// not one counter per aggregate, so interleaving must not produce two streams whose
    /// sequence numbers interleave out of call order.
    /// </summary>
    [Fact]
    public void SeqIsStrictlyMonotonicAcrossAggregatesUnderRandomAppendSequences() =>
        Gen.Select(Gen.Int[0, 4], Gen.Int[1, 3])
            .List[1, 40]
            .Sample(script =>
            {
                var store = CreateStore();
                var versions = new Dictionary<int, AggregateVersion>();
                var lastSeq = 0L;

                foreach (var (aggregateIndex, count) in script)
                {
                    var aggregateId = Agg($"agg-{aggregateIndex}");
                    var expected = versions.GetValueOrDefault(aggregateIndex, AggregateVersion.New);
                    var batch = Enumerable.Range(0, count)
                        .Select(i => NewEvent($"{aggregateIndex}-{Guid.NewGuid():N}-{i}"))
                        .ToArray();

                    var appended = store.AppendAsync(aggregateId, expected, batch)
                        .GetAwaiter().GetResult()
                        .Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

                    foreach (var e in appended)
                    {
                        if (e.Seq.Value <= lastSeq)
                            return false;
                        lastSeq = e.Seq.Value;
                    }

                    versions[aggregateIndex] = Require(AggregateVersion.From(expected.Value + count));
                }

                return true;
            }, iter: 200);
}
