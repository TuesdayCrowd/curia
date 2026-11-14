using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// LRV34 and LRV35 (errata G68, PR N's ruling 72): the log is the rows at <c>seq &gt; 0</c>. A row an administrator planted at a position at or below zero
/// is in no proof, no head and no forward read, so the store's version of a stream, its condition's version and its read by aggregate must not count or return it.
/// Every expectation is read from raw SQL over the admin connection (<c>seq &gt; 0</c> written out in the test), never from the store's own constant.
/// The planting is the administrator's: the application role cannot plant once <c>db/0006</c> is applied. A planted row is inert, not detected (G69 non-property (c)).
/// The in-memory adapter cannot plant (its <c>seq</c> is <c>count + 1</c>), so <c>EventStorePortContractTests</c> holds only the contract line (LRV39); this file is where the filter is pinned.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage(
    "Security",
    "CA2100:Review SQL queries for security vulnerabilities",
    Justification = "The only interpolated text is the schema name CreateIsolatedSchemaAsync generated from a literal prefix and a GUID's hex digits; every other value is bound as a parameter.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresEventStorePositionRowTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresEventStorePositionRowTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    public static TheoryData<string> Plants => new() { "0", "-1", "0,-1" };

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static AggregateId Agg(string value) => Require(AggregateId.Create(value));

    private static DomainEvent NewEvent(string id) => new(
        Require(EventId.Create(id)),
        Require(EventType.Create("test.event")),
        Actor: null,
        Payload: new JsonValue.Object([]));

    private static AggregateVersion Version(int value) => Require(AggregateVersion.From(value));

    /// <summary>A fresh schema, a store on the application role, and aggregate <paramref name="aggregate"/> holding two real events and then the planted rows.</summary>
    private async Task<(string Schema, PostgresEventStore Store)> PlantedAsync(string aggregate, string plants, CancellationToken ct)
    {
        var schema = await _fixture.CreateIsolatedSchemaAsync(ct);
        var store = new PostgresEventStore(_fixture.AppRoleDataSource, TimeProvider.System, _fixture.PermissiveLifecycleGate(), schema);
        var a = Agg(aggregate);
        Require(await store.AppendAsync(a, AggregateVersion.New, [NewEvent(aggregate + "-1"), NewEvent(aggregate + "-2")], ct));

        await using var admin = await _fixture.AdminDataSource.OpenConnectionAsync(ct);
        foreach (var plant in plants.Split(','))
        {
            await using var insert = new NpgsqlCommand(
                $"INSERT INTO \"{schema}\".events (seq, event_id, event_type, aggregate_id, actor_id, payload, server_ts) OVERRIDING SYSTEM VALUE VALUES (@seq, @id, 'test.event', @agg, 'operator:ops', '{{}}'::jsonb, now());",
                admin);
            insert.Parameters.AddWithValue("seq", long.Parse(plant, CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("id", $"{aggregate}-plant{plant}");
            insert.Parameters.AddWithValue("agg", aggregate);
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
        }

        return (schema, store);
    }

    private async Task<long> RawLogCountAsync(string schema, string aggregate, CancellationToken ct)
    {
        await using var admin = await _fixture.AdminDataSource.OpenConnectionAsync(ct);
        await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{schema}\".events WHERE aggregate_id = @agg AND seq > 0;", admin);
        count.Parameters.AddWithValue("agg", aggregate);
        return (long)(await count.ExecuteScalarAsync(ct))!;
    }

    /// <summary>
    /// LRV34: the read by aggregate, the forward read and the raw log count are one set of rows. The forward-read leg is a control: <c>ReadForwardAsync(Zero)</c> is <c>seq &gt; 0</c> by definition and agrees under any
    /// store; the legs that discriminate the filter are the by-aggregate read against the raw count (Amendment 7, ruling 87).
    /// </summary>
    [Theory]
    [MemberData(nameof(Plants))]
    public async Task LRV34_TheThreeBasesAgree(string plants)
    {
        ArgumentNullException.ThrowIfNull(plants);
        var ct = TestContext.Current.CancellationToken;
        var (schema, store) = await PlantedAsync("lrv34-a", plants, ct);
        var a = Agg("lrv34-a");

        var raw = await RawLogCountAsync(schema, "lrv34-a", ct);
        var byAggregate = Require(await store.ReadByAggregateAsync(a, ct)).Count;
        var forward = Require(await store.ReadForwardAsync(EventSequence.Zero, null, ct)).Count(e => e.AggregateId == a);

        Assert.Equal("raw 2; forward 2; by aggregate 2", $"raw {raw}; forward {forward}; by aggregate {byAggregate}");
    }

    /// <summary>LRV34: the version the store enforces is the log's count, so an honest append expecting 2 lands.</summary>
    [Theory]
    [MemberData(nameof(Plants))]
    public async Task LRV34_TheVersionTheStoreEnforcesIsTheLogsCount(string plants)
    {
        ArgumentNullException.ThrowIfNull(plants);
        var ct = TestContext.Current.CancellationToken;
        var (schema, store) = await PlantedAsync("lrv34-b", plants, ct);
        var a = Agg("lrv34-b");
        var expected = await RawLogCountAsync(schema, "lrv34-b", ct);
        Assert.Equal(2, expected);

        var tooHigh = await store.AppendAsync(a, Version((int)expected + 1), [NewEvent("lrv34-b-high")], ct);
        var tooLow = await store.AppendAsync(a, Version((int)expected - 1), [NewEvent("lrv34-b-low")], ct);

        // The third leg of a refusal (ruling 88): the refused appends wrote nothing, so the aggregate still holds exactly the rows it held before them.
        var afterRefusals = Require(await store.ReadByAggregateAsync(a, ct)).Select(e => e.Event.Id.Value).ToArray();
        var honest = await store.AppendAsync(a, Version((int)expected), [NewEvent("lrv34-b-honest")], ct);

        Assert.Equal(
            $"3 refused {DomainErrors.ConcurrencyConflictType}; 1 refused {DomainErrors.ConcurrencyConflictType}; held [lrv34-b-1, lrv34-b-2]; 2 landed",
            $"3 {Outcome(tooHigh)}; 1 {Outcome(tooLow)}; held [{string.Join(", ", afterRefusals)}]; 2 {Outcome(honest)}");
    }

    /// <summary>LRV35: the condition's version is the log's count too (the 503 of Task 4b, stated with no Forum in front of it).</summary>
    [Theory]
    [MemberData(nameof(Plants))]
    public async Task LRV35_TheConditionsVersionIsTheLogsCount(string plants)
    {
        ArgumentNullException.ThrowIfNull(plants);
        var ct = TestContext.Current.CancellationToken;
        var (schema, store) = await PlantedAsync("lrv35-c", plants, ct);
        var c = Agg("lrv35-c");
        Assert.Equal(2, await RawLogCountAsync(schema, "lrv35-c", ct));

        var tooHigh = await store.AppendAsync(Agg("lrv35-p3"), AggregateVersion.New, [NewEvent("lrv35-p3-1")], new StreamCondition(c, Version(3)), ct);
        var honest = await store.AppendAsync(Agg("lrv35-p2"), AggregateVersion.New, [NewEvent("lrv35-p2-1")], new StreamCondition(c, Version(2)), ct);

        var actual = tooHigh.Match(_ => "none", e => e.Detail ?? string.Empty);

        // The third leg of a refusal (ruling 88): the refused condition wrote nothing to the aggregate it guarded.
        var refusedAggregate = Require(await store.ReadByAggregateAsync(Agg("lrv35-p3"), ct)).Count;
        Assert.Equal(
            $"condition 3 refused {DomainErrors.ConditionConflictType} aggregate=lrv35-c expected=3 actual=2; lrv35-p3 holds 0; condition 2 landed",
            $"condition 3 {Outcome(tooHigh)} {actual}; lrv35-p3 holds {refusedAggregate}; condition 2 {Outcome(honest)}");
    }

    private static string Outcome(Result<IReadOnlyList<AppendedEvent>> result) =>
        result.Match(_ => "landed", e => "refused " + e.Type);
}
