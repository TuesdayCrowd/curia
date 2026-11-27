using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Infrastructure.Migrations;
using Curia.OperatorTool;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>A Forum on a real socket named <c>http://127.0.0.1:&lt;port&gt;</c>: the host <c>tools/deploy/readiness.py</c> is run against.</summary>
public sealed class ReadinessForumFixture : ForumFixture
{
    public ReadinessForumFixture() => UseReservedKestrelPort();
}

/// <summary>R2's red twin: the host's own filter (<c>AllowedHosts</c>, item 390) lets the origin's name through and answers an HTML 400 to any other, before the Forum's check can speak.</summary>
public sealed class ReadinessAllowedHostsFixture : ForumFixture
{
    public ReadinessAllowedHostsFixture() => UseReservedKestrelPort();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.ConfigureWebHost(builder);
        builder.UseSetting("AllowedHosts", "127.0.0.1");
    }
}

/// <summary>R8's red twin: a Forum that takes <c>X-Forwarded-Host</c> for its <c>Host</c> before the name is compared (the defect R5.26 forbids), on a real socket.</summary>
public sealed class ReadinessForwardedHostFixture : ForumFixture
{
    public ReadinessForwardedHostFixture() => UseReservedKestrelPort();

    protected override void ConfigureStore(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.Insert(0, ServiceDescriptor.Singleton<IStartupFilter>(new ForwardingFilter()));
    }

    private sealed class ForwardingFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // A Forum that trusts what a client says its name is: the header a proxy would write, taken for the Host before anything compares it.
            app.Use((ctx, n) =>
            {
                if (ctx.Request.Headers.TryGetValue("X-Forwarded-Host", out var forwarded)) ctx.Request.Headers.Host = forwarded;
                return n();
            });
            next(app);
        };
    }
}

/// <summary>
/// RDY1 to RDY12 (errata G83, R11.110): <c>tools/deploy/readiness.py</c> run with <c>python3</c> against a real Forum on a socket, one fault at a time. Each check is shown PASS against the Forum as built and
/// FAIL (or NOT CHECKED) against the fault it exists for; the rule of R11.110 is held by RDY12: a check that could not run is never PASS and the exit is 8, not 0. <c>python3</c> is required like the external signer's
/// is: a probe that cannot run the script fails and does not skip, and so does <c>psql</c> for RDY11.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each fixture is an await using declaration; the analyzer loses the ownership through the generic factory.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every identifier is generated here from a literal prefix and a GUID's hex digits.")]
public sealed partial class ReadinessScriptTests(ReadinessForumFixture started) : IClassFixture<ReadinessForumFixture>
{
    /// <summary>The fixture's host, started: a host is built on first use, and the script dials it from another process.</summary>
    private readonly ReadinessForumFixture forum = Start(started);

    private static ReadinessForumFixture Start(ReadinessForumFixture f)
    {
        _ = f.Client;
        return f;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] UnrunChecks = ["R5", "R6", "R9", "R10", "R11"];

    private static readonly string[] RunChecks = ["R1", "R2", "R3", "R4", "R7", "R8", "R12", "R13"];

    private sealed record Report(int Exit, Dictionary<string, (string Status, string Reason)> Checks, string Raw)
    {
        internal string Status(string id) => Checks.TryGetValue(id, out var c) ? c.Status : "MISSING";

        internal string Says() => string.Join(" | ", Checks.OrderBy(c => int.Parse(c.Key[1..], CultureInfo.InvariantCulture)).Select(c => $"{c.Key} {c.Value.Status}"));
    }

    private static async Task<Report> ReadinessAsync(string[] args, Dictionary<string, string>? env = null)
    {
        var script = Path.Combine(PublishedEdge.RepoRoot(), "tools", "deploy", "readiness.py");
        var start = new ProcessStartInfo("python3") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(script);
        foreach (var a in args) start.ArgumentList.Add(a);
        start.Environment.Remove("CURIA_EVENTS_POSTGRES");
        start.Environment.Remove("CURIA_READINESS_DB_URL");
        foreach (var (k, v) in env ?? []) start.Environment[k] = v;
        Process process;
        try
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("no process");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new InvalidOperationException("python3 is required to run tools/deploy/readiness.py and was not found (this fails, never skips): " + e.Message, e);
        }

        using (process)
        {
            var stdout = process.StandardOutput.ReadToEndAsync(Ct);
            var stderr = process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct).ConfigureAwait(false);
            var text = await stdout.ConfigureAwait(false);
            var checks = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
            foreach (Match m in CheckLine().Matches(text)) checks[m.Groups[1].Value] = (m.Groups[2].Value, m.Groups[3].Value);
            return new Report(process.ExitCode, checks, text + await stderr.ConfigureAwait(false));
        }
    }

    private static string Origin(ForumFixture f) => f.PublicOrigin;

    private static async Task<T> NewAsync<T>() where T : ForumFixture, new()
    {
        var f = new T();
        await f.InitializeAsync();
        return f;
    }

    /// <summary>Compares the statuses a report gave for <paramref name="expected"/> checks and shows the whole report when they differ.</summary>
    private static void Statuses(Report report, params (string Id, string Status)[] expected)
    {
        var wrong = expected.Where(e => report.Status(e.Id) != e.Status).Select(e => $"{e.Id} was {report.Status(e.Id)}, not {e.Status}").ToList();
        Assert.True(wrong.Count == 0, string.Join("; ", wrong) + "\n" + report.Raw);
    }

    /// <summary>RDY1: against the Forum as built, the checks that need nothing but the origin PASS; the rest are NOT CHECKED (R11.110), so the exit is 8 and never 0.</summary>
    [Fact]
    public async Task G83_RDY1_AForumThatIsWhatItSaysPassesTheChecksThatRun()
    {
        var r = await ReadinessAsync(["--origin", Origin(forum), "--only", "R1,R2,R3,R4,R8"]);
        Statuses(r, ("R1", "PASS"), ("R2", "PASS"), ("R3", "PASS"), ("R4", "PASS"), ("R8", "PASS"), ("R5", "NOT CHECKED"));
        Assert.True(r.Exit == 8, $"every selected check passed and the others were not run: the exit should be 8, was {r.Exit}\n{r.Raw}");
        Assert.Contains($"Host 127.0.0.1:{new Uri(Origin(forum)).Port}", r.Checks["R1"].Reason, StringComparison.Ordinal);
    }

    /// <summary>RDY2: a Forum that is not there fails R1 (and the exit is 1), and R2 does not pass on a closed port.</summary>
    [Fact]
    public async Task G83_RDY2_AStoppedForumFailsR1()
    {
        var r = await ReadinessAsync(["--origin", "http://127.0.0.1:1", "--only", "R1,R2"]);
        Statuses(r, ("R1", "FAIL"), ("R2", "FAIL"));
        Assert.Equal(1, r.Exit);
    }

    /// <summary>RDY3: with the host's <c>AllowedHosts</c> set, R1 passes (the origin's own name is allowed) and R2 FAILs, naming the setting: a foreign name is refused as HTML, not as the Forum's problem document (item 390).</summary>
    [Fact]
    public async Task G83_RDY3_AHostFilterTurnsR2Red()
    {
        await using var f = await NewAsync<ReadinessAllowedHostsFixture>();
        _ = f.Client;
        var r = await ReadinessAsync(["--origin", Origin(f), "--only", "R1,R2"]);
        Statuses(r, ("R1", "PASS"), ("R2", "FAIL"));
        Assert.Contains("AllowedHosts", r.Checks["R2"].Reason, StringComparison.Ordinal);
    }

    /// <summary>RDY4: a name that is not the Forum's own (the Forum answers it 400 and discovery is not served under it) fails R1 and R3.</summary>
    [Fact]
    public async Task G83_RDY4_ANameThatIsNotTheForumsFailsR1AndR3()
    {
        var port = new Uri(Origin(forum)).Port;
        var r = await ReadinessAsync(["--origin", $"http://localhost:{port}", "--connect", $"127.0.0.1:{port}", "--only", "R1,R3,R4"]);
        Statuses(r, ("R1", "FAIL"), ("R3", "FAIL"), ("R4", "FAIL"));
    }

    private sealed class TlsServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;

        internal TlsServer(X509Certificate2 certificate)
        {
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    try
                    {
                        using var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                        await using var ssl = new SslStream(client.GetStream());
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception e) when (e is System.Security.Authentication.AuthenticationException or IOException or SocketException)
                    {
                        // The script's check closes after the handshake, or refuses ours: the next connection is a new one.
                    }
                }
            }, CancellationToken.None);
        }

        internal int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            _listener.Dispose();
            await _loop;
            _stop.Dispose();
        }
    }

    private static (X509Certificate2 Leaf, string CaPem) Chain(string dns, TimeSpan notBefore, TimeSpan notAfter)
    {
        var now = DateTimeOffset.UtcNow;
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=Curia readiness test CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        caRequest.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(caRequest.PublicKey, false));
        using var ca = caRequest.CreateSelfSigned(now - TimeSpan.FromDays(400), now + TimeSpan.FromDays(400));
        using var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={dns}", leafKey, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dns);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        using var signed = request.Create(ca, now + notBefore, now + notAfter, RandomNumberGenerator.GetBytes(8));
        using var withKey = signed.CopyWithPrivateKey(leafKey);
        return (X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null), ca.ExportCertificatePem());
    }

    /// <summary>
    /// RDY5: R5 against a throwaway CA and certificate the test generates: a certificate that verifies for the name and has weeks left PASSes; an expired one and one for another name FAIL; an <c>http</c> origin FAILs
    /// unless <c>--allow-http</c>, and with it is NOT CHECKED (there is no certificate), never PASS. R5 is never PASS against a plain socket.
    /// </summary>
    [Fact]
    public async Task G83_RDY5_TheCertificateIsCheckedAgainstTheNameAndTheClock()
    {
        var ca = Path.Combine(Path.GetTempPath(), "curia-rdy5-" + Guid.NewGuid().ToString("N")[..8] + ".pem");
        try
        {
            var problems = new List<string>();
            foreach (var (label, dns, from, to, expected) in new[]
            {
                ("valid", "localhost", TimeSpan.FromDays(-1), TimeSpan.FromDays(30), "PASS"),
                ("expired", "localhost", TimeSpan.FromDays(-60), TimeSpan.FromDays(-1), "FAIL"),
                ("wrong name", "other.example", TimeSpan.FromDays(-1), TimeSpan.FromDays(30), "FAIL"),
                ("expires within the floor", "localhost", TimeSpan.FromDays(-1), TimeSpan.FromDays(5), "FAIL"),
            })
            {
                var (leaf, caPem) = Chain(dns, from, to);
                await File.WriteAllTextAsync(ca, caPem, Ct);
                await using var server = new TlsServer(leaf);
                var r = await ReadinessAsync(["--origin", $"https://localhost:{server.Port}", "--ca-file", ca, "--only", "R5"]);
                if (r.Status("R5") != expected) problems.Add($"{label}: R5 was {r.Status("R5")} ({(r.Checks.TryGetValue("R5", out var c) ? c.Reason : r.Raw)}), not {expected}");
                leaf.Dispose();
            }

            var http = await ReadinessAsync(["--origin", Origin(forum), "--only", "R5"]);
            if (http.Status("R5") != "FAIL") problems.Add($"an http origin without --allow-http: R5 was {http.Status("R5")}, not FAIL");
            var allowed = await ReadinessAsync(["--origin", Origin(forum), "--allow-http", "--only", "R5"]);
            if (allowed.Status("R5") != "NOT CHECKED") problems.Add($"an http origin with --allow-http: R5 was {allowed.Status("R5")}, not NOT CHECKED");
            Assert.True(problems.Count == 0, string.Join("; ", problems));
        }
        finally
        {
            File.Delete(ca);
        }
    }

    private static async Task<string> RunOperatorAsync(ForumFixture f, string[] args, Func<string?>? logKey = null, Func<string?>? declarationKey = null)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exit = await OperatorCommands.RunAsync(
            args, f.ConnectionString, f.Clock, f.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, Ct,
            readLogSigningKeyPem: logKey, readDeclarationSigningKeyPem: declarationKey, readOperatorActsSigningKeyPem: await f.OperatorActsKeyForAsync(args, Ct));
        Assert.True(exit == 0, $"{string.Join(' ', args)} exited {exit}: {stderr}");
        return stdout.ToString();
    }

    private static string KidOf(string operatorOutput) => Kid().Match(operatorOutput) is { Success: true } m ? m.Groups[1].Value : throw new InvalidOperationException("no kid in: " + operatorOutput);

    private static async Task<JsonElement> KeySetAsync(ForumFixture f)
    {
        using var response = await f.Client.GetAsync(new Uri("/v1/log/jwks", UriKind.Relative), Ct);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    /// <summary>
    /// RDY6: R6 against a log after the three keys are published: the announced kids (the operator prints them at publication) PASS; a wrong kid FAILs; a missing argument is NOT CHECKED, never PASS (R11.110). The kids are passed as `--flag=value`: a random kid begins with `-` one time in 64 and RDY21 is the probe for that, so this one does not depend on the first character.
    /// </summary>
    [Fact]
    public async Task G83_RDY6_TheAnnouncedKidsAreTheForumsOrTheCheckSaysItDidNotRun()
    {
        await using var f = await NewAsync<ReadinessForumFixture>();
        _ = f.Client;
        var declarationPem = DeclarationSigningKey.GeneratePem();
        var logPem = LogSigningKey.GeneratePem();
        var declarationKid = KidOf(await RunOperatorAsync(f, ["publish-declaration-key", "--by", "ops"], declarationKey: () => declarationPem));
        var logKid = KidOf(await RunOperatorAsync(f, ["sign-head", "--by", "ops"], logKey: () => logPem));
        _ = await f.OperatorActsKeyAsync(Ct);
        var operatorKid = (await KeySetAsync(f)).GetProperty("operator_keys")[0].GetProperty("kid").GetString()!;
        var origin = Origin(f);

        var good = await ReadinessAsync(["--origin", origin, "--only", "R6", $"--declaration-kid={declarationKid}", $"--operator-kid={operatorKid}", $"--log-kid={logKid}"]);
        var wrong = await ReadinessAsync(["--origin", origin, "--only", "R6", $"--declaration-kid={declarationKid}", "--operator-kid=not-the-kid", $"--log-kid={logKid}"]);
        var partial = await ReadinessAsync(["--origin", origin, "--only", "R6", $"--declaration-kid={declarationKid}"]);
        var none = await ReadinessAsync(["--origin", origin, "--only", "R6"]);
        Assert.True(
            good.Status("R6") == "PASS" && wrong.Status("R6") == "FAIL" && partial.Status("R6") == "NOT CHECKED" && none.Status("R6") == "NOT CHECKED" && none.Exit == 8,
            $"announced kids / a wrong kid / one kid / none: R6 was {good.Status("R6")} / {wrong.Status("R6")} / {partial.Status("R6")} / {none.Status("R6")} (exit {none.Exit}); {good.Raw}");
    }

    /// <summary>RDY7: R7 is the timer's liveness: no head is FAIL; a head signed long ago (the fixture's clock is in the past) is FAIL; a head signed now is PASS.</summary>
    [Fact]
    public async Task G83_RDY7_TheHeadTimersLivenessIsReadFromTheHeadsOwnTimestamp()
    {
        await using var f = await NewAsync<ReadinessForumFixture>();
        _ = f.Client;
        var origin = Origin(f);
        var logPem = LogSigningKey.GeneratePem();
        var states = new List<string> { (await ReadinessAsync(["--origin", origin, "--only", "R7"])).Status("R7") };
        _ = await RunOperatorAsync(f, ["sign-head", "--by", "ops"], logKey: () => logPem);
        states.Add((await ReadinessAsync(["--origin", origin, "--only", "R7"])).Status("R7"));
        f.Clock.Advance(DateTimeOffset.UtcNow - f.Now);
        _ = await RunOperatorAsync(f, ["sign-head", "--by", "ops"], logKey: () => logPem);
        var now = await ReadinessAsync(["--origin", origin, "--only", "R7"]);
        states.Add(now.Status("R7"));
        Assert.True(string.Join(", ", states) == "FAIL, FAIL, PASS", $"R7 with no head / a head from months ago / a head now: {string.Join(", ", states)}; {now.Raw}");
    }

    /// <summary>RDY8: a Forum that applies <c>X-Forwarded-Host</c> answers the forged request differently, and R8 FAILs; the Forum as built PASSes (RDY1).</summary>
    [Fact]
    public async Task G83_RDY8_AForumThatReadsAForwardedHeaderFailsR8()
    {
        await using var f = await NewAsync<ReadinessForwardedHostFixture>();
        _ = f.Client;
        var r = await ReadinessAsync(["--origin", Origin(f), "--only", "R8"]);
        Statuses(r, ("R8", "FAIL"));
    }

    /// <summary>
    /// RDY8b: behind an edge that rewrites the <c>Host</c> (the <c>$host</c> defect), R1 and R8 both FAIL and R8 says the proxy rewrote the Host and does not blame the Forum for reading a forwarded header
    /// (live finding 5); a Forum that does read the header still gets the R5.26 words (RDY8).
    /// </summary>
    [Fact]
    public async Task G83_RDY8b_ARewrittenHostIsNotBlamedOnTheForum()
    {
        await using var edge = await NewAsync<ForwardedForumFixture>();
        _ = edge.Client;
        string[] through = ["--origin", edge.PublicOrigin, "--connect", $"127.0.0.1:{edge.ForwarderPort}", "--only", "R1,R8"];
        _ = await edge.ForwardAsync(new RecipeForwarder.Options(RewriteHost: true));
        var r = await ReadinessAsync(through);
        Statuses(r, ("R1", "FAIL"), ("R8", "FAIL"));
        var reason = r.Checks["R8"].Reason;
        Assert.True(reason.Contains("rewrote the Host", StringComparison.Ordinal) && !reason.Contains("the Forum reads a forwarded header", StringComparison.Ordinal), $"R8 said: {reason}");
        var fwd = await NewAsync<ReadinessForwardedHostFixture>();
        await using (fwd)
        {
            _ = fwd.Client;
            var blamed = await ReadinessAsync(["--origin", Origin(fwd), "--only", "R8"]);
            Assert.Contains("the Forum reads a forwarded header", blamed.Checks["R8"].Reason, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// RDY9: R9 is opt-in, and against a bare Forum it FAILs (no edge limit: asserted); through the forwarder that models the recipe it PASSes with the 429 position named; with the bucket removed it FAILs again.
    /// Without <c>--probe-limits</c> it is NOT CHECKED.
    /// </summary>
    [Fact]
    public async Task G83_RDY9_TheEdgeLimitIsSeenThroughTheOriginOrTheCheckFails()
    {
        var bare = await ReadinessAsync(["--origin", Origin(forum), "--only", "R9", "--probe-limits"]);
        var off = await ReadinessAsync(["--origin", Origin(forum), "--only", "R9"]);
        await using var edge = await NewAsync<ForwardedForumFixture>();
        _ = edge.Client;
        string[] through = ["--origin", edge.PublicOrigin, "--connect", $"127.0.0.1:{edge.ForwarderPort}", "--only", "R9", "--probe-limits"];
        _ = await edge.ForwardAsync();
        var limited = await ReadinessAsync(through);
        _ = await edge.ForwardAsync(new RecipeForwarder.Options(DropBucket: true));
        var open = await ReadinessAsync(through);
        Assert.True(
            bare.Status("R9") == "FAIL" && off.Status("R9") == "NOT CHECKED" && limited.Status("R9") == "PASS" && open.Status("R9") == "FAIL",
            $"bare Forum / no flag / through the recipe / bucket removed: R9 was {bare.Status("R9")} / {off.Status("R9")} / {limited.Status("R9")} / {open.Status("R9")}; {limited.Raw}");
    }

    /// <summary>RDY10: R10 runs the operator's lifecycle-audit: a clean log PASSes, a log holding a second root (a row the Forum skips) FAILs on exit 7, and with no database variable the check is NOT CHECKED.</summary>
    [Fact]
    public async Task G83_RDY10_ASkippedRowFailsTheAudit()
    {
        await using var f = await VerifiedStopSupport.NewAsync();
        // The operator as its own project builds it: the copy beside the tests lacks the assemblies a database verb loads, which only `--help` does not.
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var operatorDll = Path.Combine(PublishedEdge.RepoRoot(), "src", "Curia.Operator", "bin", configuration, "net10.0", "curia-operator.dll");
        Assert.True(File.Exists(operatorDll), $"{operatorDll} is not built");
        string[] args = ["--origin", "http://127.0.0.1:1", "--only", "R10", "--operator-cmd", $"dotnet {operatorDll}"];
        var env = new Dictionary<string, string> { ["CURIA_EVENTS_POSTGRES"] = f.ConnectionString };
        var api = new OperatorActsApi(f);
        var victim = await api.GenuineAsync("rdy10", Ct);
        var unset = await ReadinessAsync(args);
        var clean = await ReadinessAsync(args, env);
        var agent = victim.Agent.AgentId;
        await api.InsertRowAsync(
            "agent.enrolled", agent, $"{{\"agent_id\":\"{agent}\",\"kid\":\"{victim.Agent.Kid}\",\"reason\":\"Enrollment accepted: agent key registered with the Registrar\"}}", agent, f.Now - TimeSpan.FromHours(72), Ct);
        var planted = await ReadinessAsync(args, env);
        Assert.True(
            unset.Status("R10") == "NOT CHECKED" && clean.Status("R10") == "PASS" && planted.Status("R10") == "FAIL" && planted.Checks["R10"].Reason.Contains("exit 7", StringComparison.Ordinal),
            $"no variable / clean log / a skipped row: R10 was {unset.Status("R10")} / {clean.Status("R10")} ({clean.Checks.GetValueOrDefault("R10").Reason}) / {planted.Status("R10")} ({planted.Checks.GetValueOrDefault("R10").Reason})");
    }

    private static string AdminUrl(string connectionString, string database)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString) { Database = database };
        var credentials = string.IsNullOrEmpty(b.Password) ? Uri.EscapeDataString(b.Username ?? string.Empty) : $"{Uri.EscapeDataString(b.Username ?? string.Empty)}:{Uri.EscapeDataString(b.Password)}";
        return $"postgresql://{credentials}@{b.Host}:{b.Port}/{database}";
    }

    /// <summary>
    /// RDY11: R11 by <c>psql</c>: the fixture's database (every migration) PASSes; a database built without <c>db/0006</c> FAILs on the <c>seq</c> grant and a database without <c>db/0002</c> FAILs on the two tables; with no URL the
    /// check is NOT CHECKED. <c>psql</c> is required: the probe fails, and does not skip, without it.
    /// </summary>
    [Fact]
    public async Task G83_RDY11_TheGrantsAndTheTablesAreReadByPsql()
    {
        Assert.True(Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator).Any(d => File.Exists(Path.Combine(d, "psql"))), "psql is required by RDY11 (this fails, never skips)");
        await using var f = await NewAsync<ReadinessForumFixture>();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var withoutSix = $"curia_rdy_a_{suffix}";
        var withoutTwo = $"curia_rdy_b_{suffix}";
        var admin = new NpgsqlConnectionStringBuilder(f.ConnectionString) { Database = "postgres" }.ConnectionString;
        await using var source = new NpgsqlDataSourceBuilder(admin).Build();
        async Task BuildAsync(string name, IEnumerable<string> files)
        {
            await using (var create = source.CreateCommand($"CREATE DATABASE \"{name}\"")) await create.ExecuteNonQueryAsync(Ct);
            await using var target = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString).Build();
            foreach (var file in files)
            {
                await using var apply = target.CreateCommand(SchemaMigrations.Render(file, name, "pw-" + suffix));
                await apply.ExecuteNonQueryAsync(Ct);
            }
        }

        try
        {
            await BuildAsync(withoutSix, SchemaMigrations.FileNames.Where(n => n != SchemaMigrations.EventInsertColumnsFile));
            await BuildAsync(withoutTwo, [SchemaMigrations.FileNames[0], SchemaMigrations.EventInsertColumnsFile]);
            string[] common = ["--origin", "http://127.0.0.1:1", "--only", "R11"];
            var good = await ReadinessAsync([.. common, "--db-admin-url", AdminUrl(f.ConnectionString, new NpgsqlConnectionStringBuilder(f.ConnectionString).Database!), "--app-role", f.RoleName]);
            var noSix = await ReadinessAsync([.. common, "--db-admin-url", AdminUrl(f.ConnectionString, withoutSix), "--app-role", withoutSix]);
            var noTwo = await ReadinessAsync([.. common, "--db-admin-url", AdminUrl(f.ConnectionString, withoutTwo), "--app-role", withoutTwo]);
            var unset = await ReadinessAsync(common);
            Assert.True(
                good.Status("R11") == "PASS" && noSix.Status("R11") == "FAIL" && noSix.Checks["R11"].Reason.Contains("events.seq", StringComparison.Ordinal)
                && noTwo.Status("R11") == "FAIL" && noTwo.Checks["R11"].Reason.Contains("authn_replay", StringComparison.Ordinal) && unset.Status("R11") == "NOT CHECKED",
                $"full / without 0006 / without 0002 / no url: R11 was {good.Status("R11")} / {noSix.Status("R11")} / {noTwo.Status("R11")} / {unset.Status("R11")}; {good.Raw} {noSix.Raw} {noTwo.Raw}");
        }
        finally
        {
            foreach (var name in new[] { withoutSix, withoutTwo })
            {
                await using (var drop = source.CreateCommand($"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)")) await drop.ExecuteNonQueryAsync(CancellationToken.None);
                await using (var dropRole = source.CreateCommand($"DROP ROLE IF EXISTS \"{name}\"")) await dropRole.ExecuteNonQueryAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>
    /// RDY12, R11.110's rule itself: a run with nothing optional given reports the checks it could not run as NOT CHECKED, none of them PASS, and exits 8 when the rest passed (never 0); with a failing check it exits 1.
    /// </summary>
    [Fact]
    public async Task G83_RDY12_AnUnrunCheckIsNeverReportedAsPassed()
    {
        await using var f = await NewAsync<ReadinessForumFixture>();
        _ = f.Client;
        var logPem = LogSigningKey.GeneratePem();
        f.Clock.Advance(DateTimeOffset.UtcNow - f.Now);
        _ = await RunOperatorAsync(f, ["sign-head", "--by", "ops"], logKey: () => logPem);
        var r = await ReadinessAsync(["--origin", Origin(f), "--allow-http", "--expect-enrolment", "open"]);
        var unrun = UnrunChecks;
        var passed = unrun.Where(id => r.Status(id) != "NOT CHECKED").ToList();
        Assert.True(passed.Count == 0 && r.Exit == 8 && RunChecks.All(id => r.Status(id) == "PASS"),
            $"the checks given nothing to run should be NOT CHECKED and the exit 8: {r.Says()} (exit {r.Exit}); not NOT CHECKED: {string.Join(", ", passed)}");

        var stopped = await ReadinessAsync(["--origin", "http://127.0.0.1:1", "--allow-http"]);
        Assert.True(stopped.Exit == 1, $"a failing check should make the exit 1, was {stopped.Exit}");
    }

    [GeneratedRegex(@"^(R\d+)\s+(PASS|FAIL|NOT CHECKED)\s+(.*)$", RegexOptions.Multiline)]
    private static partial Regex CheckLine();

    [GeneratedRegex(@"(?:\bkid|published declaration key)\s+(\S+)")]
    private static partial Regex Kid();
}
