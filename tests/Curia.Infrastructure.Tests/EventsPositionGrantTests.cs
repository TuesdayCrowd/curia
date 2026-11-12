using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Infrastructure.Migrations;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// Errata G69, R11.6 (add.): an event's position in the log is the database's. SEQ1 connects AS the application role and names <c>seq</c> in four forms, each refused
/// <c>42501</c> by the grant; SEQ2 reads the catalogue against the six column names parsed from the errata (a different artifact from the migration); SEQ3 applies
/// <c>db/0006</c> twice; SEQ4 holds the isolated schema's grant to the same set; SEQ8 runs the pre-check and post-check text the README carries, on a database that holds
/// planted rows; SEQ10 is the control that an append through the store still takes the database's value.
///
/// <para>The form that is not a probe: an <c>INSERT</c> naming <c>seq</c> without <c>OVERRIDING SYSTEM VALUE</c> is refused by the identity rule (<c>428C9</c>) before
/// and after the migration, so it proves nothing about the grant and is in the file as a named control (finding 10).</para>
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit discovers a concrete test class only when it is public.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every interpolated identifier is generated here from a literal prefix and a GUID's hex digits, or is a fixed statement; the README's text is a checked-in file.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class EventsPositionGrantTests(PostgresDatabaseFixture fixture)
{
    private const string MigrationFile = "0006_events_insert_columns.sql";
    private const string RolePlaceholder = "__CURIA_APP_ROLE__";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- SEQ1 ----

    /// <summary>SEQ1: four statements that name <c>seq</c>, each refused by the grant; a normal append and its <c>RETURNING seq</c> still work; the event count does not move.</summary>
    [Fact]
    public async Task R11_6_SEQ1_TheRoleCannotNameSeq()
    {
        await fixture.ResetEventsTableAsync(Ct);
        var problems = new List<string>();
        try
        {
            await using var app = await fixture.AppRoleDataSource.OpenConnectionAsync(Ct);

            // The positive control, in the same test, so the refusals below cannot be those of a role that lost all access.
            var assigned = await ScalarAsync(app, "INSERT INTO events (event_id, event_type, aggregate_id, payload, server_ts) VALUES ('seq1-control', 'test.event', 'agg-seq1', '{}', now()) RETURNING seq;");
            if (!Equals(assigned, 1L)) problems.Add($"the positive control: expected the database's value 1 back through RETURNING seq, actual {assigned ?? "nothing"}");

            problems.AddRange(await RefusalsAsync(app, [
                ("OVERRIDING SYSTEM VALUE at -6", "INSERT INTO events (seq, event_id, event_type, aggregate_id, payload, server_ts) OVERRIDING SYSTEM VALUE VALUES (-6, 'seq1-neg', 'test.event', 'agg-seq1', '{}', now());"),
                ("OVERRIDING SYSTEM VALUE at 0", "INSERT INTO events (seq, event_id, event_type, aggregate_id, payload, server_ts) OVERRIDING SYSTEM VALUE VALUES (0, 'seq1-zero', 'test.event', 'agg-seq1', '{}', now());"),
                ("OVERRIDING SYSTEM VALUE at a positive value", "INSERT INTO events (seq, event_id, event_type, aggregate_id, payload, server_ts) OVERRIDING SYSTEM VALUE VALUES (987654, 'seq1-ahead', 'test.event', 'agg-seq1', '{}', now());"),
                ("seq = DEFAULT", "INSERT INTO events (seq, event_id, event_type, aggregate_id, payload, server_ts) VALUES (DEFAULT, 'seq1-default', 'test.event', 'agg-seq1', '{}', now());"),
                ("UPDATE", "UPDATE events SET event_type = 'tampered' WHERE event_id = 'seq1-control';"),
                ("DELETE", "DELETE FROM events WHERE event_id = 'seq1-control';"),
            ]));

            var count = await ScalarAsync(app, "SELECT count(*) FROM events;");
            if (!Equals(count, 1L)) problems.Add($"the event count: expected 1 (the control only), actual {count}");
        }
        finally
        {
            await fixture.ResetEventsTableAsync(CancellationToken.None);
        }

        Assert.True(problems.Count == 0, $"Expected: every form refused 42501 'permission denied for table events'{Environment.NewLine}Actual:   {string.Join(" | ", problems)}");
    }

    /// <summary>
    /// SEQ1's named control, NOT a probe of the grant: without <c>OVERRIDING SYSTEM VALUE</c> a non-DEFAULT <c>seq</c> is refused by the identity rule (<c>428C9</c>), which
    /// is not a privilege, so this passes with or without <c>db/0006</c> (finding 10; trap 246). It is here so that nobody counts it as the closure.
    /// </summary>
    [Fact]
    public async Task R11_6_SEQ1_Control_TheIdentityRuleRefusesWithOrWithoutTheGrant()
    {
        await fixture.ResetEventsTableAsync(Ct);
        await using var app = await fixture.AppRoleDataSource.OpenConnectionAsync(Ct);
        await using var insert = new NpgsqlCommand("INSERT INTO events (seq, event_id, event_type, aggregate_id, payload, server_ts) VALUES (5, 'seq1-identity', 'test.event', 'agg-seq1', '{}', now());", app);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(Ct));

        Assert.Equal("428C9", ex.SqlState);
    }

    // ---- SEQ2 ----

    /// <summary>SEQ2: the columns the role may <c>INSERT</c> are the six the errata names, and <c>seq</c> is not among them; table-level <c>SELECT</c> only, no other table privilege.</summary>
    [Fact]
    public async Task R11_6_SEQ2_TheCatalogueEqualsTheErrata()
    {
        var expected = ErrataColumns();

        var granted = await ReadGrantsAsync("public.events");

        Assert.Equal(expected, granted.Insertable);
        Assert.DoesNotContain("seq", granted.Insertable);
        Assert.Equal(["SELECT"], granted.TablePrivileges);
    }

    // ---- SEQ3 ----

    /// <summary>SEQ3: the migration exists, and applying it twice raises nothing and leaves the catalogue as it was.</summary>
    [Fact]
    public async Task R11_6_SEQ3_TheMigrationIsIdempotent()
    {
        var path = Path.Combine(RepoRoot(), "db", MigrationFile);
        Assert.True(File.Exists(path), $"db/{MigrationFile} does not exist");
        var rendered = (await File.ReadAllTextAsync(path, Ct)).Replace(RolePlaceholder, fixture.RoleName, StringComparison.Ordinal);
        var before = await ReadGrantsAsync("public.events");

        for (var i = 0; i < 2; i++)
        {
            await using var admin = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
            await using var apply = new NpgsqlCommand(rendered, admin);
            await apply.ExecuteNonQueryAsync(Ct);
        }

        var after = await ReadGrantsAsync("public.events");
        Assert.Equal(before.Insertable, after.Insertable);
        Assert.Equal(before.TablePrivileges, after.TablePrivileges);
    }

    // ---- SEQ4 ----

    /// <summary>SEQ4: the isolated schema the contract tests use holds the errata's six columns, and the same set as <c>public</c>: a hand-typed copy of the grant that drifts fails here.</summary>
    [Fact]
    public async Task R11_6_SEQ4_TheIsolatedSchemaGrantIsTheMigrationsGrant()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(Ct);
        var expected = ErrataColumns();

        var isolated = await ReadGrantsAsync($"\"{schema}\".events");
        var published = await ReadGrantsAsync("public.events");

        Assert.Equal(expected, isolated.Insertable);
        Assert.Equal(published.Insertable, isolated.Insertable);
        Assert.Equal(published.TablePrivileges, isolated.TablePrivileges);
    }

    // ---- SEQ8 ----

    /// <summary>
    /// SEQ8: the pre-check and the post-check are parsed out of the README, between their markers, and run: against a database that has not applied <c>db/0006</c> and holds a
    /// row at 0, one at -3 and one ahead of the identity the pre-check reports 2 and 1 and the post-check says <c>seq</c> is writable; against a clean one it reports 0 and 0;
    /// after the apply the post-check says <c>seq</c> is not writable and <c>server_ts</c> is.
    /// </summary>
    [Fact]
    public async Task R11_6_SEQ8_TheOwnersTextIsRun()
    {
        var readme = await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "README.md"), Ct);
        var precheck = ReadmeBlock(readme, "events-position-precheck");
        var postcheck = ReadmeBlock(readme, "events-position-postcheck");
        var apply = ReadmeBlock(readme, "events-position-apply");
        var count = ReadmeBlock(readme, "events-position-count");
        Assert.Contains(MigrationFile, apply, StringComparison.Ordinal);
        Assert.Contains("--single-transaction", apply, StringComparison.Ordinal);
        Assert.Contains("ON_ERROR_STOP", apply, StringComparison.Ordinal);

        var suffix = Guid.NewGuid().ToString("N")[..10];
        var databases = new[] { ($"curia_s8_{suffix}_p", $"curia_s8_{suffix}_pa"), ($"curia_s8_{suffix}_c", $"curia_s8_{suffix}_ca") };
        try
        {
            foreach (var (name, role) in databases) await CreatePreMigrationDatabaseAsync(name, role);

            await using (var planted = await OpenAsync(databases[0].Item1))
            {
                await ExecuteAsync(planted, "INSERT INTO events (event_id, event_type, aggregate_id, payload, server_ts) VALUES ('s8-a', 'test.event', 'agg', '{}', now()), ('s8-b', 'test.event', 'agg', '{}', now());");
                await ExecuteAsync(planted, "INSERT INTO events (seq, event_id, event_type, aggregate_id, payload, server_ts) OVERRIDING SYSTEM VALUE VALUES (0, 's8-zero', 'test.event', 'agg', '{}', now()), (-3, 's8-neg', 'test.event', 'agg', '{}', now()), (500, 's8-ahead', 'test.event', 'agg', '{}', now());");
                Assert.Equal((2L, 1L), await PreCheckAsync(planted, precheck));
                Assert.Equal((true, true), await PostCheckAsync(planted, postcheck, databases[0].Item2));
                Assert.Equal((5L, 500L), await CountAsync(planted, count));
                await ExecuteAsync(planted, (await File.ReadAllTextAsync(Path.Combine(RepoRoot(), "db", MigrationFile), Ct)).Replace(RolePlaceholder, databases[0].Item2, StringComparison.Ordinal));
                Assert.Equal((false, true), await PostCheckAsync(planted, postcheck, databases[0].Item2));
                Assert.Equal((5L, 500L), await CountAsync(planted, count));
            }

            await using var clean = await OpenAsync(databases[1].Item1);
            await ExecuteAsync(clean, "INSERT INTO events (event_id, event_type, aggregate_id, payload, server_ts) VALUES ('s8-c', 'test.event', 'agg', '{}', now());");
            Assert.Equal((0L, 0L), await PreCheckAsync(clean, precheck));
            Assert.Equal((1L, 1L), await CountAsync(clean, count));

            // The count text must be able to see a loss, or every assertion above would hold of a block that reads "SELECT 0, 0".
            // Test-only: the connection is the table's owner; the application role has no DELETE and no tree code deletes.
            await ExecuteAsync(clean, "DELETE FROM events;");
            Assert.Equal((0L, 0L), await CountAsync(clean, count));

            // This restates the README against itself; whether the sentence does its work is the cold read's question.
            var afterPostcheck = readme[(readme.IndexOf("<!-- /events-position-postcheck -->", StringComparison.Ordinal))..];
            var step3 = afterPostcheck[..afterPostcheck.IndexOf("The issuer key", StringComparison.Ordinal)];
            Assert.Contains("at least what you kept", step3, StringComparison.Ordinal);
            Assert.Contains("no row", step3, StringComparison.Ordinal);
        }
        finally
        {
            await using var admin = await fixture.AdminDataSource.OpenConnectionAsync(CancellationToken.None);
            foreach (var (name, _) in databases)
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }

            foreach (var (_, role) in databases)
            {
                await using var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{role}\"", admin);
                await dropRole.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    // ---- SEQ10 ----

    /// <summary>SEQ10, a control (green by design): an append through the store on an isolated schema, as the application role, takes the database's value, 1 and 2.</summary>
    [Fact]
    public async Task R11_6_SEQ10_Control_AnAppendThroughTheStoreTakesTheDatabasesValue()
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(Ct);
        var store = new PostgresEventStore(fixture.AppRoleDataSource, TimeProvider.System, fixture.PermissiveLifecycleGate(), schema);
        var aggregate = Require(AggregateId.Create("agg-seq10"));

        var first = Require(await store.AppendAsync(aggregate, AggregateVersion.New, [NewEvent("seq10-1")], Ct));
        var second = Require(await store.AppendAsync(Require(AggregateId.Create("agg-seq10-b")), AggregateVersion.New, [NewEvent("seq10-2")], Ct));

        Assert.Equal([1L, 2L], new[] { first[0].Seq.Value, second[0].Seq.Value });
    }

    // ---- helpers ----

    private async Task<(string[] Insertable, string[] TablePrivileges)> ReadGrantsAsync(string table)
    {
        var role = fixture.RoleName;
        await using var admin = await fixture.AdminDataSource.OpenConnectionAsync(Ct);
        var insertable = new List<string>();
        await using (var columns = new NpgsqlCommand(
            "SELECT a.attname FROM pg_attribute a WHERE a.attrelid = @table::regclass AND a.attnum > 0 AND NOT a.attisdropped AND has_column_privilege(@role, a.attrelid, a.attnum, 'INSERT') ORDER BY a.attname;", admin))
        {
            columns.Parameters.AddWithValue("table", table);
            columns.Parameters.AddWithValue("role", role);
            await using var reader = await columns.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct)) insertable.Add(reader.GetString(0));
        }

        var privileges = new List<string>();
        await using (var tableLevel = new NpgsqlCommand(
            "SELECT p FROM unnest(ARRAY['DELETE','INSERT','REFERENCES','SELECT','TRIGGER','TRUNCATE','UPDATE']) AS p WHERE has_table_privilege(@role, @table::regclass, p) ORDER BY p;", admin))
        {
            tableLevel.Parameters.AddWithValue("table", table);
            tableLevel.Parameters.AddWithValue("role", role);
            await using var reader = await tableLevel.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct)) privileges.Add(reader.GetString(0));
        }

        return ([.. insertable], [.. privileges]);
    }

    /// <summary>The six column names of R11.6 (add.)'s paragraph in the errata: the backticked names between "supplies," and "and SHALL NOT include", sorted.</summary>
    private static string[] ErrataColumns()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "curia-whitepaper-ERRATA-AND-ADDENDUM.md"));
        var start = text.IndexOf("**R11.6 (add.)**", StringComparison.Ordinal);
        Assert.True(start >= 0, "no R11.6 (add.) paragraph in the errata");
        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        var paragraph = text[start..(end < 0 ? text.Length : end)];
        var span = Regex.Match(paragraph, @"supplies,(?<names>.*?)and SHALL NOT include", RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        Assert.True(span.Success, "R11.6 (add.)'s paragraph does not list the columns between 'supplies,' and 'and SHALL NOT include'");
        var names = Regex.Matches(span.Groups["names"].Value, "`([a-z_]+)`", RegexOptions.None, TimeSpan.FromSeconds(5)).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(6, names.Length);
        return names;
    }

    /// <summary>The SQL of the fenced block between <c>&lt;!-- name --&gt;</c> and <c>&lt;!-- /name --&gt;</c>, with the fence and any shell prompt removed.</summary>
    private static string ReadmeBlock(string readme, string name)
    {
        var open = $"<!-- {name} -->";
        var close = $"<!-- /{name} -->";
        var start = readme.IndexOf(open, StringComparison.Ordinal);
        Assert.True(start >= 0, $"no {name} block in README.md");
        var end = readme.IndexOf(close, start, StringComparison.Ordinal);
        Assert.True(end > start, $"the {name} block in README.md is not closed");
        var body = readme[(start + open.Length)..end];
        var fence = Regex.Match(body, "```[a-z]*\n(?<code>.*?)```", RegexOptions.Singleline, TimeSpan.FromSeconds(5));
        Assert.True(fence.Success, $"the {name} block in README.md holds no fenced code");
        return fence.Groups["code"].Value;
    }

    private async Task CreatePreMigrationDatabaseAsync(string database, string role)
    {
        await using (var admin = await fixture.AdminDataSource.OpenConnectionAsync(Ct))
        {
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        // Every migration but 0006, which is the one the owner applies: the shape of a database that predates it.
        var script = string.Concat(SchemaMigrations.FileNames
            .Where(f => !string.Equals(f, MigrationFile, StringComparison.Ordinal))
            .Select(f => SchemaMigrations.Render(f, role, Guid.NewGuid().ToString("N")) + "\n\n"));
        await using var connection = await OpenAsync(database);
        await ExecuteAsync(connection, script);
    }

    private static async Task<NpgsqlConnection> OpenAsync(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ResolveAdminConnectionString()) { Database = database };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(CancellationToken.None);
        return connection;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private static async Task<(long AtOrBelowZero, long AheadOfTheIdentity)> PreCheckAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None), "the pre-check returned no row");
        return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    private static async Task<(long Events, long HighestSeq)> CountAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None), "the count text returned no row");
        return (Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture), Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture));
    }

    private static async Task<(bool SeqWritable, bool ServerTsWritable)> PostCheckAsync(NpgsqlConnection connection, string sql, string role)
    {
        // The README's text names the role `curia_app`; a deployment substitutes its own.
        await using var command = new NpgsqlCommand(sql.Replace("curia_app", role, StringComparison.Ordinal), connection);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None), "the post-check returned no row");
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    private static async Task<IEnumerable<string>> RefusalsAsync(NpgsqlConnection connection, (string Label, string Sql)[] statements)
    {
        var problems = new List<string>();
        foreach (var (label, sql) in statements)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            try
            {
                await command.ExecuteNonQueryAsync(Ct);
                problems.Add($"{label}: no exception was thrown (the statement was accepted)");
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InsufficientPrivilege && ex.MessageText.Contains("permission denied for table events", StringComparison.Ordinal))
            {
                // the refusal the grant makes
            }
            catch (PostgresException ex)
            {
                problems.Add($"{label}: expected 42501 'permission denied for table events', actual {ex.SqlState} '{ex.MessageText}'");
            }
        }

        return problems;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(Ct);
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static DomainEvent NewEvent(string id) => new(
        Require(EventId.Create(id)),
        Require(EventType.Create("test.event")),
        Actor: null,
        Payload: new JsonValue.Object([]));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repo root (a 'db' directory) above " + AppContext.BaseDirectory);
    }
}
