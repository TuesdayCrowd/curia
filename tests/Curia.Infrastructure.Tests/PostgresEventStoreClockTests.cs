using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// CS-9 / T4.2 for the real adapter: mirrors <c>Curia.Application.Tests.InMemoryEventStoreTests</c>'
/// own two clock tests, against Postgres instead of the in-memory fake. Appendix D's
/// <c>server_ts TIMESTAMPTZ NOT NULL DEFAULT now()</c> default is left in place verbatim in
/// db/0001_create_events.sql (see that file's header), so these tests are what actually proves
/// <see cref="PostgresEventStore"/> never relies on it: every assertion below distinguishes "the
/// value the injected <see cref="ManualTimeProvider"/> reported" from "whatever Postgres's own
/// <c>now()</c> would have reported," which only differ because the adapter supplies
/// <c>server_ts</c> explicitly on every insert.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresEventStoreClockTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresEventStoreClockTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static DomainEvent NewEvent(string id) => new(
        Require(EventId.Create(id)),
        Require(EventType.Create("test.event")),
        Actor: null,
        Payload: new JsonValue.Object([]));

    private static AggregateId Agg(string value) => Require(AggregateId.Create(value));

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    [Fact]
    public async Task ServerTimestampComesFromTheInjectedClockAndNowhereElse()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.ResetEventsTableAsync(ct);

        // Picked far from "now" on purpose: if the adapter ever fell back to Postgres's own
        // now() (the DDL's DEFAULT, or an explicit now() call), the recorded server_ts would
        // land near the real wall clock, nowhere near 2026-01-01 -- an unmissable failure.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new PostgresEventStore(_fixture.AppRoleDataSource, clock);
        var aggregateId = Agg("agg-clock");

        var firstBatch = (await store.AppendAsync(aggregateId, AggregateVersion.New, [NewEvent("first")], ct))
            .Match(v => v, e => throw new InvalidOperationException(e.Type));
        Assert.Equal(ServerTimestamp.At(clock.GetUtcNow()), firstBatch[0].ServerTimestamp);

        clock.Set(new DateTimeOffset(2026, 6, 15, 12, 30, 0, TimeSpan.Zero));
        var secondVersion = AggregateVersion.From(firstBatch.Count).Match(v => v, e => throw new InvalidOperationException(e.Type));
        var secondBatch = (await store.AppendAsync(aggregateId, secondVersion, [NewEvent("second")], ct))
            .Match(v => v, e => throw new InvalidOperationException(e.Type));

        Assert.Equal(ServerTimestamp.At(new DateTimeOffset(2026, 6, 15, 12, 30, 0, TimeSpan.Zero)), secondBatch[0].ServerTimestamp);
        Assert.Equal(ServerTimestamp.At(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)), firstBatch[0].ServerTimestamp);
    }

    [Fact]
    public async Task AllEventsInOneAppendCallShareOneClockRead()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.ResetEventsTableAsync(ct);

        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new PostgresEventStore(_fixture.AppRoleDataSource, clock);
        var aggregateId = Agg("agg-batch-clock");

        var batch = (await store.AppendAsync(
                aggregateId, AggregateVersion.New, [NewEvent("b1"), NewEvent("b2"), NewEvent("b3")], ct))
            .Match(v => v, e => throw new InvalidOperationException(e.Type));

        Assert.All(batch, e => Assert.Equal(batch[0].ServerTimestamp, e.ServerTimestamp));
    }
}
