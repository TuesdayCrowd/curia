using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Infrastructure.Migrations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// ASA35 (errata G58, plan Task 8, trap 138): the oracle for "the verb was run again". <c>tools/fault-harness/forks.sh</c> gains a body-duplicate section: the same author, kind, board and parent
/// with an identical <c>body</c>, read from the signed canonical text by a pattern (never from the Forum's answer and never from the digest, which differs between two runs of a verb). The probe seeds
/// a throwaway database by SQL with two <c>answer</c> events of one author whose canonical texts carry one body and two nonces (the positive control: the section returns the pair and the script
/// exits 1) and with a single event (a resend stores nothing: the section is empty and the script exits 0 on that section). Each section's heading is read from the script's output and not from a
/// list kept here. Predicted red: <c>section missing</c>. The connection string is the resolved one, with its password (trap 134).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit discovers a concrete test class only when it is public.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Identifiers are generated here from a literal prefix and a GUID's hex digits; every value is bound as a parameter.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed partial class ForksBodyDuplicateTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public ForksBodyDuplicateTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^== (?<heading>.*\bbod(?:y|ies)\b.*)$", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex BodySection();

    private static string Harness()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "fault-harness", "forks.sh"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tools", "fault-harness");
    }

    private static async Task<(int Exit, string Output)> ForksAsync(string db, string role, string state)
    {
        var admin = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ResolveAdminConnectionString());
        var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(Harness(), "forks.sh"));
        info.Environment["PGHOST"] = admin.Host ?? "localhost";
        info.Environment["PGPORT"] = admin.Port.ToString(CultureInfo.InvariantCulture);
        if (admin.Username is { Length: > 0 } user) info.Environment["PGUSER"] = user;
        if (admin.Password is { Length: > 0 } password) info.Environment["PGPASSWORD"] = password;
        info.Environment["PGDATABASE"] = "postgres";
        info.Environment["FH_DB"] = db;
        info.Environment["FH_ROLE"] = role;
        info.Environment["FH_DIR"] = state;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await output + await error);
    }

    private static string AnswerPayload(string postId, string nonce, string body) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["post_id"] = postId,
        ["canonical"] = $"{{\"author\":\"https://agents.example/asa35\",\"board\":\"asa35\",\"body\":\"{body}\",\"kind\":\"answer\",\"nonce\":\"{nonce}\",\"parent\":\"01PARENT0000000000000000AA\",\"v\":1}}",
        ["signature"] = "sig-" + nonce,
        ["digest"] = "sha256:" + new string(nonce[0], 64),
        ["author"] = "https://agents.example/asa35",
        ["board"] = "asa35",
        ["kind"] = "answer",
        ["parent"] = "01PARENT0000000000000000AA",
        ["server_ts"] = "2026-01-01T00:00:00.0000000+00:00",
        ["risk_flags"] = Array.Empty<object>(),
    });

    /// <summary>The section of <paramref name="output"/> that begins <c>== {heading}</c>, to the next heading, with its rows.</summary>
    private static (string Heading, string[] Rows)? SectionOf(string output)
    {
        var match = BodySection().Match(output);
        if (!match.Success) return null;
        var lines = output[(match.Index + match.Length)..].Split('\n');
        var rows = lines.TakeWhile(l => !l.StartsWith("== ", StringComparison.Ordinal) && !l.StartsWith("ORACLE", StringComparison.Ordinal)).Where(l => l.Length > 0).ToArray();
        return (match.Groups["heading"].Value, rows);
    }

    [Fact]
    public async Task R11_79_ASA35_TheBodyDuplicateSectionReturnsThePairAndNothingForAResend()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var db = "curia_asa35_" + suffix;
        var role = db + "_app";
        var state = Path.Combine(Path.GetTempPath(), "curia-asa35-" + suffix);
        Directory.CreateDirectory(state);
        var problems = new List<string>();
        try
        {
            await using (var admin = await _fixture.AdminDataSource.OpenConnectionAsync(Ct))
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{db}\"", admin);
                await create.ExecuteNonQueryAsync(Ct);
            }

            var target = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ResolveAdminConnectionString()) { Database = db };
            await using var source = new NpgsqlDataSourceBuilder(target.ConnectionString).Build();
            await using (var apply = source.CreateCommand(SchemaMigrations.RenderAll(role, Guid.NewGuid().ToString("N"))))
                await apply.ExecuteNonQueryAsync(Ct);

            async Task InsertAsync(string postId, string nonce, string body)
            {
                await using var insert = source.CreateCommand("INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts) VALUES (@id, 'post.accepted', @id, 'https://agents.example/asa35', @payload, now());");
                insert.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Text) { Value = postId });
                insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = AnswerPayload(postId, nonce, body) });
                await insert.ExecuteNonQueryAsync(Ct);
            }

            // a resend stores nothing: one event, and the section is empty
            await InsertAsync("01ASA35POST000000000000000A", "a1", "the same words");
            var one = await ForksAsync(db, role, state);
            if (!one.Output.Contains("ORACLE", StringComparison.Ordinal)) problems.Add($"forks.sh did not run to its verdict (exit {one.Exit}: {one.Output})");
            var oneSection = SectionOf(one.Output);
            if (oneSection is null) problems.Add("section missing: forks.sh prints no section for a body stored twice");
            else if (oneSection.Value.Rows.Any(r => r != "(none)")) problems.Add($"a single act is listed in \"{oneSection.Value.Heading}\": {string.Join(" / ", oneSection.Value.Rows)}");

            // the verb run again: two events with one body and two nonces, and the section returns the pair
            await InsertAsync("01ASA35POST000000000000000B", "b2", "the same words");
            var two = await ForksAsync(db, role, state);
            var twoSection = SectionOf(two.Output);
            if (twoSection is null) problems.Add("section missing: forks.sh prints no section for a body stored twice");
            else
            {
                if (!twoSection.Value.Rows.Any(r => r.Contains("01ASA35POST000000000000000A", StringComparison.Ordinal) && r.Contains("01ASA35POST000000000000000B", StringComparison.Ordinal)))
                    problems.Add($"the pair is not in \"{twoSection.Value.Heading}\": {string.Join(" / ", twoSection.Value.Rows)}");
                if (two.Exit != 1) problems.Add($"exit {two.Exit} with the pair seeded, expected 1");
            }
        }
        finally
        {
            await using var admin = await _fixture.AdminDataSource.OpenConnectionAsync(CancellationToken.None);
            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)", admin)) await drop.ExecuteNonQueryAsync(CancellationToken.None);
            await using (var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{role}\"", admin)) await dropRole.ExecuteNonQueryAsync(CancellationToken.None);
            Directory.Delete(state, recursive: true);
        }

        if (problems.Count > 0)
            throw new Xunit.Sdk.XunitException($"Expected: a body-duplicate section that is empty for one act and holds the pair for the verb run again, exit 1{Environment.NewLine}Actual:   {string.Join("; ", problems.Distinct(StringComparer.Ordinal))}");
    }
}
