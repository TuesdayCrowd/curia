using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Curia.Infrastructure.Migrations;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// LCB24 (errata G57, plan Task 0b): the fault harness's rehearsal as a test. The scripts of <c>tools/fault-harness</c> are run against a throwaway database and role this test creates
/// (<c>curia_lcb24_*</c> and <c>curia_lcb24_*_app</c>) and drops: refusals exit 2 with nothing touched, <c>status</c> exits 1 with <c>FAULT OPEN</c> while a hold is open and 0 after <c>all-off</c>, a
/// hold refuses another proxy mode and an unopened name, <c>landing.sh</c> prints <c>absent</c>, <c>latecommit.sh</c> refuses a non-loopback Forum and a hold under twice the command timeout, and
/// <c>forks.sh</c> exits 0 on a clean database (its body-duplicate section present) and 2 on a bad pair, and the digest drill is run through a stand-in client whose <c>acts</c> lists a kept digest and whose <c>resend</c> is called with it (G58). <b>Green on the seam</b> (Task 0b is committed before it): a control, red under FLCB32.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit discovers a concrete test class only when it is public.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Identifiers are generated here from a literal prefix and a GUID's hex digits.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class HarnessRehearsalTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public HarnessRehearsalTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static readonly string FakeDigest = "sha256:" + new string('a', 64);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Harness()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "fault-harness", "fault.sh"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tools", "fault-harness");
    }

    private static async Task<(int Exit, string Output)> RunAsync(string script, string[] args, IDictionary<string, string> env)
    {
        // The resolved string and not the data source's: Npgsql strips the password from a data source's ConnectionString, and CI's server requires one.
        var admin = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ResolveAdminConnectionString());
        var info = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(Path.Combine(Harness(), script));
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["PGHOST"] = admin.Host ?? "localhost";
        info.Environment["PGPORT"] = admin.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (admin.Username is { Length: > 0 } user) info.Environment["PGUSER"] = user;
        if (admin.Password is { Length: > 0 } password) info.Environment["PGPASSWORD"] = password;
        info.Environment["PGDATABASE"] = "postgres";
        foreach (var (k, v) in env) info.Environment[k] = v;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(Ct);
        var error = process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await output + await error);
    }

    [Fact]
    public async Task R6_60_LCB24_TheHarnessRehearses()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var db = "curia_lcb24_" + suffix;
        var role = db + "_app";
        var password = Guid.NewGuid().ToString("N");
        var state = Path.Combine(Path.GetTempPath(), "curia-lcb24-" + suffix);
        Directory.CreateDirectory(state);
        var said = new List<string>();
        Dictionary<string, string>? proxyEnv = null;
        try
        {
            await using (var admin = await _fixture.AdminDataSource.OpenConnectionAsync(Ct))
            {
                await using var create = new NpgsqlCommand($"CREATE DATABASE \"{db}\"", admin);
                await create.ExecuteNonQueryAsync(Ct);
            }

            var target = new NpgsqlConnectionStringBuilder(PostgresTestEnvironment.ResolveAdminConnectionString()) { Database = db };
            await using (var source = new NpgsqlDataSourceBuilder(target.ConnectionString).Build())
            await using (var apply = source.CreateCommand(SchemaMigrations.RenderAll(role, password)))
                await apply.ExecuteNonQueryAsync(Ct);

            var env = new Dictionary<string, string> { ["FH_DB"] = db, ["FH_ROLE"] = role, ["FH_DIR"] = state };
            async Task Check(string label, string script, string[] args, int exit, string? contains = null, IDictionary<string, string>? extra = null, string? absent = null)
            {
                var merged = new Dictionary<string, string>(env);
                if (extra is not null) foreach (var (k, v) in extra) merged[k] = v;
                var (code, output) = await RunAsync(script, args, merged);
                var ok = code == exit && (contains is null || output.Contains(contains, StringComparison.Ordinal)) && (absent is null || !output.Contains(absent, StringComparison.Ordinal));
                // A row that misses carries the script's last output, so a failure seen only in CI says why without a second run.
                var tail = output.Length > 1500 ? output[^1500..] : output;
                said.Add($"{label}: {(ok ? "as expected" : $"exit {code}, expected {exit}{(contains is null ? string.Empty : $" with '{contains}'")}{(absent is null ? string.Empty : $" without '{absent}'")}; output: {tail}")}");
            }

            await Check("hold-on 0", "fault.sh", ["hold-on", "0", "a"], 2);
            await Check("hold-on 901", "fault.sh", ["hold-on", "901", "a"], 2);
            await Check("hold-on -1", "fault.sh", ["hold-on", "-1", "a"], 2);
            await Check("hold-on, a bad name", "fault.sh", ["hold-on", "6", "Bad"], 2);
            await Check("hold-off with no hold", "fault.sh", ["hold-off", "x"], 2, "no hold is on");
            await Check("a role that is not the database's", "fault.sh", ["hold-on", "6", "a"], 2, "does not begin with", new Dictionary<string, string> { ["FH_ROLE"] = "postgres" });
            await Check("a curia_e2e_ database", "fault.sh", ["hold-on", "6", "a"], 2, "curia_e2e_", new Dictionary<string, string> { ["FH_DB"] = "curia_e2e_" + suffix });
            await Check("status at rest", "fault.sh", ["status"], 0);
            await Check("hold-on 6", "fault.sh", ["hold-on", "6", "t"], 0);
            await Check("status while a hold is open", "fault.sh", ["status"], 1, "FAULT OPEN");
            await Check("a second hold", "fault.sh", ["hold-on", "6", "t2"], 2, "exclusive");
            await Check("hold-off under another name", "fault.sh", ["hold-off", "wrong"], 2, "not opened under the name");
            await Check("hold-off", "fault.sh", ["hold-off", "t"], 0);
            await Check("status after hold-off", "fault.sh", ["status"], 0);
            await Check("hold-on then all-off", "fault.sh", ["hold-on", "6", "u"], 0);
            await Check("all-off", "fault.sh", ["all-off"], 0);
            await Check("landing.sh, a bad digest", "landing.sh", ["nonsense"], 2);
            await Check("landing.sh, an absent digest", "landing.sh", ["sha256:" + new string('0', 64)], 0, "absent", new Dictionary<string, string> { ["LAND_DEADLINE"] = "1" });
            await Check("forks.sh on a clean database", "forks.sh", [], 0, "ORACLE: none");
            await Check("forks.sh prints the body-duplicate section", "forks.sh", [], 0, "== bodies stored twice");
            await Check("forks.sh, a bad pair", "forks.sh", [], 2, "does not match", new Dictionary<string, string> { ["FH_DB"] = "postgres" });
            var drill = new Dictionary<string, string> { ["FH_AGENT"] = "a1", ["FH_POST"] = "P", ["FH_FORUM"] = "http://203.0.113.9:5000" };
            await Check("latecommit.sh, a non-loopback Forum", "latecommit.sh", ["revision"], 2, "not a loopback", drill);
            drill["FH_FORUM"] = "http://127.0.0.1:5000";
            drill["FH_HOLD"] = "3";
            await Check("latecommit.sh, a hold under twice the timeout", "latecommit.sh", ["revision"], 2, "twice the command timeout", drill);

            // A drill that could not run is exit 2 and never reaches the oracle (review I1: a status overwritten after the heredoc read as a pass). The
            // client's `ask` keeps no act here, so `curia acts` lists none and the digest drill has nothing to resend; the preflight needs the proxy.
            if (OperatingSystem.IsWindows()) throw new Xunit.Sdk.XunitException("the harness is bash");
            var port = (20000 + (Math.Abs(suffix.GetHashCode(StringComparison.Ordinal)) % 20000)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            proxyEnv = new Dictionary<string, string>(env) { ["FH_PROXY_PORT"] = port };
            await Check("proxy start", "proxy.sh", ["start"], 0, "proxy up", proxyEnv);
            var nothingKept = Path.Combine(state, "nothing-kept.sh");
            await File.WriteAllTextAsync(nothingKept, "#!/bin/bash\nexit 1\n", Ct);
            File.SetUnixFileMode(nothingKept, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var failing = new Dictionary<string, string>(drill) { ["FH_CURIA"] = nothingKept, ["FH_PROXY_PORT"] = port, ["FH_HOLD"] = "6" };
            await Check("latecommit.sh digest, no act kept", "latecommit.sh", ["digest"], 2, "kept no act", failing, absent: "== oracle");

            // The digest drill through `curia resend`: a stand-in client whose `ask` keeps an act and waits, whose `acts` lists it and whose `resend` is answered
            // `already recorded`. The drill must read the digest from `acts`, send `resend <that digest>` and reach the oracle, whose body section is present and empty.
            var fakeLog = Path.Combine(state, "fake.log");
            var standIn = Path.Combine(state, "curia-standin.sh");
            await File.WriteAllTextAsync(standIn, "#!/bin/bash\necho \"$@\" >> \"$FAKE_LOG\"\ncase \"$1\" in\n  ask) touch \"$FAKE_KEPT\"; sleep 4; echo 'error: 503'; exit 8;;\n  acts) [ -f \"$FAKE_KEPT\" ] && echo \"" + FakeDigest + "  question  board meta  sent 1 time(s)  not answered  kept under a minute ago\"; exit 0;;\n  resend) echo 'already recorded'; exit 0;;\n  *) exit 0;;\nesac\n", Ct);
            File.SetUnixFileMode(standIn, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var resend = new Dictionary<string, string>(drill)
            {
                ["FH_CURIA"] = standIn, ["FH_PROXY_PORT"] = port, ["FH_HOLD"] = "6", ["FAKE_LOG"] = fakeLog, ["FAKE_KEPT"] = Path.Combine(state, "fake.kept"), ["LAND_DEADLINE"] = "2",
            };
            await Check("latecommit.sh digest, through curia resend", "latecommit.sh", ["digest"], 0, "ORACLE: none", resend);
            var calls = File.Exists(fakeLog) ? await File.ReadAllLinesAsync(fakeLog, Ct) : [];
            said.Add(calls.Any(l => l.StartsWith("resend " + FakeDigest + " ", StringComparison.Ordinal))
                ? "the drill sent curia resend of the digest curia acts listed: as expected"
                : $"the drill never sent curia resend of the listed digest (calls: {string.Join(" / ", calls)})");
            said.Add(calls.Count(l => l.StartsWith("ask ", StringComparison.Ordinal)) == 1 ? "A was one curia ask: as expected" : "A was not exactly one curia ask");
        }
        finally
        {
            if (proxyEnv is not null) await RunAsync("proxy.sh", ["stop"], proxyEnv);
            await using var admin = await _fixture.AdminDataSource.OpenConnectionAsync(CancellationToken.None);
            await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{db}\" WITH (FORCE)", admin)) await drop.ExecuteNonQueryAsync(CancellationToken.None);
            await using (var dropRole = new NpgsqlCommand($"DROP ROLE IF EXISTS \"{role}\"", admin)) await dropRole.ExecuteNonQueryAsync(CancellationToken.None);
            Directory.Delete(state, recursive: true);
        }

        var bad = said.Where(s => !s.EndsWith("as expected", StringComparison.Ordinal)).ToList();
        if (bad.Count > 0)
            throw new Xunit.Sdk.XunitException($"Expected: every rehearsal row as expected{Environment.NewLine}Actual:   {string.Join(" | ", bad)}");
    }
}
