using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// R6.47: appends serialize behind one lock per log, so that <c>seq</c> order is commit order
/// and a tree folded at any instant is a prefix of every later tree. Observed directly: a
/// transaction holding the log's lock keeps an append to an <i>unrelated</i> aggregate waiting,
/// which is exactly what the per-aggregate lock it replaced did not do.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PostgresEventStoreSerializationTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresEventStoreSerializationTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    private static DomainEvent NewEvent(string id) => new(
        Require(EventId.Create(id)),
        Require(EventType.Create("test.event")),
        Actor: null,
        Payload: new JsonValue.Object([]));

    [Fact]
    public async Task R6_47_AnAppendToAnyAggregateWaitsWhileTheLogLockIsHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedSchemaAsync(ct);
        var store = new PostgresEventStore(_fixture.AppRoleDataSource, TimeProvider.System, schema);

        // Hold the log's lock in a transaction of our own, as a concurrent appender would mid-insert.
        await using var holder = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var holding = await holder.BeginTransactionAsync(ct);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0));", holder, holding))
        {
            take.Parameters.AddWithValue("k", PostgresEventStore.SerializationLockKey(schema));
            await take.ExecuteNonQueryAsync(ct);
        }

        var append = store.AppendAsync(Require(AggregateId.Create("unrelated")), AggregateVersion.New, [NewEvent("e1")], ct);
        var finishedWhileHeld = await Task.WhenAny(append, Task.Delay(TimeSpan.FromSeconds(2), ct)) == append;
        Assert.False(finishedWhileHeld, "an append proceeded while the log's lock was held by another transaction");

        await holding.CommitAsync(ct);
        Require(await append);

        var read = Require(await store.ReadForwardAsync(EventSequence.Zero, null, ct));
        Assert.Equal("e1", Assert.Single(read).Event.Id.Value);
    }

    [Fact]
    public async Task R6_47_TheLockIsPerLogNotPerAggregate()
    {
        var ct = TestContext.Current.CancellationToken;
        var schemaA = await _fixture.CreateIsolatedSchemaAsync(ct);
        var schemaB = await _fixture.CreateIsolatedSchemaAsync(ct);
        Assert.NotEqual(PostgresEventStore.SerializationLockKey(schemaA), PostgresEventStore.SerializationLockKey(schemaB));

        // Holding log A's lock does not delay log B.
        await using var holder = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var holding = await holder.BeginTransactionAsync(ct);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0));", holder, holding))
        {
            take.Parameters.AddWithValue("k", PostgresEventStore.SerializationLockKey(schemaA));
            await take.ExecuteNonQueryAsync(ct);
        }

        var storeB = new PostgresEventStore(_fixture.AppRoleDataSource, TimeProvider.System, schemaB);
        var append = storeB.AppendAsync(Require(AggregateId.Create("b")), AggregateVersion.New, [NewEvent("e1")], ct);
        Assert.True(await Task.WhenAny(append, Task.Delay(TimeSpan.FromSeconds(5), ct)) == append, "log B waited for log A's lock");
        Require(await append);
        await holding.RollbackAsync(ct);
    }
}
