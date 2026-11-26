using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Infrastructure.Migrations;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// DPK6a to DPK6c (errata G83; amendment C6): the restore post-check of <c>docs/runbooks/backup-restore.md</c> is its fenced <c>sql</c> blocks, extracted by this test and run on throwaway databases. The first block is
/// five rows of <c>name, got, want</c> over the application role's grants, which are <b>column-level</b> under <c>db/0006</c>: <c>has_table_privilege(..., 'INSERT')</c> is false with and without the migration, so
/// the rows that decide are the column ones and <c>has_any_column_privilege</c> for <c>UPDATE</c>, and two rows are positive controls (a role stripped of every grant would otherwise read as narrowed).
/// The second block is the <c>db/0002</c> tables. The role is the literal <c>curia_app</c>, replaced by one textual substitution (psql's variables would not run over Npgsql).
/// DPK6a: the migrations as built give <c>got = want</c> on every row. DPK6b: without <c>db/0006</c> the rows <c>insert on events, whole table</c> and <c>insert on events.seq</c> are red. DPK6c: a role created with
/// no grants reads red on the positive controls. The tables statement is true with <c>db/0002</c> and false without it.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit discovers a concrete test class only when it is public.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every identifier is generated here from a literal prefix and a GUID's hex digits; the SQL blocks are the runbook's checked-in text.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed partial class BackupPostCheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no repository root above " + AppContext.BaseDirectory);
    }

    private static List<string> SqlBlocks() =>
        SqlFence().Matches(File.ReadAllText(Path.Combine(RepoRoot(), "docs", "runbooks", "backup-restore.md"))).Select(m => m.Groups[1].Value).ToList();

    private sealed record Row(string Name, bool Got, bool Want);

    private static async Task<List<Row>> GrantRowsAsync(NpgsqlDataSource source, string block, string role)
    {
        var rows = new List<Row>();
        await using var command = source.CreateCommand(block.Replace("curia_app", role, StringComparison.Ordinal));
        await using var reader = await command.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) rows.Add(new Row(reader.GetString(0), reader.GetBoolean(1), reader.GetBoolean(2)));
        return rows;
    }

    private static async Task<bool> TablesAsync(NpgsqlDataSource source, string block)
    {
        await using var command = source.CreateCommand(block);
        return (bool)(await command.ExecuteScalarAsync(Ct))!;
    }

    private static async Task<T> WithDatabaseAsync<T>(IEnumerable<string> migrations, Func<NpgsqlDataSource, string, Task<T>> use)
    {
        var admin = PostgresTestEnvironment.ResolveAdminConnectionString();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var database = $"curia_dpk6_{suffix}";
        var role = $"curia_dpk6_{suffix}";
        await using var adminSource = new NpgsqlDataSourceBuilder(admin).Build();
        await using (var create = adminSource.CreateCommand($"CREATE DATABASE \"{database}\"")) await create.ExecuteNonQueryAsync(Ct);
        try
        {
            await using var target = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(admin) { Database = database }.ConnectionString).Build();
            foreach (var file in migrations)
            {
                await using var apply = target.CreateCommand(SchemaMigrations.Render(file, role, "pw-" + suffix));
                await apply.ExecuteNonQueryAsync(Ct);
            }

            return await use(target, role);
        }
        finally
        {
            await using (var drop = adminSource.CreateCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)")) await drop.ExecuteNonQueryAsync(CancellationToken.None);
            await using (var dropRole = adminSource.CreateCommand($"DROP ROLE IF EXISTS \"{role}\"")) await dropRole.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private static string Says(List<Row> rows) => string.Join("; ", rows.Select(r => $"{r.Name}: got {r.Got} want {r.Want}"));

    [Fact]
    public async Task G83_DPK6a_TheBlockReadsGreenOnTheMigrationsAsBuilt()
    {
        var blocks = SqlBlocks();
        Assert.True(blocks.Count >= 2, $"docs/runbooks/backup-restore.md holds {blocks.Count} fenced sql blocks, not the grants block and the tables statement");
        var (rows, tables) = await WithDatabaseAsync(SchemaMigrations.FileNames, async (db, role) => (await GrantRowsAsync(db, blocks[0], role), await TablesAsync(db, blocks[1])));
        var bad = rows.Where(r => r.Got != r.Want).ToList();
        Assert.True(rows.Count == 5 + 1 && bad.Count == 0 && tables, $"on the migrations as built every row should read got = want ({rows.Count} rows, expected 6) and the db/0002 tables should exist ({tables}): {Says(rows)}");
    }

    [Fact]
    public async Task G83_DPK6b_WithoutDb0006TheInsertRowsAreRed()
    {
        var blocks = SqlBlocks();
        Assert.True(blocks.Count >= 2, $"docs/runbooks/backup-restore.md holds {blocks.Count} fenced sql blocks");
        var rows = await WithDatabaseAsync(SchemaMigrations.FileNames.Where(f => f != SchemaMigrations.EventInsertColumnsFile), (db, role) => GrantRowsAsync(db, blocks[0], role));
        var red = rows.Where(r => r.Got != r.Want).Select(r => r.Name).ToList();
        Assert.True(red.Contains("insert on events, whole table") && red.Contains("insert on events.seq"), $"without db/0006 the whole-table and seq INSERT rows should read red; red rows: [{string.Join(", ", red)}]; {Says(rows)}");
    }

    [Fact]
    public async Task G83_DPK6c_ARoleWithNoGrantsReadsRedOnThePositiveControlsAndTheTablesStatementNeedsDb0002()
    {
        var blocks = SqlBlocks();
        Assert.True(blocks.Count >= 2, $"docs/runbooks/backup-restore.md holds {blocks.Count} fenced sql blocks");
        var rows = await WithDatabaseAsync(SchemaMigrations.FileNames, async (db, _) =>
        {
            var stripped = "curia_dpk6_stripped_" + Guid.NewGuid().ToString("N")[..8];
            await using (var create = db.CreateCommand($"CREATE ROLE \"{stripped}\"")) await create.ExecuteNonQueryAsync(Ct);
            try
            {
                return await GrantRowsAsync(db, blocks[0], stripped);
            }
            finally
            {
                await using var drop = db.CreateCommand($"DROP ROLE IF EXISTS \"{stripped}\"");
                await drop.ExecuteNonQueryAsync(CancellationToken.None);
            }
        });
        var red = rows.Where(r => r.Got != r.Want).Select(r => r.Name).ToList();
        var none = await WithDatabaseAsync([SchemaMigrations.FileNames[0]], (db, _) => TablesAsync(db, blocks[1]));
        Assert.True(
            red.Count == 2 && red.All(n => n.Contains("positive control", StringComparison.Ordinal) || n == "select on events.seq") && !none,
            $"a role with no grants should read red on exactly the two positive rows (got [{string.Join(", ", red)}]) and the tables statement should be false without db/0002 (got {none}): {Says(rows)}");
    }

    [GeneratedRegex("```sql\n(.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex SqlFence();
}
