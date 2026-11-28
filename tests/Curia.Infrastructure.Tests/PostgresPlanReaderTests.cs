using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Application.Reads;
using Curia.Application.Tests;
using Curia.Application.Tests.Reads;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// The Postgres answer to a read plan (errata G87, R11.113) held to the reference semantics and to the planner: RC1 (ii) and RC12 (the adapter's rows and indices are the reference's, over a log with burnt
/// identity values), the whole-log decisions over the same database (the equivalence oracle), RC4 (one snapshot for every stage), RC5 (a fault is thrown, never an empty slice), RC13 (every clause is served
/// by the index it names, read from the planner's own plan).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit discovers a concrete test class only when it is public.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every interpolated identifier is a schema name this fixture generated from a GUID's hex digits.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresPlanReaderTests(PostgresDatabaseFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} {e.Detail}"));

    private async Task<(PostgresEventStore Store, string Schema)> SceneAsync(int seed, bool burn)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(Ct);
        var store = new PostgresEventStore(fixture.AppRoleDataSource, TimeProvider.System, fixture.PermissiveLifecycleGate(), schema);

        // An identity value burnt by a rolled-back insert: seq has a gap, and a log index is not seq - 1.
        async Task Burn()
        {
            await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
            await using var command = new NpgsqlCommand($"SELECT nextval(pg_get_serial_sequence('\"{schema}\".events', 'seq'));", connection);
            await command.ExecuteScalarAsync(Ct);
        }

        await ReadScene.BuildAsync(store, seed, burn ? Burn : null, Ct);
        return (store, schema);
    }

    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public async Task RC1_ii_RC12_TheAdapterReturnsTheReferencesRowsAndIndicesOverALogWithBurntValues(int seed)
    {
        var (store, _) = await SceneAsync(seed, burn: true);
        var all = Require(await store.ReadAllAsync(Ct));
        Assert.True(all[^1].Seq.Value > all.Count, "the scene burnt no identity value: the index is never told apart from seq - 1");

        foreach (var name in ReadScene.AgentsOf(seed))
        foreach (var options in new[] { AgentReadOptions.Stream, AgentReadOptions.PostureOnly, AgentReadOptions.PostureAndFindings, new AgentReadOptions(Posture: true, Findings: true, Acceptances: true) })
        {
            var agent = Typed.Agent(name);
            var plan = AgentRead.PlanFor(agent, name, options);
            var expected = WholeLogPlanReader.Answer(all, plan);
            var actual = await store.ReadAsync(plan, Ct);

            Assert.Equal(
                expected.Rows.Select(r => $"{r.LogIndex}:{r.Event.Event.Id.Value}:{r.Event.Seq.Value}"),
                actual.Rows.Select(r => $"{r.LogIndex}:{r.Event.Event.Id.Value}:{r.Event.Seq.Value}"));
            Assert.Equal(expected.HighWater, actual.HighWater);
        }
    }

    /// <summary>
    /// RC16 (ii) (R11.6 (add.)): the signal clause reads <c>events.signal_target</c>, a column the append wrote from the signed envelope, and the reference reader reads the canonical text itself: two artifacts, so
    /// that a column the append wrote wrongly (NULL, or another post's digest) is a difference here and not a restatement. Over every digest the scene's signals name, the adapter returns the reference's rows, and
    /// the comparison is not vacuous: the scene holds signals, and the clause finds them.
    /// </summary>
    [Theory]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public async Task RC16_ii_TheSignalClauseOverTheColumnReturnsTheRowsTheCanonicalNames(int seed)
    {
        var (store, _) = await SceneAsync(seed, burn: true);
        var all = Require(await store.ReadAllAsync(Ct));
        var targets = all.Select(r => SignalTargetColumn.Of(r.Event)).Where(t => t is not null).Select(t => t!).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(targets.Length > 0, "the scene holds no vote or verification, so the comparison proves nothing");

        var plan = ReadPlan.Of(ReadClause.SignalsOn(targets));
        var expected = WholeLogPlanReader.Answer(all, plan);
        var actual = await store.ReadAsync(plan, Ct);

        Assert.NotEmpty(actual.Rows);
        Assert.Equal(expected.Rows.Select(r => r.Event.Event.Id.Value), actual.Rows.Select(r => r.Event.Event.Id.Value));
        Assert.Equal(all.Count(r => SignalTargetColumn.Of(r.Event) is not null), actual.Rows.Count);
    }

    /// <summary>
    /// RC1 (errata G87): the <c>Keys</c> clause reads the three published-key types and no other, over the real database. A log of the scene's kind holds none of them, so a statement that left a class out would be
    /// green over it (FRC2b); each class is appended once, and the clause returns them with the log index each stands at.
    /// </summary>
    [Fact]
    public async Task RC1_TheKeysClauseReadsAllThreeKeyTypesAndNothingElse()
    {
        var (store, _) = await SceneAsync(14, burn: false);
        var before = Require(await store.ReadAllAsync(Ct)).Count;
        foreach (var (type, id) in new[] { (Curia.Domain.Acta.LogEntries.KeyType, "01RC1KEYA0000000000000000Z"), (Curia.Domain.Acta.LogEntries.DeclarationKeyType, "01RC1KEYB0000000000000000Z"), (Curia.Domain.Acta.LogEntries.OperatorKeyType, "01RC1KEYC0000000000000000Z") })
        {
            Require(await store.AppendAsync(
                Require(AggregateId.Create("rc1-" + type)),
                AggregateVersion.New,
                [new DomainEvent(Require(EventId.Create(id)), Require(EventType.Create(type)), Actor: null, Payload: new Curia.Canon.Json.JsonValue.Object([]))],
                Ct));
        }

        var slice = await store.ReadAsync(ReadPlan.Of(ReadClause.Keys()), Ct);

        // The scene's own acts published an operator key; the three appended here stand at the end. The expectation is read from the whole log, by type, and not from the clause.
        var all = Require(await store.ReadAllAsync(Ct));
        var expected = all
            .Select((e, i) => (Index: i, Type: e.Event.Type.Value))
            .Where(r => r.Type is Curia.Domain.Acta.LogEntries.KeyType or Curia.Domain.Acta.LogEntries.DeclarationKeyType or Curia.Domain.Acta.LogEntries.OperatorKeyType)
            .Select(r => $"{r.Index}:{r.Type}")
            .ToArray();
        Assert.True(expected.Length >= 4 && all.Count == before + 3, "the three key rows were appended after the scene's own");
        Assert.Equal(expected, slice.Rows.Select(r => $"{r.LogIndex}:{r.Event.Event.Type.Value}"));
        Assert.Contains(slice.Rows, r => r.Event.Event.Type.Value == Curia.Domain.Acta.LogEntries.DeclarationKeyType);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    public async Task RC1_ii_ARequestsOwnReadDecidesWhatTheWholeLogFoldDecidesOverTheRealDatabase(int seed)
    {
        var (store, _) = await SceneAsync(seed, burn: true);

        var differences = await ReadScene.DifferencesAsync(store, store, seed, Ct);

        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }

    /// <summary>
    /// RC4: every stage of a plan reads one snapshot. A row appended between stage 1 and stage 3 (here, by the stage function itself, through another connection) is in none of the stages' reads, and a read
    /// taken after it holds it. Red against one transaction per stage (FRC20).
    /// </summary>
    [Fact]
    public async Task RC4_ARowAppendedBetweenStagesIsInNoneOfThem()
    {
        var (store, _) = await SceneAsync(31, burn: false);
        var agent = ReadScene.AgentsOf(31)[0];
        var before = Require(await store.ReadAllAsync(Ct)).Count;
        var appendedId = "01RC4" + new string('Z', 21);
        var seen = new List<int>();

        var plan = ReadPlan.Staged(
            _ => [ReadClause.Aggregates([agent])],
            rows =>
            {
                seen.Add(rows.Count);
                // Between the statements of the one read, another connection appends to the agent's stream.
                var version = Require(AggregateVersion.From(rows.Count(r => r.AggregateId.Value == agent)));
                Require(store.AppendAsync(
                    Require(AggregateId.Create(agent)),
                    version,
                    [new DomainEvent(Require(EventId.Create(appendedId)), Require(EventType.Create("test.event")), Actor: null, Payload: new Curia.Canon.Json.JsonValue.Object([]))],
                    Ct).GetAwaiter().GetResult());
                return [ReadClause.Aggregates([agent])];
            },
            rows =>
            {
                seen.Add(rows.Count);
                return [ReadClause.Aggregates([agent])];
            });

        var slice = await store.ReadAsync(plan, Ct);

        Assert.DoesNotContain(slice.Rows, r => r.Event.Event.Id.Value == appendedId);
        Assert.Equal(seen[0], seen[1]);
        Assert.Equal(before, (long)slice.HighWater.Value > 0 ? Require(await store.ReadAllAsync(Ct)).Count - 1 : -1);
        var fresh = await store.ReadAsync(ReadPlan.Of(ReadClause.Aggregates([agent])), Ct);
        Assert.Contains(fresh.Rows, r => r.Event.Event.Id.Value == appendedId);
    }

    /// <summary>RC5: a store that cannot be reached throws (R11.71), and no slice, empty or otherwise, is returned for it.</summary>
    [Fact]
    public async Task RC5_AFaultIsThrownAndNeverAnEmptySlice()
    {
        var (_, schema) = await SceneAsync(41, burn: false);
        await using var unreachable = NpgsqlDataSource.Create("Host=localhost;Port=1;Username=nobody;Database=postgres;Timeout=1;Command Timeout=1");
        var store = new PostgresEventStore(unreachable, TimeProvider.System, fixture.PermissiveLifecycleGate(), schema);

        await Assert.ThrowsAsync<LogUnreadableException>(() => store.ReadAsync(ReadPlan.Of(ReadClause.Keys()), Ct));
    }

    /// <summary>RC5, the second scene: the application role's SELECT is revoked, so the first statement is refused and the read throws.</summary>
    [Fact]
    public async Task RC5_ARevokedSelectIsAFaultAndNotAnEmptyRead()
    {
        var (store, schema) = await SceneAsync(42, burn: false);
        await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct))
        await using (var revoke = new NpgsqlCommand($"REVOKE SELECT ON \"{schema}\".events FROM \"{fixture.RoleName}\";", connection))
            await revoke.ExecuteNonQueryAsync(Ct);

        await Assert.ThrowsAsync<LogUnreadableException>(() => store.ReadAsync(ReadPlan.Of(ReadClause.Keys()), Ct));
    }

    /// <summary>
    /// RC13: each clause's statement, planned over a log of thirteen thousand events with sequential scans disabled, reads an index and names the one its clause says. Dropping the index turns the probe red
    /// (the plan then names none), which is the silence this probe exists for.
    /// </summary>
    [Fact]
    public async Task RC13_EveryClauseIsServedByTheIndexItNames()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(Ct);
        await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct))
        await using (var fill = new NpgsqlCommand(
            $$"""
            INSERT INTO "{{schema}}".events (event_id, event_type, aggregate_id, actor_id, payload, signal_target)
            SELECT 'e' || g, CASE WHEN g % 3 = 0 THEN 'post.accepted' WHEN g % 3 = 1 THEN 'agent.enrolled' ELSE 'log.key' END,
                   'agg' || (g % 4000), 'actor' || (g % 50),
                   CASE WHEN g % 3 = 0 THEN jsonb_build_object('digest', 'sha256:' || g, 'kind', CASE WHEN g % 9 = 0 THEN 'vote' ELSE 'question' END, 'parent', NULL, 'board', 'b',
                        'canonical', '{"target":"sha256:' || (g % 500) || '"}')
                        ELSE jsonb_build_object('kid', 'k' || g) END,
                   CASE WHEN g % 9 = 0 THEN 'sha256:' || lpad((g % 500)::text, 64, '0') ELSE NULL END
            FROM generate_series(1, 13000) g;
            ANALYZE "{{schema}}".events;
            """, connection))
            await fill.ExecuteNonQueryAsync(Ct);

        var table = $"\"{schema}\".events";
        (ReadClause Clause, string Index)[] clauses =
        [
            (ReadClause.Aggregates(["agg1", "agg2"]), "aggregate_id"),
            (ReadClause.Keys(), "event_type"),
            (ReadClause.PostsBy("actor3"), "event_type"),
            (ReadClause.AcceptancesBy("actor3"), "event_type"),
            (ReadClause.PostsWithDigest(["sha256:3", "sha256:6"]), "events_post_digest"),
            (ReadClause.SignalsOn(["sha256:4", "sha256:9"]), "events_post_signal_target"),
            (ReadClause.Entries(["e3", "e6"]), "event_id"),
            (ReadClause.LegacyFlags(), "event_type"),
            (ReadClause.LifecycleNaming("k7"), "event_type"),
        ];

        var report = new List<string>();
        foreach (var (clause, index) in clauses)
        {
            var (sql, parameter) = PostgresPlanReader.Statement(clause, table, "seq");
            var plan = await PlanOfAsync(sql, parameter, usesTypes: sql.Contains("@types", StringComparison.Ordinal));
            if (plan.Contains("\"Seq Scan\"", StringComparison.Ordinal)) report.Add($"{sql}: a sequential scan");
            if (!plan.Contains(index, StringComparison.Ordinal)) report.Add($"{sql}: names no {index}");
        }

        Assert.True(report.Count == 0, string.Join(Environment.NewLine, report));

        // The probe is red with the index gone: the plan names none.
        await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct))
        await using (var drop = new NpgsqlCommand($"DROP INDEX \"{schema}\".events_post_digest;", connection))
            await drop.ExecuteNonQueryAsync(Ct);
        var (digestSql, digestParameter) = PostgresPlanReader.Statement(ReadClause.PostsWithDigest(["sha256:3"]), table, "seq");
        Assert.DoesNotContain("events_post_digest", await PlanOfAsync(digestSql, digestParameter, usesTypes: false), StringComparison.Ordinal);

        // And for the signal clause: the same statement, planned with its index gone, names no events_post_signal_target (RC13 stays red with the index dropped).
        await using (var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct))
        await using (var drop = new NpgsqlCommand($"DROP INDEX \"{schema}\".events_post_signal_target;", connection))
            await drop.ExecuteNonQueryAsync(Ct);
        var (signalSql, signalParameter) = PostgresPlanReader.Statement(ReadClause.SignalsOn(["sha256:4"]), table, "seq");
        Assert.DoesNotContain("events_post_signal_target", await PlanOfAsync(signalSql, signalParameter, usesTypes: false), StringComparison.Ordinal);
    }

    private async Task<string> PlanOfAsync(string sql, NpgsqlParameter? parameter, bool usesTypes)
    {
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        await using (var off = new NpgsqlCommand("SET enable_seqscan = off;", connection))
            await off.ExecuteNonQueryAsync(Ct);

        await using var command = new NpgsqlCommand("EXPLAIN (FORMAT JSON) " + sql.TrimEnd(';'), connection);
        if (parameter is not null) command.Parameters.Add(new NpgsqlParameter(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value });
        if (usesTypes) command.Parameters.Add(new NpgsqlParameter("types", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text) { Value = PostgresPlanReader.LifecycleTypes });
        return (string)(await command.ExecuteScalarAsync(Ct))!;
    }
}
