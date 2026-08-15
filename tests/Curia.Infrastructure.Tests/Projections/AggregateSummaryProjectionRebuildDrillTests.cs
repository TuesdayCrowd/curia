using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Infrastructure.Projections;
using CsCheck;
using Xunit;

namespace Curia.Infrastructure.Tests.Projections;

/// <summary>
/// The R11.9 replay-rebuild drill itself: an integration test against the real Postgres server
/// (never an in-memory fake -- <c>Curia.Application.Tests.Projections.AggregateSummaryProjectorTests</c>
/// covers the projector's own fold/pagination logic at unit speed) proving the drill's five
/// steps in order, plus the property that makes the drill mean anything: replay is deterministic.
///
/// <see cref="PostgresAggregateSummaryProjection"/> is built on <see cref="PostgresDatabaseFixture.AdminDataSource"/>,
/// not the R11.6-constrained app role -- see that type's remarks for why a projection table
/// needs a different privilege boundary than the append-only event log. The <em>event reader</em>
/// every rebuild replays through, however, is a <see cref="PostgresEventStore"/> bound to
/// <see cref="PostgresDatabaseFixture.AppRoleDataSource"/> -- the exact restricted role R11.6
/// governs -- so the drill exercises the real production read path for "the event table," not a
/// superuser shortcut.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AggregateSummaryProjectionRebuildDrillTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public AggregateSummaryProjectionRebuildDrillTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

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

    /// <summary>Boolean twin of <see cref="AssertSameSummaries"/> for use inside a CsCheck
    /// property predicate, which must return <see langword="bool"/> rather than assert.</summary>
    private static bool SameSummaries(
        IReadOnlyDictionary<AggregateId, AggregateSummary> left,
        IReadOnlyDictionary<AggregateId, AggregateSummary> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var otherValue) || !Equals(value, otherValue))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The drill, exactly as specified: append a generated history, build the projection, drop
    /// it entirely (proved, not merely called), rebuild from the event table alone by replay,
    /// and assert the rebuild is identical to what was dropped.
    /// </summary>
    [Fact]
    public async Task RebuildDrillDropThenRebuildFromEventTableAloneProducesIdenticalProjection()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedSchemaAsync(ct);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var eventStore = new PostgresEventStore(_fixture.AppRoleDataSource, clock, schema);
        var projection = new PostgresAggregateSummaryProjection(_fixture.AdminDataSource, clock, schema);

        var alpha = Agg("agg-alpha");
        var beta = Agg("agg-beta");
        var gamma = Agg("agg-gamma");

        // Step 1: append a generated event history -- three aggregates, five interleaved
        // batches, the clock advancing between batches so more than one server_ts appears in
        // the stream (real deployments do not append everything in the same instant).
        var alphaBatch1 = Require(await eventStore.AppendAsync(alpha, AggregateVersion.New, [NewEvent("alpha-1"), NewEvent("alpha-2")], ct));
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        var betaBatch1 = Require(await eventStore.AppendAsync(beta, AggregateVersion.New, [NewEvent("beta-1")], ct));
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        var alphaBatch2 = Require(await eventStore.AppendAsync(
            alpha, Require(AggregateVersion.From(alphaBatch1.Count)), [NewEvent("alpha-3")], ct));
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        var gammaBatch1 = Require(await eventStore.AppendAsync(
            gamma, AggregateVersion.New, [NewEvent("gamma-1"), NewEvent("gamma-2"), NewEvent("gamma-3")], ct));
        clock.Set(clock.GetUtcNow().AddMinutes(1));
        var betaBatch2 = Require(await eventStore.AppendAsync(
            beta, Require(AggregateVersion.From(betaBatch1.Count)), [NewEvent("beta-2"), NewEvent("beta-3")], ct));

        // Step 2: build the projection. pageSize: 2 against 9 total events forces several
        // pages, so this is a real exercise of the forward-scan cursor, not a single unpaged call.
        Assert.False(await projection.ExistsAsync(ct), "the projection table must not exist before the first build.");
        var built = await projection.RebuildAsync(eventStore, pageSize: 2, cancellationToken: ct);
        var beforeDrop = await projection.ReadAllAsync(ct);
        Assert.True(await projection.ExistsAsync(ct));
        AssertSameSummaries(built, beforeDrop);
        Assert.Equal(3, beforeDrop.Count);

        // Step 3: drop it entirely -- and prove the table is genuinely gone, not merely that
        // DropAsync returned without throwing.
        await projection.DropAsync(ct);
        Assert.False(await projection.ExistsAsync(ct), "the table must be genuinely gone after DropAsync.");

        // Step 4: rebuild from the event table alone, by replay. `eventStore` is the exact same
        // IEventReader used to build the first projection; nothing carries state forward from
        // "before" to "after" except the real events row on the server.
        var afterDrop = await projection.RebuildAsync(eventStore, pageSize: 2, cancellationToken: ct);
        var afterDropRead = await projection.ReadAllAsync(ct);

        // Step 5: identical -- both the freshly computed in-memory result and what actually
        // round-tripped through a real INSERT and SELECT.
        AssertSameSummaries(beforeDrop, afterDropRead);
        AssertSameSummaries(built, afterDrop);

        // Concrete, hand-checkable content, not just "the two dictionaries match each other" --
        // guards against both sides being identically wrong.
        Assert.Equal(3, afterDropRead[alpha].EventCount);
        Assert.Equal(alphaBatch1[0].Seq, afterDropRead[alpha].FirstSeq);
        Assert.Equal(alphaBatch2[0].Seq, afterDropRead[alpha].LastSeq);
        Assert.Equal(alphaBatch2[0].ServerTimestamp, afterDropRead[alpha].LastServerTimestamp);

        Assert.Equal(3, afterDropRead[beta].EventCount);
        Assert.Equal(betaBatch1[0].Seq, afterDropRead[beta].FirstSeq);
        Assert.Equal(betaBatch2[^1].Seq, afterDropRead[beta].LastSeq);
        Assert.Equal(betaBatch2[^1].ServerTimestamp, afterDropRead[beta].LastServerTimestamp);

        Assert.Equal(3, afterDropRead[gamma].EventCount);
        Assert.Equal(gammaBatch1[0].Seq, afterDropRead[gamma].FirstSeq);
        Assert.Equal(gammaBatch1[^1].Seq, afterDropRead[gamma].LastSeq);
    }

    /// <summary>
    /// The property that makes the drill mean something, proved rather than asserted once:
    /// across many randomly generated append histories, dropping and rebuilding the projection
    /// twice -- with the injected clock set to a wildly different reading for the second rebuild
    /// -- always produces the identical projection. If a rebuild ever read "now" from anywhere
    /// (see <see cref="AggregateSummaryProjector"/>'s class remarks on this exact trap), moving
    /// the clock between the two rebuilds is precisely what would expose it; every one of these
    /// iterations gives that bug a chance to show itself and it never does.
    ///
    /// Mirrors <c>EventStorePortContractTests.SeqIsStrictlyMonotonicAcrossAggregatesUnderRandomAppendSequences</c>'s
    /// own CsCheck-against-real-Postgres shape: one fresh, isolated schema per generated case
    /// (via <see cref="PostgresDatabaseFixture.CreateIsolatedSchemaAsync"/>), for the same reason
    /// that test needs it -- CsCheck runs generated cases with genuine internal concurrency, so a
    /// shared table between cases is not actually isolated.
    /// </summary>
    [Fact]
    public void ReplayIsDeterministicAcrossManyRandomHistoriesAndDifferentClockReadings() =>
        Gen.Select(Gen.Int[0, 4], Gen.Int[1, 3])
            .List[1, 20]
            .Sample(script =>
            {
                var schema = _fixture.CreateIsolatedSchemaAsync().GetAwaiter().GetResult();
                var clock = new ManualTimeProvider(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
                var eventStore = new PostgresEventStore(_fixture.AppRoleDataSource, clock, schema);
                var projection = new PostgresAggregateSummaryProjection(_fixture.AdminDataSource, clock, schema);

                var versions = new Dictionary<int, AggregateVersion>();
                foreach (var (aggregateIndex, count) in script)
                {
                    var aggregateId = Agg($"agg-{aggregateIndex}");
                    var expected = versions.GetValueOrDefault(aggregateIndex, AggregateVersion.New);
                    var batch = Enumerable.Range(0, count)
                        .Select(i => NewEvent($"{aggregateIndex}-{Guid.NewGuid():N}-{i}"))
                        .ToArray();

                    eventStore.AppendAsync(aggregateId, expected, batch).GetAwaiter().GetResult()
                        .Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

                    versions[aggregateIndex] = Require(AggregateVersion.From(expected.Value + count));
                }

                // Rebuild #1 at the original clock reading.
                projection.RebuildAsync(eventStore).GetAwaiter().GetResult();
                var first = projection.ReadAllAsync().GetAwaiter().GetResult();

                // Drop, jump the clock 15 years forward, rebuild #2 -- from the event table
                // alone, nothing carried over from rebuild #1.
                projection.DropAsync().GetAwaiter().GetResult();
                clock.Set(new DateTimeOffset(2035, 6, 15, 12, 0, 0, TimeSpan.Zero));
                projection.RebuildAsync(eventStore).GetAwaiter().GetResult();
                var second = projection.ReadAllAsync().GetAwaiter().GetResult();

                return SameSummaries(first, second);
            }, iter: 30);
}
