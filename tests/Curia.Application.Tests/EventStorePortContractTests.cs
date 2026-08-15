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

    private static DomainEvent NewEvent(string id, string type = "test.event") => new(
        Require(EventId.Create(id)),
        Require(EventType.Create(type)),
        Actor: null,
        Payload: new JsonValue.Object([]));

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
