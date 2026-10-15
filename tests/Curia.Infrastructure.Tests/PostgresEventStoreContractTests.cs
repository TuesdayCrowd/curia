using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// Runs the shared <see cref="EventStorePortContractTests"/> suite against
/// <see cref="PostgresEventStore"/> -- the same contract <c>InMemoryEventStoreTests</c> holds
/// the Stage 1 adapter to, so a real difference between the two adapters shows up as a failing
/// inherited test here rather than as an untested divergence.
///
/// <see cref="CreateStore"/> builds the store on <see cref="PostgresDatabaseFixture.AppRoleDataSource"/>,
/// not the admin connection: this contract suite therefore exercises the adapter under the
/// exact R11.6-constrained privileges (INSERT, SELECT only) it will actually run with in
/// production, not a superuser connection that would let a latent bug (e.g. an accidental
/// UPDATE) pass unnoticed.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "Must be public for xUnit to discover it as a concrete test class: it carries no " +
        "[Fact] of its own (every test comes from the inherited EventStorePortContractTests base), so " +
        "the analyzer's own test-class heuristic does not recognize it the way it recognizes a class " +
        "with direct [Fact] methods -- but xUnit's own discovery still requires the class public " +
        "regardless of where its [Fact]s are declared.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresEventStoreContractTests : EventStorePortContractTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresEventStoreContractTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    protected override IEventStore CreateStore()
    {
        // CreateStore() is synchronous (the abstract suite's contract), so the async schema
        // provisioning is blocked on here -- the same accepted pattern
        // EventStorePortContractTests' own property test already uses for AppendAsync via
        // GetAwaiter().GetResult(). A fresh schema per call, not a shared-table TRUNCATE: see
        // PostgresDatabaseFixture.CreateIsolatedSchemaAsync's remarks for why this suite
        // specifically needs true per-call isolation, including under CsCheck's own
        // concurrent case execution.
        var schema = _fixture.CreateIsolatedSchemaAsync().GetAwaiter().GetResult();
        return new PostgresEventStore(_fixture.AppRoleDataSource, TimeProvider.System, _fixture.PermissiveLifecycleGate(), schema);
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static DomainEvent NewEvent(string id) => new(
        Require(EventId.Create(id)),
        Require(EventType.Create("test.event")),
        Actor: null,
        Payload: new JsonValue.Object([]));

    /// <summary>
    /// R6.56: the condition is read after the append has the serialization lock, not before. A transaction
    /// holds the lock and appends to B; the conditioned append (A on B@0) is started and observed waiting on
    /// the advisory lock; the lock is released by commit. The append must then conflict -- a condition read
    /// before the lock would have seen B at 0 and landed. Red when the read moves ahead of the lock.
    /// </summary>
    [SuppressMessage(
        "Naming",
        "CA1707:Identifiers should not contain underscores",
        Justification = "Test names carry the requirement IDs they enforce verbatim.")]
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The only interpolated text is the schema name CreateIsolatedSchemaAsync generated " +
            "from a literal prefix and a GUID's hex digits; every other value is bound as a parameter.")]
    [Fact]
    public async Task R6_56_TheConditionIsReadUnderTheSerializationLock()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedSchemaAsync(ct);
        var store = new PostgresEventStore(_fixture.AppRoleDataSource, TimeProvider.System, _fixture.PermissiveLifecycleGate(), schema);
        var a = Require(AggregateId.Create("agg-lock-target"));
        var b = Require(AggregateId.Create("agg-lock-stream"));

        await using var holder = await _fixture.AdminDataSource.OpenConnectionAsync(ct);
        await using var transaction = await holder.BeginTransactionAsync(ct);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));", holder, transaction))
        {
            take.Parameters.AddWithValue("key", PostgresEventStore.SerializationLockKey(schema));
            await take.ExecuteNonQueryAsync(ct);
        }

        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO \"{schema}\".events (event_id, event_type, aggregate_id, payload) VALUES ('lock-b-1', 'test.event', @agg, '{{}}');",
            holder, transaction))
        {
            insert.Parameters.AddWithValue("agg", b.Value);
            await insert.ExecuteNonQueryAsync(ct);
        }

        var conditioned = store.AppendAsync(a, AggregateVersion.New, [NewEvent("lock-a-1")], new StreamCondition(b, AggregateVersion.New), ct);

        var waiting = false;
        for (var attempt = 0; attempt < 200 && !waiting; attempt++)
        {
            await using var probe = new NpgsqlCommand(
                "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database());",
                holder);
            waiting = (long)(await probe.ExecuteScalarAsync(ct))! > 0;
            if (!waiting)
                await Task.Delay(25, ct);
        }

        Assert.True(waiting, "the conditioned append never waited on the serialization lock");
        Assert.False(conditioned.IsCompleted, "the conditioned append finished while another transaction held the lock");

        await transaction.CommitAsync(ct);
        var result = await conditioned;

        Assert.Equal(DomainErrors.ConditionConflictType, result.Match(_ => string.Empty, e => e.Type));
        Assert.Empty(Require(await store.ReadByAggregateAsync(a, ct)));
    }
}
