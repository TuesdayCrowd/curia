using Curia.Application.Projections;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Application.Tests.Projections;

/// <summary>
/// Unit-speed coverage for <see cref="AggregateSummaryProjector"/>'s pure fold and paged-replay
/// logic, run against <see cref="InMemoryEventStore"/> rather than Postgres. Every
/// <see cref="AppendedEvent"/> used below comes from a real append through that store, never
/// fabricated directly -- <c>Curia.Architecture.Tests.EventStoreWriteSurfaceTests</c> (CS-15)
/// scans this assembly's compiled IL for exactly that, and <c>InMemoryEventStore</c> is the only
/// type in this project on the intended write surface.
///
/// The integration-level replay-rebuild drill against a real Postgres server -- the one R11.9
/// actually asks to be "exercised in CI, not assumed" -- lives in
/// <c>Curia.Infrastructure.Tests</c>; this file is the fast, Postgres-free complement covering
/// the projector's own logic (folding, ordering, pagination) in isolation.
/// </summary>
public sealed class AggregateSummaryProjectorTests
{
    private static DomainEvent NewEvent(string id) => new(
        Require(EventId.Create(id)),
        Require(EventType.Create("test.event")),
        Actor: null,
        Payload: new JsonValue.Object([]));

    private static AggregateId Agg(string value) => Require(AggregateId.Create(value));

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static void AssertSameSummaries(
        IReadOnlyDictionary<AggregateId, AggregateSummary> expected,
        IReadOnlyDictionary<AggregateId, AggregateSummary> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        foreach (var (key, value) in expected)
        {
            Assert.True(actual.TryGetValue(key, out var actualValue), $"Missing aggregate {key.Value} in actual projection.");
            Assert.Equal(value, actualValue);
        }
    }

    [Fact]
    public async Task FoldComputesCountAndFirstLastSeqPerAggregate()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(TimeProvider.System);
        var a = Agg("agg-a");
        var b = Agg("agg-b");

        var firstA = Require(await store.AppendAsync(a, AggregateVersion.New, [NewEvent("a-1"), NewEvent("a-2")], ct));
        var firstB = Require(await store.AppendAsync(b, AggregateVersion.New, [NewEvent("b-1")], ct));
        var secondA = Require(await store.AppendAsync(
            a, Require(AggregateVersion.From(firstA.Count)), [NewEvent("a-3")], ct));

        var all = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct));
        var summaries = AggregateSummaryProjector.Fold(all);

        Assert.Equal(2, summaries.Count);
        Assert.Equal(3, summaries[a].EventCount);
        Assert.Equal(firstA[0].Seq, summaries[a].FirstSeq);
        Assert.Equal(secondA[0].Seq, summaries[a].LastSeq);
        Assert.Equal(secondA[0].ServerTimestamp, summaries[a].LastServerTimestamp);

        Assert.Equal(1, summaries[b].EventCount);
        Assert.Equal(firstB[0].Seq, summaries[b].FirstSeq);
        Assert.Equal(firstB[0].Seq, summaries[b].LastSeq);
    }

    [Fact]
    public async Task FoldUntouchedAggregateNeverAppearsInTheProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(TimeProvider.System);
        await store.AppendAsync(Agg("agg-touched"), AggregateVersion.New, [NewEvent("t-1")], ct);

        var all = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct));
        var summaries = AggregateSummaryProjector.Fold(all);

        Assert.False(summaries.ContainsKey(Agg("agg-never-appended")));
    }

    /// <summary>
    /// Reverses a legitimately-obtained event list (no fabricated <see cref="AppendedEvent"/> --
    /// see the class remarks) to prove <see cref="AggregateSummaryProjector.Fold"/> actually
    /// checks <c>seq</c> order rather than silently trusting the caller.
    /// </summary>
    [Fact]
    public async Task FoldThrowsWhenGivenEventsOutOfAscendingSeqOrder()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(TimeProvider.System);
        await store.AppendAsync(Agg("agg-order"), AggregateVersion.New, [NewEvent("o-1"), NewEvent("o-2")], ct);

        var inOrder = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct));
        var reversed = inOrder.Reverse().ToArray();

        var ex = Assert.Throws<InvalidOperationException>(() => AggregateSummaryProjector.Fold(reversed));
        Assert.Contains("ascending seq order", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The trap named in <see cref="AggregateSummaryProjector"/>'s remarks, made concrete: two
    /// batches for the same aggregate, with the *later* batch (higher <c>seq</c>) stamped with an
    /// *earlier* <c>server_ts</c> -- a legitimate outcome of a backward wall-clock adjustment
    /// between two <c>AppendAsync</c> calls, which nothing in the <c>IEventStore</c> contract
    /// forbids. A projector that computed "the maximum <c>server_ts</c> seen" would report the
    /// first batch's (later-looking) timestamp; this asserts it reports the second batch's
    /// instead, because <c>seq</c>, not timestamp magnitude, is what "last" means here.
    /// </summary>
    [Fact]
    public async Task FoldLastServerTimestampFollowsSeqEvenWhenServerTimeRanBackward()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryEventStore(clock);
        var a = Agg("agg-clock-skew");

        var laterLookingFirstBatch = Require(await store.AppendAsync(a, AggregateVersion.New, [NewEvent("skew-1")], ct));

        // Wall clock jumps backward before the second, seq-later batch.
        clock.Set(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var earlierLookingSecondBatch = Require(await store.AppendAsync(
            a, Require(AggregateVersion.From(laterLookingFirstBatch.Count)), [NewEvent("skew-2")], ct));

        var all = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct));
        var summaries = AggregateSummaryProjector.Fold(all);

        Assert.Equal(earlierLookingSecondBatch[0].Seq, summaries[a].LastSeq);
        Assert.Equal(earlierLookingSecondBatch[0].ServerTimestamp, summaries[a].LastServerTimestamp);
        Assert.NotEqual(laterLookingFirstBatch[0].ServerTimestamp, summaries[a].LastServerTimestamp);
    }

    [Fact]
    public async Task RebuildAsyncOnAnEmptyStoreReturnsAnEmptyProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(TimeProvider.System);

        var summaries = await AggregateSummaryProjector.RebuildAsync(store, cancellationToken: ct);

        Assert.Empty(summaries);
    }

    /// <summary>
    /// Pages of size 2 over 7 events across 3 aggregates -- several page boundaries, none of
    /// which land on an aggregate boundary -- must produce exactly the same result as a single
    /// unpaged <see cref="AggregateSummaryProjector.Fold"/> call. This is the direct exercise of
    /// <see cref="IEventReader.ReadForwardAsync"/>'s <c>afterSeq</c> cursor the drill's Postgres
    /// counterpart also relies on, at a scale small enough to hand-verify.
    /// </summary>
    [Fact]
    public async Task RebuildAsyncPagingProducesTheSameResultAsAnUnpagedFold()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(TimeProvider.System);
        var a = Agg("agg-page-a");
        var b = Agg("agg-page-b");
        var c = Agg("agg-page-c");

        await store.AppendAsync(a, AggregateVersion.New, [NewEvent("pa-1"), NewEvent("pa-2"), NewEvent("pa-3")], ct);
        await store.AppendAsync(b, AggregateVersion.New, [NewEvent("pb-1")], ct);
        await store.AppendAsync(c, AggregateVersion.New, [NewEvent("pc-1"), NewEvent("pc-2"), NewEvent("pc-3")], ct);

        var all = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: ct));
        Assert.Equal(7, all.Count); // guards against the scenario silently shrinking

        var unpaged = AggregateSummaryProjector.Fold(all);
        var paged = await AggregateSummaryProjector.RebuildAsync(store, pageSize: 2, cancellationToken: ct);

        AssertSameSummaries(unpaged, paged);
    }

    [Fact]
    public async Task RebuildAsyncPageSizeLargerThanTheWholeStoreStillProducesTheFullProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = new InMemoryEventStore(TimeProvider.System);
        await store.AppendAsync(Agg("agg-single-page"), AggregateVersion.New, [NewEvent("sp-1"), NewEvent("sp-2")], ct);

        var summaries = await AggregateSummaryProjector.RebuildAsync(store, pageSize: 1000, cancellationToken: ct);

        Assert.Single(summaries);
        Assert.Equal(2, summaries[Agg("agg-single-page")].EventCount);
    }

    [Fact]
    public void RebuildAsyncRejectsANonPositivePageSize()
    {
        var store = new InMemoryEventStore(TimeProvider.System);
        var ct = TestContext.Current.CancellationToken;

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => AggregateSummaryProjector.RebuildAsync(store, pageSize: 0, cancellationToken: ct).GetAwaiter().GetResult());
        Assert.Equal("pageSize", ex.ParamName);
    }
}
