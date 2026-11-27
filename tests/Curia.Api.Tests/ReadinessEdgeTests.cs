using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// G86_RDY13 to G86_RDY20 (errata G86, R11.110 (add.)): readiness R12 (the log's size against the ceiling) and R13 (the enrolment edge observed from outside). The ceiling and the warning figure are read
/// from the errata and never from the script (RDY13, RDY14, RDY19); R13's probes run against fake upstreams that answer as a closed edge, as an open Forum, and as an edge that closes one spelling of the
/// route, so that a check that passes on any refusal, or on the lowercase spelling alone, is red.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each upstream is an await using declaration; the analyzer loses the ownership through the factory.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every identifier is generated here from a literal prefix and a GUID's hex digits.")]
public sealed partial class ReadinessScriptTests
{
    /// <summary>A server on a loopback port that answers as the test says: the Forum, or the edge, the readiness script is pointed at.</summary>
    private sealed class FakeUpstream : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private FakeUpstream(WebApplication app, int port)
        {
            _app = app;
            Port = port;
        }

        internal int Port { get; }

        internal string Origin => $"http://127.0.0.1:{Port}";

        internal static async Task<FakeUpstream> StartAsync(Func<HttpContext, Task> handler)
        {
            int port;
            using (var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
                port = ((IPEndPoint)probe.LocalEndPoint!).Port;
            }

            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, port));
            var app = builder.Build();
            app.Run(ctx => handler(ctx));
            await app.StartAsync();
            return new FakeUpstream(app, port);
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private static async Task Answer(HttpContext ctx, int status, string type)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/problem+json";
        await ctx.Response.WriteAsync($"{{\"type\":\"{type}\",\"title\":\"x\",\"status\":{status}}}", Ct);
    }

    /// <summary>The Forum's own answer to an enrolment body: <c>proof-required</c> to a well-formed one with no proof, <c>invalid</c> to a body that is not (Task 0 measured each; an empty body is <c>unreadable</c>).</summary>
    private static async Task ForumEnrolment(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var text = await reader.ReadToEndAsync(Ct);
        if (text.Length == 0)
        {
            await Answer(ctx, 400, "curia/request/unreadable");
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string? Member(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            var key = Member("public_key");
            var wellFormed = Member("agent_id") is { Length: > 0 } && Member("kid") is { Length: > 0 } && Member("alg") == "ES256" && key is not null && IsSpki(key);
            if (!wellFormed || root.TryGetProperty("proof", out _)) await Answer(ctx, 400, "curia/enroll/invalid");
            else await Answer(ctx, 400, "curia/enroll/proof-required");
        }
        catch (JsonException)
        {
            await Answer(ctx, 400, "curia/request/unreadable");
        }
    }

    private static bool IsSpki(string base64)
    {
        try
        {
            var der = Convert.FromBase64String(base64);
            return der.Length == 91 && der[0] == 0x30;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsEnrolmentPath(HttpContext ctx) => string.Equals(ctx.Request.Path.Value?.TrimEnd('/'), "/v1/agents", StringComparison.OrdinalIgnoreCase);

    private static Func<HttpContext, Task> ClosedEdge(Func<HttpContext, bool>? closes = null) => async ctx =>
    {
        if (IsEnrolmentPath(ctx) && (closes?.Invoke(ctx) ?? true)) await Answer(ctx, 403, PublishedEdge.EnrolmentClosedType());
        else if (IsEnrolmentPath(ctx)) await ForumEnrolment(ctx);
        else await Answer(ctx, 404, "curia/request/no-route");
    };

    private static async Task<string> CountDatabaseAsync(ForumFixture f, string name, int rows)
    {
        var admin = new NpgsqlConnectionStringBuilder(f.ConnectionString) { Database = "postgres" }.ConnectionString;
        await using var source = new NpgsqlDataSourceBuilder(admin).Build();
        await using (var create = source.CreateCommand($"CREATE DATABASE \"{name}\"")) await create.ExecuteNonQueryAsync(Ct);
        await using var target = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString).Build();
        await using var fill = target.CreateCommand($"CREATE TABLE events (seq bigint NOT NULL); INSERT INTO events SELECT g FROM generate_series(1, {rows}) AS g");
        await fill.ExecuteNonQueryAsync(Ct);
        return AdminUrl(f.ConnectionString, name);
    }

    private static async Task DropDatabaseAsync(ForumFixture f, string name)
    {
        var admin = new NpgsqlConnectionStringBuilder(f.ConnectionString) { Database = "postgres" }.ConnectionString;
        await using var source = new NpgsqlDataSourceBuilder(admin).Build();
        await using var drop = source.CreateCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
        await drop.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<Report> CountedAsync(int rows, params string[] extra)
    {
        var name = $"curia_rdy_n_{Guid.NewGuid():N}"[..28];
        try
        {
            var url = await CountDatabaseAsync(forum, name, rows);
            return await ReadinessAsync(["--origin", "http://127.0.0.1:1", "--only", "R12", "--db-admin-url", url, .. extra]);
        }
        finally
        {
            await DropDatabaseAsync(forum, name);
        }
    }

    /// <summary>RDY13 (R11.110 (add.)): a log of exactly the ceiling PASSes and one event more FAILs, the ceiling being the one the errata states (parsed, not typed), counted by <c>psql</c>.</summary>
    [Fact]
    public async Task G86_RDY13_TheCeilingIsTheErratasAndOneOverFails()
    {
        var (ceiling, _) = PublishedEdge.EventBand();
        var at = await CountedAsync(ceiling);
        var over = await CountedAsync(ceiling + 1);
        Assert.True(at.Status("R12") == "PASS" && over.Status("R12") == "FAIL" && over.Exit == 1,
            $"{ceiling} events / {ceiling + 1} events: R12 was {at.Status("R12")} / {over.Status("R12")} (exit {over.Exit}); {at.Raw} {over.Raw}");
    }

    /// <summary>RDY14: above the warning figure (the errata's) a PASS carries <c>warning:</c> in its reason; at the figure it does not; and the reason names no connection string.</summary>
    [Fact]
    public async Task G86_RDY14_AboveTheWarningFigureAPassSaysSo()
    {
        var (_, warn) = PublishedEdge.EventBand();
        var above = await CountedAsync(warn + 1);
        var at = await CountedAsync(warn);
        var reason = above.Checks.GetValueOrDefault("R12").Reason ?? string.Empty;
        Assert.True(above.Status("R12") == "PASS" && reason.StartsWith("warning:", StringComparison.Ordinal) && at.Status("R12") == "PASS" && !at.Checks["R12"].Reason.Contains("warning", StringComparison.Ordinal) && !reason.Contains("postgresql://", StringComparison.Ordinal),
            $"{warn + 1} events / {warn} events: R12 {above.Status("R12")} '{reason}' / {at.Status("R12")} '{at.Checks.GetValueOrDefault("R12").Reason}'");
    }

    /// <summary>RDY15: with no database URL and no head, or a database that cannot be read and no head to fall back on, R12 is NOT CHECKED and the exit is 8; never PASS (R11.110).</summary>
    [Fact]
    public async Task G86_RDY15_AnUnreadableSizeIsNotCheckedNeverPassed()
    {
        var none = await ReadinessAsync(["--origin", Origin(forum), "--only", "R12"]);
        var unreachable = await ReadinessAsync(["--origin", Origin(forum), "--only", "R12", "--db-admin-url", "postgresql://127.0.0.1:1/none"]);
        await using var head = await FakeUpstream.StartAsync(ctx => HeadOf(ctx, 100));
        var fellBack = await ReadinessAsync(["--origin", head.Origin, "--only", "R12", "--db-admin-url", "postgresql://127.0.0.1:1/none"]);
        Assert.True(none.Status("R12") == "NOT CHECKED" && none.Exit == 8 && unreachable.Status("R12") == "NOT CHECKED" && fellBack.Status("R12") == "NOT CHECKED",
            $"no source / an unreachable database with no head / an unreachable database with a head: R12 was {none.Status("R12")} (exit {none.Exit}) / {unreachable.Status("R12")} / {fellBack.Status("R12")}: a configured database that fails is never answered by the head\n{none.Raw}");
    }

    private static async Task HeadOf(HttpContext ctx, int treeSize)
    {
        if (ctx.Request.Path.Value != "/v1/log/head")
        {
            await Answer(ctx, 404, "curia/request/no-route");
            return;
        }

        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync($"{{\"head\":{{\"tree_size\":{treeSize},\"timestamp\":\"2026-01-01T00:00:00+00:00\"}}}}", Ct);
    }

    /// <summary>RDY16: the head's <c>tree_size</c> understates the log (the head's own row, and every row since), so it certifies only a size at or below the warning figure: 100 PASSes, one between the warning and the ceiling is NOT CHECKED, one above the ceiling FAILs.</summary>
    [Fact]
    public async Task G86_RDY16_ARowCountFromTheHeadCertifiesOnlyTheBandBelowTheWarning()
    {
        var (ceiling, warn) = PublishedEdge.EventBand();
        var states = new List<string>();
        foreach (var size in new[] { 100, warn, (warn + ceiling) / 2, ceiling, ceiling + 2000 })
        {
            await using var upstream = await FakeUpstream.StartAsync(ctx => HeadOf(ctx, size));
            var r = await ReadinessAsync(["--origin", upstream.Origin, "--only", "R12"]);
            states.Add($"{size}:{r.Status("R12")}");
        }

        Assert.True(string.Join(' ', states) == $"100:PASS {warn}:PASS {(warn + ceiling) / 2}:NOT CHECKED {ceiling}:NOT CHECKED {ceiling + 2000}:FAIL", $"R12 from the head's tree_size: {string.Join(' ', states)}");
    }

    private static async Task<Report> EnrolmentCheckAsync(Func<HttpContext, Task> upstreamBehaviour, params string[] extra)
    {
        await using var upstream = await FakeUpstream.StartAsync(upstreamBehaviour);
        return await ReadinessAsync(["--origin", upstream.Origin, "--only", "R13", .. extra]);
    }

    /// <summary>RDY17 (R11.110 (add.)), closed expected: an edge that closes both spellings PASSes; a Forum that answers the route FAILs; an edge that closes only the lowercase spelling FAILs; a <c>403</c> of another type and a <c>404</c> FAIL (not any refusal will do).</summary>
    [Fact]
    public async Task G86_RDY17_AClosedEdgeIsObservedInTwoSpellingsAndOnlyAClosedOneThere()
    {
        var closed = await EnrolmentCheckAsync(ClosedEdge());
        var open = await EnrolmentCheckAsync(ctx => IsEnrolmentPath(ctx) ? ForumEnrolment(ctx) : Answer(ctx, 404, "curia/request/no-route"));
        var lowercaseOnly = await EnrolmentCheckAsync(ClosedEdge(ctx => ctx.Request.Path.Value == "/v1/agents"));
        var otherType = await EnrolmentCheckAsync(ctx => Answer(ctx, 403, "curia/keys/lifecycle-not-enabled"));
        var notFound = await EnrolmentCheckAsync(ctx => Answer(ctx, 404, "curia/request/no-route"));
        Assert.True(
            closed.Status("R13") == "PASS" && open.Status("R13") == "FAIL" && lowercaseOnly.Status("R13") == "FAIL" && otherType.Status("R13") == "FAIL" && notFound.Status("R13") == "FAIL",
            $"closed edge / an open Forum / one spelling closed / another 403 / a 404: R13 was {closed.Status("R13")} / {open.Status("R13")} / {lowercaseOnly.Status("R13")} / {otherType.Status("R13")} / {notFound.Status("R13")}; {closed.Raw} {lowercaseOnly.Raw}");
        Assert.Contains("reached the Forum's enrolment route", open.Checks["R13"].Reason, StringComparison.Ordinal);
    }

    /// <summary>RDY18, the mirror: with <c>--expect-enrolment open</c> a Forum that answers a well-formed body with <c>proof-required</c> in both spellings PASSes, and a closed edge FAILs; a body the Forum cannot read as an enrolment (an empty one) is not what the probe sends.</summary>
    [Fact]
    public async Task G86_RDY18_OpenIsExpectedAndTheForumsOwnProofRequiredIsWhatPasses()
    {
        var open = await EnrolmentCheckAsync(ctx => IsEnrolmentPath(ctx) ? ForumEnrolment(ctx) : Answer(ctx, 404, "curia/request/no-route"), "--expect-enrolment", "open");
        var closed = await EnrolmentCheckAsync(ClosedEdge(), "--expect-enrolment", "open");
        var bare = await EnrolmentCheckAsync(ctx => Answer(ctx, 400, "curia/enroll/invalid"), "--expect-enrolment", "open");
        Assert.True(open.Status("R13") == "PASS" && closed.Status("R13") == "FAIL" && bare.Status("R13") == "FAIL",
            $"an open Forum / a closed edge / a Forum that does not say proof-required: R13 was {open.Status("R13")} / {closed.Status("R13")} / {bare.Status("R13")}; {open.Raw}");
    }

    /// <summary>RDY19: the script's two defaults are the errata's two figures (read from the errata, not from the script), and the default expectation is that enrolment is closed.</summary>
    [Fact]
    public async Task G86_RDY19_TheDefaultsAreTheErratasFigures()
    {
        var (ceiling, warn) = PublishedEdge.EventBand();
        var script = Path.Combine(PublishedEdge.RepoRoot(), "tools", "deploy", "readiness.py");
        var start = new System.Diagnostics.ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-I", "-c", "import importlib.util, sys; s = importlib.util.spec_from_file_location('readiness', sys.argv[1]); m = importlib.util.module_from_spec(s); s.loader.exec_module(m); a = m.parse(['--origin', 'http://x']); print(a.event_ceiling, a.event_warn, a.expect_enrolment)", script }) start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start)!;
        var text = (await process.StandardOutput.ReadToEndAsync(Ct)).Trim();
        var err = await process.StandardError.ReadToEndAsync(Ct);
        await process.WaitForExitAsync(Ct);
        Assert.True(text == $"{ceiling} {warn} closed", $"the script's defaults are '{text}', the errata's are '{ceiling} {warn} closed' {err}");
    }

    /// <summary>RDY20 (RDY12's contract for the two new checks): an unselected R12 and R13 are NOT CHECKED, an edge that cannot be reached FAILs R13 (a transport fault is a failure), and none of them is ever PASS unrun.</summary>
    [Fact]
    public async Task G86_RDY20_AnUnrunR12AndR13AreNeverPassed()
    {
        var unselected = await ReadinessAsync(["--origin", Origin(forum), "--only", "R1"]);
        var down = await ReadinessAsync(["--origin", "http://127.0.0.1:1", "--only", "R13"]);
        Assert.True(unselected.Status("R12") == "NOT CHECKED" && unselected.Status("R13") == "NOT CHECKED" && down.Status("R13") == "FAIL",
            $"not selected: R12 {unselected.Status("R12")}, R13 {unselected.Status("R13")}; an edge that is down: R13 {down.Status("R13")}");
    }

    /// <summary>
    /// RDY21 (the RDY6 flake): a key id is base64url, so about one in 64 begins with <c>-</c>, which argparse reads as an option: <c>--log-kid: expected one argument</c> and no report. Whatever a kid begins with, the
    /// separate form (<c>--log-kid -abc</c>) and the joined form (<c>--log-kid=-abc</c>) reach R6, which says the Forum does not list it (FAIL); no usage error, no missing check.
    /// </summary>
    [Fact]
    public async Task G86_RDY21_AKidThatBeginsWithAHyphenIsAKidAndNotAnOption()
    {
        var origin = Origin(forum);
        var separate = await ReadinessAsync(["--origin", origin, "--only", "R6", "--log-kid", "-abc"]);
        var doubled = await ReadinessAsync(["--origin", origin, "--only", "R6", "--declaration-kid", "--abc", "--operator-kid", "-_x"]);
        var joined = await ReadinessAsync(["--origin", origin, "--only", "R6", "--log-kid=-abc"]);
        Assert.True(
            separate.Status("R6") == "FAIL" && doubled.Status("R6") == "FAIL" && joined.Status("R6") == "FAIL",
            $"R6 for a kid beginning with a hyphen, separate / doubled / joined: {separate.Status("R6")} (exit {separate.Exit}) / {doubled.Status("R6")} (exit {doubled.Exit}) / {joined.Status("R6")} (exit {joined.Exit}); {separate.Raw}");
    }
}
