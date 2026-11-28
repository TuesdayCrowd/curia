using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// R6.69 (errata G87): the append writes the leaf of the row it appends, in the same INSERT, and the role that appends can neither change it nor name it on an UPDATE. SL1 holds the stored leaf to
/// <c>conformance/acta/</c>, which was computed outside both implementations; SL2 to the leaf recomputed from the row read back, at a clock reading with a seventh fractional digit; SL3 to SL6 are
/// the harm and its detection (a wrong leaf the owner plants, a row with none, the backfill that fills only what is missing and never heals what is wrong); SL7 is the grant.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit discovers a concrete test class only when it is public.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every interpolated identifier is a schema name this fixture generated from a GUID's hex digits.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class StoredLeafTests(PostgresDatabaseFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} {e.Detail}"));

    private static string ConformanceActa()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "conformance", "acta");
            if (Directory.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new InvalidOperationException("conformance/acta/ is not above " + AppContext.BaseDirectory);
    }

    public static TheoryData<string> Vectors()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.EnumerateDirectories(ConformanceActa()).OrderBy(d => d, StringComparer.Ordinal))
            data.Add(Path.GetFileName(dir));
        return data;
    }

    private async Task<(PostgresEventStore Store, string Schema, ManualTimeProvider Clock)> NewStoreAsync(DateTimeOffset now)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(Ct);
        var clock = new ManualTimeProvider(now);
        return (new PostgresEventStore(fixture.AppRoleDataSource, clock, fixture.PermissiveLifecycleGate(), schema), schema, clock);
    }

    private static DomainEvent Plain(string id, JsonValue? payload = null) => new(
        Require(EventId.Create(id)), Require(EventType.Create("test.event")), Actor: null, Payload: payload ?? new JsonValue.Object([new("n", new JsonValue.Number(1))]));

    private static async Task<IReadOnlyList<AppendedEvent>> AppendOneAsync(PostgresEventStore store, string aggregate, long version, DomainEvent e) =>
        Require(await store.AppendAsync(Require(AggregateId.Create(aggregate)), Require(AggregateVersion.From(version)), [e], Ct));

    // ---- SL1 ----

    /// <summary>SL1: every published entry kind, appended through the store at its vector's instant, is stored with the vector's leaf, and reads back with it.</summary>
    [Theory]
    [MemberData(nameof(Vectors))]
    public async Task R6_69_SL1_TheStoredLeafIsTheConformanceVectorsLeaf(string vector)
    {
        var dir = Path.Combine(ConformanceActa(), vector);
        var doc = (JsonValue.Object)Require(JsonReader.ParseUnrestricted(await File.ReadAllBytesAsync(Path.Combine(dir, "input.json"), Ct)));
        JsonValue? Member(string name) => doc.Members.FirstOrDefault(m => m.Key == name).Value;
        string Str(string name) => ((JsonValue.String)Member(name)!).Value;
        var actor = Member("actor_id") is JsonValue.String a ? Require(ActorId.Create(a.Value)) : (ActorId?)null;
        var at = DateTimeOffset.Parse(Str("server_ts"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var (store, _, _) = await NewStoreAsync(at);
        var domainEvent = new DomainEvent(Require(EventId.Create(Str("event_id"))), Require(EventType.Create(Str("event_type"))), actor, Member("payload")!);

        var appended = await AppendOneAsync(store, Str("aggregate_id"), 0, domainEvent);
        var read = Require(await store.ReadAllAsync(Ct));

        var expected = "sha256:" + (await File.ReadAllTextAsync(Path.Combine(dir, "expected.leaf"), Ct)).Trim();
        Assert.Equal(expected, appended[0].StoredLeafHash);
        Assert.Equal(expected, read[0].StoredLeafHash);
    }

    // ---- SL2 ----

    /// <summary>SL2: at a clock reading with a seventh fractional digit (the column holds microseconds) and a payload jsonb reorders, the stored leaf equals the leaf of the row read back.</summary>
    [Fact]
    public async Task R6_69_SL2_TheStoredLeafEqualsTheLeafOfTheRowReadBack()
    {
        var (store, _, _) = await NewStoreAsync(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero).AddTicks(1234569));
        var payload = new JsonValue.Object(
        [
            new("z", new JsonValue.Number(1)), new("longer_key_b", new JsonValue.Number(2.5)), new("a", new JsonValue.String("café")),
            new("big", new JsonValue.Number(1e30)), new("nested", new JsonValue.Object([new("y", new JsonValue.Null()), new("x", new JsonValue.Array([new JsonValue.Number(0), new JsonValue.Number(-0.5)]))])),
        ]);

        var appended = await AppendOneAsync(store, "agg-sl2", 0, Plain("sl2", payload));
        var read = Require(await store.ReadAllAsync(Ct));

        Assert.Equal(LogEntries.Prefixed(Require(LogLeaf.Hash(read[0]))), appended[0].StoredLeafHash);
        Assert.Equal(appended[0].StoredLeafHash, read[0].StoredLeafHash);
    }

    // ---- SL3 ----

    /// <summary>SL3: a wrong leaf planted as the owner is found by the audit, and the stored fold disagrees with the recomputing fold from that row on.</summary>
    [Fact]
    public async Task R6_69_SL3_AWrongLeafPlantedByTheOwnerIsFound()
    {
        var (store, schema, _) = await NewStoreAsync(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        for (var i = 0; i < 4; i++) await AppendOneAsync(store, "agg-sl3-" + i, 0, Plain("sl3-" + i));
        await AdminAsync($"UPDATE {Quoted(schema)}.events SET leaf_hash = decode(repeat('00', 32), 'hex') WHERE event_id = 'sl3-2';");

        var log = Require(await store.ReadAllAsync(Ct));
        var findings = Require(StoredLeafAudit.Check(log));

        Assert.Equal([2L], findings.Mismatched);
        Assert.NotEqual(Convert.ToHexString([.. Require(ActaLog.FoldRecomputed(log)).Root]), Convert.ToHexString([.. Require(ActaLog.Fold(log)).Root]));
    }

    // ---- SL4 ----

    /// <summary>SL4: a row appended with no leaf (an old row, or a statement that does not name the column) is reported missing, and served by recomputation to the same root.</summary>
    [Fact]
    public async Task R6_69_SL4_ARowWithNoLeafIsMissingAndServedByRecomputation()
    {
        var (store, schema, _) = await NewStoreAsync(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        await AppendOneAsync(store, "agg-sl4-0", 0, Plain("sl4-0"));
        await using (var raw = await fixture.AppRoleDataSource.OpenConnectionAsync(Ct))
        await using (var insert = new NpgsqlCommand($"INSERT INTO {Quoted(schema)}.events (event_id, event_type, aggregate_id, payload, server_ts) VALUES ('sl4-old', 'test.event', 'agg-sl4-1', '{{\"n\":2}}', '2026-11-27T10:00:00Z');", raw))
            await insert.ExecuteNonQueryAsync(Ct);
        await AppendOneAsync(store, "agg-sl4-2", 0, Plain("sl4-2"));

        var log = Require(await store.ReadAllAsync(Ct));
        var findings = Require(StoredLeafAudit.Check(log));
        var served = Require(ActaLog.Fold(log));

        Assert.Equal([1L], findings.Missing);
        Assert.Empty(findings.Mismatched);
        Assert.Equal(1, served.LeavesRecomputed);
        Assert.Equal(Convert.ToHexString([.. Require(ActaLog.FoldRecomputed(log)).Root]), Convert.ToHexString([.. served.Root]));
    }

    // ---- SL5 ----

    /// <summary>SL5: the backfill, run as the owner, fills what is missing and reports it; a second run fills nothing; and a wrong leaf already there is left for the audit, never overwritten.</summary>
    [Fact]
    public async Task R6_69_SL5_TheBackfillFillsOnlyWhatIsMissingAndNeverHealsAWrongLeaf()
    {
        var (store, schema, _) = await NewStoreAsync(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        for (var i = 0; i < 5; i++) await AppendOneAsync(store, "agg-sl5-" + i, 0, Plain("sl5-" + i));
        await AdminAsync($"UPDATE {Quoted(schema)}.events SET leaf_hash = NULL WHERE event_id IN ('sl5-0', 'sl5-3');");
        await AdminAsync($"UPDATE {Quoted(schema)}.events SET leaf_hash = decode(repeat('ab', 32), 'hex') WHERE event_id = 'sl5-4';");
        await using var backfill = new PostgresLeafBackfill(fixture.AdminDataSource, schema);

        var first = Require(await backfill.RunAsync(Ct));
        var second = Require(await backfill.RunAsync(Ct));
        var findings = Require(StoredLeafAudit.Check(Require(await store.ReadAllAsync(Ct))));

        Assert.Equal((2L, 3L), (first.Filled, first.AlreadyHeld));
        Assert.Equal((0L, 5L), (second.Filled, second.AlreadyHeld));
        Assert.Empty(findings.Missing);
        Assert.Equal([4L], findings.Mismatched);
    }

    /// <summary>SL5b: the backfill needs the owner's role; as the application role it is refused before it reads a row, and it changes nothing.</summary>
    [Fact]
    public async Task R6_69_SL5b_TheBackfillAsTheApplicationRoleIsRefusedAndChangesNothing()
    {
        var (store, schema, _) = await NewStoreAsync(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        await AppendOneAsync(store, "agg-sl5b", 0, Plain("sl5b"));
        await AdminAsync($"UPDATE {Quoted(schema)}.events SET leaf_hash = NULL;");

        await using var asTheApp = new PostgresLeafBackfill(fixture.AppRoleDataSource, schema);
        var refused = await asTheApp.RunAsync(Ct);

        Assert.False(refused.IsOk);
        Assert.Equal("curia/backfill/not-the-owner", refused.Match(_ => "", e => e.Type));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(leaf_hash) FROM {Quoted(schema)}.events;"));
    }

    // ---- SL6 ----

    /// <summary>SL6: the application role is refused an UPDATE of the column (42501), in both forms, and a leaf of the wrong length (the table's own CHECK).</summary>
    [Fact]
    public async Task R6_69_SL6_TheApplicationRoleCannotUpdateTheLeafAndCannotWriteOneOfTheWrongLength()
    {
        var (store, schema, _) = await NewStoreAsync(new DateTimeOffset(2026, 11, 27, 10, 0, 0, TimeSpan.Zero));
        await AppendOneAsync(store, "agg-sl6", 0, Plain("sl6"));
        var table = Quoted(schema) + ".events";
        var problems = new List<string>();
        await using var app = await fixture.AppRoleDataSource.OpenConnectionAsync(Ct);

        foreach (var (name, sql) in new[]
        {
            ("UPDATE of leaf_hash", $"UPDATE {table} SET leaf_hash = decode(repeat('00', 32), 'hex');"),
            ("UPDATE of leaf_hash to NULL", $"UPDATE {table} SET leaf_hash = NULL;"),
            ("DELETE", $"DELETE FROM {table};"),
        })
        {
            try
            {
                await using var command = new NpgsqlCommand(sql, app);
                await command.ExecuteNonQueryAsync(Ct);
                problems.Add(name + " was accepted");
            }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { }
        }

        try
        {
            await using var command = new NpgsqlCommand($"INSERT INTO {table} (event_id, event_type, aggregate_id, payload, server_ts, leaf_hash) VALUES ('sl6-short', 'test.event', 'agg-sl6b', '{{}}', now(), decode('abcd', 'hex'));", app);
            await command.ExecuteNonQueryAsync(Ct);
            problems.Add("a 2-byte leaf was accepted");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.CheckViolation) { }

        Assert.True(problems.Count == 0, string.Join(" | ", problems));
    }

    // ---- SL7 ----

    /// <summary>SL7: in the fixture's database (every migration through the production renderer) the role holds INSERT on <c>leaf_hash</c> and no UPDATE on any column, and the covering index answers a leaf read from the index alone.</summary>
    [Fact]
    public async Task R6_69_SL7_TheGrantIsInsertOnTheColumnAndTheCoveringIndexServesTheLeafRead()
    {
        var role = fixture.RoleName;
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using var privileges = new NpgsqlCommand(
            "SELECT has_column_privilege(@r, 'events', 'leaf_hash', 'INSERT'), has_column_privilege(@r, 'events', 'leaf_hash', 'UPDATE'), has_any_column_privilege(@r, 'events', 'UPDATE'), has_column_privilege(@r, 'events', 'seq', 'INSERT');", admin);
        privileges.Parameters.AddWithValue("r", role);
        await using var reader = await privileges.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal((true, false, false, false), (reader.GetBoolean(0), reader.GetBoolean(1), reader.GetBoolean(2), reader.GetBoolean(3)));
        await reader.CloseAsync();

        await using var plan = new NpgsqlCommand("SET enable_seqscan = off; SET enable_bitmapscan = off; EXPLAIN SELECT seq, leaf_hash FROM events WHERE seq > 0 ORDER BY seq;", admin);
        var lines = new List<string>();
        await using (var planReader = await plan.ExecuteReaderAsync(Ct))
            while (await planReader.ReadAsync(Ct)) lines.Add(planReader.GetString(0));
        await using (var reset = new NpgsqlCommand("RESET enable_seqscan; RESET enable_bitmapscan;", admin)) await reset.ExecuteNonQueryAsync(Ct);   // the connection returns to the pool
        Assert.Contains(lines, l => l.Contains("Index Only Scan", StringComparison.Ordinal) && l.Contains("events_seq_leaf_hash", StringComparison.Ordinal));
    }

    // ---- helpers ----

    private static string Quoted(string schema) => "\"" + schema + "\"";

    private async Task AdminAsync(string sql)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync(Ct))!;
    }
}
