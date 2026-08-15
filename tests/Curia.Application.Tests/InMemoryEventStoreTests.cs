using Curia.Application.Ports;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// Runs the shared <see cref="EventStorePortContractTests"/> suite against
/// <see cref="InMemoryEventStore"/>, plus one test specific to this adapter: that it never reads
/// wall-clock time on its own (CS-9), only its injected <see cref="TimeProvider"/>.
/// </summary>
public sealed class InMemoryEventStoreTests : EventStorePortContractTests
{
    protected override IEventStore CreateStore() => new InMemoryEventStore(TimeProvider.System);

    private static DomainEvent NewEvent(string id) => new(
        EventId.Create(id).Match(v => v, e => throw new InvalidOperationException(e.Type)),
        EventType.Create("test.event").Match(v => v, e => throw new InvalidOperationException(e.Type)),
        Actor: null,
        Payload: new JsonValue.Object([]));

    private static AggregateId Agg(string value) =>
        AggregateId.Create(value).Match(v => v, e => throw new InvalidOperationException(e.Type));

    /// <summary>
    /// Not part of the shared contract suite: a Postgres adapter (Stage 2) may instead rely on
    /// the database's own transaction timestamp rather than routing through an injected
    /// TimeProvider, so asserting exact equality against an arbitrary fake instant is specific to
    /// how this adapter is built, not to what every IEventStore must guarantee. See the Stage 1
    /// report for why Stage 2 is nonetheless recommended to mirror this rather than defaulting to
    /// SQL's <c>now()</c>.
    /// </summary>
    [Fact]
    public async Task ServerTimestampComesFromTheInjectedClockAndNowhereElse()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryEventStore(clock);
        var aggregateId = Agg("agg-clock");

        var firstBatch = (await store.AppendAsync(aggregateId, AggregateVersion.New, [NewEvent("first")], ct))
            .Match(v => v, e => throw new InvalidOperationException(e.Type));
        Assert.Equal(clock.GetUtcNow(), firstBatch[0].ServerTimestamp);

        clock.Set(new DateTimeOffset(2026, 6, 15, 12, 30, 0, TimeSpan.Zero));
        var secondVersion = AggregateVersion.From(firstBatch.Count).Match(v => v, e => throw new InvalidOperationException(e.Type));
        var secondBatch = (await store.AppendAsync(aggregateId, secondVersion, [NewEvent("second")], ct))
            .Match(v => v, e => throw new InvalidOperationException(e.Type));

        // Advancing the clock moved the new event's timestamp and left the old one alone --
        // the only way that can happen is if AppendAsync reads GetUtcNow() at append time and
        // never caches or otherwise derives it from anything but the injected TimeProvider.
        Assert.Equal(new DateTimeOffset(2026, 6, 15, 12, 30, 0, TimeSpan.Zero), secondBatch[0].ServerTimestamp);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), firstBatch[0].ServerTimestamp);
    }

    /// <summary>All events in one Append call share a single clock read (mirroring a batched
    /// Postgres INSERT inside one transaction), even though wall-clock time keeps moving.</summary>
    [Fact]
    public async Task AllEventsInOneAppendCallShareOneClockRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryEventStore(clock);
        var aggregateId = Agg("agg-batch-clock");

        var batch = (await store.AppendAsync(
                aggregateId, AggregateVersion.New, [NewEvent("b1"), NewEvent("b2"), NewEvent("b3")], ct))
            .Match(v => v, e => throw new InvalidOperationException(e.Type));

        Assert.All(batch, e => Assert.Equal(batch[0].ServerTimestamp, e.ServerTimestamp));
    }
}
