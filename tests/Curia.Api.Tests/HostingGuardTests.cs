using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Curia.Api.Tests.OriginProbe;

namespace Curia.Api.Tests;

/// <summary>
/// A host built for R11.112's probes: told an origin (or none) and an environment (or none: the process variables then decide, or the factory's own), and optionally something else wrong with its start,
/// so that the order of the refusals can be read from the message. Not public, so ORG15 does not start it.
/// </summary>
internal sealed class GuardProbeFixture(string? origin, string? environment, Action<IWebHostBuilder>? afterConfigure = null, bool productionEmbedder = false, string? connectionString = null) : ForumFixture
{
    internal override string PublicOrigin => origin ?? "http://localhost";

    protected override string? HostingEnvironment => environment;

    protected override bool ReplacesEmbedderWithTheHashedOne => !productionEmbedder;

    protected override string HostConnectionString => connectionString ?? base.HostConnectionString;

    protected override void ConfigureOrigin(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (origin is not null) builder.UseSetting("Curia:PublicOrigin", origin);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        afterConfigure?.Invoke(builder);
    }
}

/// <summary>DEV9: the shape the rule refuses, written by a fixture that forgot: a public origin, and the factory's own environment (Development). Private, so ORG15 does not start it.</summary>
file sealed class ForgetfulFixture : ForumFixture
{
    internal override string PublicOrigin => "https://forum.example";
}

/// <summary>
/// R11.112 (errata G85), the start refusal: a Forum whose host is Development and whose public origin is not loopback does not start. <b>Written red first</b>: <c>Curia.Api.HostingGuard</c> is read by reflection, so a
/// probe written before the type exists is red by an assertion, and the start probes are red because the unguarded Forum starts. Each refused row is first started in Production and must start (trap 305: a row that
/// R11.109 also refuses passes for the wrong reason), and each starts row is started in Development.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("origin-env")]
public sealed class HostingGuardTests
{
    private const string TypeName = "Curia.Api.HostingGuard";
    private const string GuardType = "curia/config/development-with-public-origin";
    private const string OriginVariable = "CURIA_PUBLIC_ORIGIN";

    private static readonly string[] Named = ["\"https://forum.example\"", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "Production"];
    private static readonly string[] NotDevelopment = ["Production", "Staging", "Dev", "Developments"];
    private static readonly string[] OtherCases = ["development", "DEVELOPMENT"];
    private static readonly string[] R109Refuses = ["http://[::ffff:7f00:1]", "https://forum.example/", "http://forum.example."];

    private static string Chain(Exception? exception)
    {
        var messages = new List<string>();
        for (var e = exception; e is not null; e = e.InnerException) messages.Add(e.GetType().Name + ": " + e.Message);
        return string.Join(" | ", messages);
    }

    /// <summary>Starts a host and returns the whole exception chain of its refusal, or null when it started.</summary>
    private static async Task<string?> StartAsync(GuardProbeFixture fixture)
    {
        await using (fixture)
        {
            await fixture.InitializeAsync();
            var failure = Record.Exception(() => fixture.Services);
            return failure is null ? null : Chain(failure);
        }
    }

    private static Task<string?> StartAsync(string? origin, string? environment) => StartAsync(new GuardProbeFixture(origin, environment));

    private static string Words() =>
        (string?)Guard().GetField("Words", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue()
        ?? throw new Xunit.Sdk.XunitException($"{TypeName}.Words does not exist: the refusal's words are held nowhere");

    /// <summary>The filled words, or a sentinel when the guard does not exist, so that a start probe is red by its own assertion about the start and not by the missing type.</summary>
    private static string FilledOrMissing(string origin) =>
        Type.GetType(TypeName + ", Curia.Api") is null && typeof(Program).Assembly.GetType(TypeName) is null ? "<no Curia.Api.HostingGuard.Words>" : Filled(origin);

    private static string Filled(string origin) => Words().Replace("{Origin}", origin, StringComparison.Ordinal);

    private static Type Guard() =>
        typeof(Program).Assembly.GetType(TypeName)
        ?? throw new Xunit.Sdk.XunitException($"{TypeName} does not exist: nothing refuses a Development Forum whose name is not this machine's (R11.112)");

    /// <summary>Sets process variables for the body and restores every one of them, whatever happens.</summary>
    private static async Task WithEnvironmentAsync(IReadOnlyDictionary<string, string?> variables, Func<Task> body)
    {
        var previous = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        foreach (var (name, value) in variables) Environment.SetEnvironmentVariable(name, value);
        try
        {
            await body();
        }
        finally
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static readonly Dictionary<string, string?> Clean = new(StringComparer.Ordinal)
    {
        [OriginVariable] = null,
        ["DOTNET_ENVIRONMENT"] = null,
        ["ASPNETCORE_ENVIRONMENT"] = null,
    };

    private static Dictionary<string, string?> Clean_(params (string Name, string? Value)[] set)
    {
        var map = new Dictionary<string, string?>(Clean, StringComparer.Ordinal);
        foreach (var (name, value) in set) map[name] = value;
        return map;
    }

    /// <summary>
    /// DEV1. Development and <c>https://forum.example</c>, through a fixture that sets the host's environment with <c>UseEnvironment</c> and no process variable (so the guard is red if it reads anything
    /// but the host's own resolution): the start is refused, and the exception's message is the filled words, naming the origin, <c>DOTNET_ENVIRONMENT</c>, <c>ASPNETCORE_ENVIRONMENT</c> and Production.
    /// Red on the tree: the Forum starts.
    /// </summary>
    [Fact]
    public async Task R11_112_DEV1_ADevelopmentForumWithAPublicOriginDoesNotStart()
    {
        await WithEnvironmentAsync(Clean, async () =>
        {
            var refusal = await StartAsync("https://forum.example", "Development");
            Assert.True(refusal is not null, "R11.112: a Forum started in Development with the public origin https://forum.example, and a Forum so named serves a developer exception page that carries the credentials of a request that faulted");
            var words = Filled("https://forum.example");
            Assert.True(refusal.Contains(words, StringComparison.Ordinal), $"R11.112: the refusal is not the guard's words. Expected {words} in: {refusal}");
            foreach (var needed in Named)
                Assert.True(refusal.Contains(needed, StringComparison.Ordinal), $"R11.112: the refusal does not name {needed}: {refusal}");
        });
    }

    /// <summary>
    /// DEV1b. The guard's own verdict, called directly: the error's type is <c>curia/config/development-with-public-origin</c>, its detail is the filled words; Production and a loopback origin are ok.
    /// Red on the tree: <c>Curia.Api.HostingGuard</c> does not exist.
    /// </summary>
    [Fact]
    public void R11_112_DEV1b_TheGuardsVerdictHasItsTypeAndItsWords()
    {
        var check = Guard().GetMethod("Check", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new Xunit.Sdk.XunitException($"{TypeName}.Check does not exist");
        var parse = typeof(Program).Assembly.GetType("Curia.Api.PublicOrigin")!.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)])!;

        (bool Ok, string Type, string Detail) Verdict(string environment, string origin)
        {
            var parsed = parse.Invoke(null, [origin])!;
            var args = new object?[] { null, null };
            Assert.True((bool)parsed.GetType().GetMethod("TryGetValue")!.Invoke(parsed, args)!, $"{origin} did not parse");
            var result = check.Invoke(null, [environment, args[0]])!;
            var outcome = new object?[] { null, null };
            var ok = (bool)result.GetType().GetMethod("TryGetValue")!.Invoke(result, outcome)!;
            if (ok) return (true, string.Empty, string.Empty);
            var error = outcome[1]!;
            return (false, (string)error.GetType().GetProperty("Type")!.GetValue(error)!, (string)(error.GetType().GetProperty("Detail")!.GetValue(error) ?? string.Empty));
        }

        var refused = Verdict("Development", "https://forum.example");
        Assert.False(refused.Ok, "Check(Development, https://forum.example) said ok");
        Assert.Equal(GuardType, refused.Type);
        Assert.Equal(Filled("https://forum.example"), refused.Detail);
        Assert.True(Verdict("Production", "https://forum.example").Ok, "Check(Production, https://forum.example) refused");
        Assert.True(Verdict("Development", "http://localhost").Ok, "Check(Development, http://localhost) refused");
    }

    /// <summary>
    /// DEV2. The two-sided table, written from R11.109's grammar and not from <c>IsLoopback</c>. Every row is first started in <b>Production</b> and must start (so a row R11.109 refuses cannot pass as a guard
    /// refusal: trap 305). Then, in Development, each starts-row starts and each refused-row is refused with the guard's words and no other.
    /// Red on the tree: every refused-side row starts.
    /// </summary>
    [Fact]
    public async Task R11_112_DEV2_LoopbackStartsAndEveryOtherSpellingIsRefusedInDevelopment()
    {
        string[] starts =
        [
            "http://localhost", "http://localhost:5000", "https://localhost", "http://127.0.0.1", "http://127.0.0.1:8080", "http://127.8.9.10:8080", "http://[::1]", "http://[::1]:5000",
        ];
        string[] refused =
        [
            "http://forum.example", "https://forum.example", "https://198.51.100.10", "http://10.0.0.5", "http://0.0.0.0", "http://x.localhost", "http://localhost.example", "http://[2001:db8::1]",
            "http://[::ffff:127.0.0.1]", "http://128.0.0.1", "http://12.7.0.1", "http://127.example", "http://127.0.0.1.example", "http://1.example", "http://xn--bcher-kva.example",
        ];

        await WithEnvironmentAsync(Clean, async () =>
        {
            var failures = new List<string>();
            foreach (var origin in starts.Concat(refused))
            {
                var control = await StartAsync(origin, "Production");
                Expect(failures, control is null, $"control: {origin} did not start in Production, so a refusal of it in Development would not be the guard's: {control}");
            }

            foreach (var origin in starts)
            {
                var refusal = await StartAsync(origin, "Development");
                Expect(failures, refusal is null, $"starts-side: {origin} is loopback and was refused in Development: {refusal}");
            }

            foreach (var origin in refused)
            {
                var refusal = await StartAsync(origin, "Development");
                Expect(failures, refusal is not null, $"refused-side: {origin} is not loopback and the Forum started in Development");
                if (refusal is not null) Expect(failures, refusal.Contains(FilledOrMissing(origin), StringComparison.Ordinal), $"refused-side: {origin} was refused, but not with the guard's words: {refusal}");
            }

            NoFailures(failures, "R11.112: loopback is three spellings and no other");
        });
    }

    /// <summary>DEV3. The control that the rule is not "refuse public origins": Production, and a custom name (<c>Staging</c>), start with a public origin.</summary>
    [Fact]
    public async Task R11_112_DEV3_ProductionAndOtherNamesStartWithAPublicOrigin()
    {
        await WithEnvironmentAsync(Clean, async () =>
        {
            var failures = new List<string>();
            foreach (var environment in NotDevelopment)
            {
                var refusal = await StartAsync("https://forum.example", environment);
                Expect(failures, refusal is null, $"{environment} with https://forum.example was refused: {refusal}");
            }

            NoFailures(failures, "R11.112: only Development is refused");
        });
    }

    /// <summary>
    /// DEV4. The environment's case. Through <c>UseEnvironment</c>: <c>development</c> and <c>DEVELOPMENT</c> are refused with a public origin and start with a loopback one (the host's precedence of its two variables
    /// is DEV4b's, in a real process). Red on the tree: every refused row starts.
    /// </summary>
    [Fact]
    public async Task R11_112_DEV4_TheEnvironmentsCaseIsComparedAsTheHostComparesIt()
    {
        var failures = new List<string>();
        await WithEnvironmentAsync(Clean, async () =>
        {
            foreach (var environment in OtherCases)
            {
                var refusal = await StartAsync("https://forum.example", environment);
                Expect(failures, refusal is not null && refusal.Contains(FilledOrMissing("https://forum.example"), StringComparison.Ordinal), $"{environment} with a public origin was not refused with the guard's words: {refusal ?? "it started"}");
                var loopback = await StartAsync("http://localhost", environment);
                Expect(failures, loopback is null, $"{environment} with http://localhost was refused: {loopback}");
            }
        });

        NoFailures(failures, "R11.112: the guard agrees with the host about which environment it is in");
    }

    /// <summary>
    /// DEV4b. The host's precedence through <b>real process variables</b>, in a real <c>Curia.Api</c> process (the factory builds every in-process host in Development whatever the variables say, so no in-process
    /// row can show a variable at work). The process is given a public origin, a dead database and a key that is not a PEM, and is read for whether the guard's words came out: <c>DOTNET_ENVIRONMENT=Development</c>
    /// beside <c>ASPNETCORE_ENVIRONMENT=Production</c> is refused, the reverse is not, <c>DOTNET_ENVIRONMENT=Development</c> alone is refused, <c>ASPNETCORE_ENVIRONMENT=development</c> and <c>Development</c> alone are refused,
    /// neither set is not, and <c>Staging</c> is not. A row that is not refused by the guard fails later for its own reasons (the dead database, the key): the live run (LDEV3 to LDEV6) is where a Forum is shown to start.
    /// Red on the tree: the refused rows are not refused.
    /// </summary>
    [Fact]
    public async Task R11_112_DEV4b_TheHostsPrecedenceHoldsInARealProcess()
    {
        var rows = new (string Name, (string Name, string Value)[] Variables, bool Refused)[]
        {
            ("DOTNET=Development beside ASPNETCORE=Production", [("DOTNET_ENVIRONMENT", "Development"), ("ASPNETCORE_ENVIRONMENT", "Production")], true),
            ("ASPNETCORE=Development beside DOTNET=Production", [("ASPNETCORE_ENVIRONMENT", "Development"), ("DOTNET_ENVIRONMENT", "Production")], false),
            ("DOTNET=Development alone", [("DOTNET_ENVIRONMENT", "Development")], true),
            ("DOTNET=development alone", [("DOTNET_ENVIRONMENT", "development")], true),
            ("ASPNETCORE=development alone", [("ASPNETCORE_ENVIRONMENT", "development")], true),
            ("ASPNETCORE=Development alone", [("ASPNETCORE_ENVIRONMENT", "Development")], true),
            ("ASPNETCORE=Staging alone", [("ASPNETCORE_ENVIRONMENT", "Staging")], false),
            ("neither set", [], false),
        };
        var failures = new List<string>();
        foreach (var (name, variables, refused) in rows)
        {
            var output = await RunForumProcessAsync("https://forum.example", variables);
            var guard = output.Contains(FilledOrMissing("https://forum.example"), StringComparison.Ordinal);
            if (refused) Expect(failures, guard, $"{name}: expected the guard's refusal in the process's output, got: {Trim(output)}");
            else Expect(failures, !guard, $"{name}: the guard refused a Forum that is not in Development: {Trim(output)}");
        }

        NoFailures(failures, "R11.112: the guard agrees with the host about which environment it is in, whichever variable selected it");
    }

    private static string Trim(string text) => text.Length > 300 ? text[..300] + "..." : text;

    /// <summary>A real <c>Curia.Api</c> with a public origin, a dead database and no usable key, in an environment holding only what is given (and none of this process's own); its output when it exits, killed after a minute.</summary>
    private static async Task<string> RunForumProcessAsync(string origin, (string Name, string Value)[] variables)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Curia.Api.dll"));
        foreach (var inherited in start.Environment.Keys.Where(k => k.StartsWith("ASPNETCORE_", StringComparison.Ordinal) || (k.StartsWith("DOTNET_", StringComparison.Ordinal) && k != "DOTNET_ROOT") || k.StartsWith("CURIA_", StringComparison.Ordinal)).ToList())
            start.Environment.Remove(inherited);
        start.Environment["CURIA_PUBLIC_ORIGIN"] = origin;
        start.Environment["CURIA_EVENTS_POSTGRES"] = "Host=127.0.0.1;Port=1;Database=nowhere;Username=nobody;Timeout=1;Command Timeout=1";
        start.Environment["CURIA_ISSUER_SIGNING_KEY_PEM"] = "this is not a PEM";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        foreach (var (name, value) in variables) start.Environment[name] = value;

        using var process = System.Diagnostics.Process.Start(start)!;
        var output = Task.WhenAll(process.StandardOutput.ReadToEndAsync(), process.StandardError.ReadToEndAsync());
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        return string.Concat(await output);
    }

    /// <summary>
    /// DEV5. The order. Development, a public origin, and every other start failure at once (a connection string that cannot connect, a signing key that is not a PEM, the production embedder with no model
    /// directory): the refusal is the guard's, so the guard precedes every store and key read that could fail first. And an origin R11.109 refuses, with the pair Development and a public origin otherwise
    /// present, is refused in R11.109's words, so the origin must parse before it can be called public. Red on the tree: the refusal is another.
    /// </summary>
    [Fact]
    public async Task R11_112_DEV5_TheGuardPrecedesEveryStoreAndKeyRead_AndR11_109ParsesFirst()
    {
        await WithEnvironmentAsync(Clean_(("CURIA_EMBEDDING_MODEL_DIR", null)), async () =>
        {
            var failures = new List<string>();
            var allWrong = new GuardProbeFixture(
                "https://forum.example", "Development",
                afterConfigure: b =>
                {
                    b.UseSetting("Curia:IssuerSigningKeyPem", "this is not a PEM");
                },
                productionEmbedder: true,
                connectionString: "Host=127.0.0.1;Port=1;Database=nowhere;Username=nobody;Timeout=1;Command Timeout=1");
            var refusal = await StartAsync(allWrong);
            Expect(failures, refusal is not null && refusal.Contains(FilledOrMissing("https://forum.example"), StringComparison.Ordinal), $"every start failure at once: expected the guard's refusal first, got: {refusal ?? "it started"}");

            foreach (var origin in R109Refuses)
            {
                var other = await StartAsync(origin, "Development");
                Expect(failures, other is not null && other.Contains("Curia:PublicOrigin", StringComparison.Ordinal) && !other.Contains("developer exception page", StringComparison.Ordinal),
                    $"{origin} (R11.109 refuses it): expected R11.109's words and not the guard's, got: {other ?? "it started"}");
            }

            var absent = await StartAsync(new GuardProbeFixture(null, "Development", afterConfigure: b => b.UseSetting("Curia:PublicOrigin", null)));
            Expect(failures, absent is not null && absent.Contains("No public origin configured", StringComparison.Ordinal), $"no origin: expected R11.109's words, got: {absent ?? "it started"}");
            NoFailures(failures, "R11.112: before a store or a key, after the origin parses");
        });
    }

    /// <summary>
    /// DEV6. One definition. The runbook quotes the refusal with the origin <c>https://forum.example</c>, and the quotation is the source's <c>Words</c> with the hole filled: an edit to either alone fails the build.
    /// The words name both variables and Production and the setting for local work, whatever else they say. Red on the tree: the type and the quotation do not exist.
    /// </summary>
    [Fact]
    public void R11_112_DEV6_TheRunbookQuotesTheSourcesWordsAndTheWordsNameTheWayOut()
    {
        var words = Words();
        var runbook = File.ReadAllText(Path.Combine(Curia.Tests.Shared.PublishedEdge.RepoRoot(), "docs", "runbooks", "hosted-instance.md"));
        var quoted = FilledOrMissing("https://forum.example");
        Assert.True(runbook.Contains(quoted, StringComparison.Ordinal), $"docs/runbooks/hosted-instance.md does not quote the refusal as the source words it: {quoted}");
        foreach (var needed in new[] { "{Origin}", "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT", "Production", "CURIA_PUBLIC_ORIGIN", "R11.112" })
            Assert.True(words.Contains(needed, StringComparison.Ordinal), $"the refusal's words do not name {needed}");
        Assert.DoesNotContain("@", words, StringComparison.Ordinal);
    }

    /// <summary>
    /// DEV6b. A value that carries userinfo never reaches the guard: R11.109 refuses it first and quotes nothing, so the guard's words (which quote the origin) cannot echo a password. Red on the tree: the
    /// refusal is not the one that says nothing is quoted... the Forum starts.
    /// </summary>
    [Fact]
    public async Task R11_112_DEV6b_AValueWithUserinfoNeverReachesTheGuard()
    {
        await WithEnvironmentAsync(Clean, async () =>
        {
            var refusal = await StartAsync("https://svc:s3cretpw@forum.example", "Development");
            Assert.True(refusal is not null, "a Forum with a userinfo origin started");
            Assert.DoesNotContain("s3cretpw", refusal, StringComparison.Ordinal);
            Assert.DoesNotContain("developer exception page", refusal, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// DEV9. The suite can fail on this: a fixture that holds a public origin and does not set its environment (so it is Development, as the factory builds it) fails its start with the guard's words, and not
    /// R11.109's and not a store's. Red on the tree: it starts.
    /// </summary>
    [Fact]
    public async Task R11_112_DEV9_AFixtureThatForgetsFailsItsOwnStartWithTheGuardsWords()
    {
        await WithEnvironmentAsync(Clean, async () =>
        {
            await using var fixture = new ForgetfulFixture();
            await fixture.InitializeAsync();
            var failure = Record.Exception(() => fixture.Services);
            Assert.True(failure is not null, "a fixture with a public origin and the factory's Development environment started: the suite cannot fail on the shape the rule refuses");
            Assert.True(Chain(failure).Contains(FilledOrMissing("https://forum.example"), StringComparison.Ordinal), $"the failure is not the guard's words: {Chain(failure)}");
        });
    }

    /// <summary>
    /// DEV10. The same exception path as R11.109: the guard's failure and R11.109's absent-origin failure are both <see cref="InvalidOperationException"/> thrown by the one factory, so item 393's later fix
    /// covers both; if either changes type, this says which. Red on the tree: the guard's start does not fail.
    /// </summary>
    [Fact]
    public async Task R11_112_DEV10_TheGuardsFailureIsTheSameExceptionTypeAsR11_109s()
    {
        await WithEnvironmentAsync(Clean, async () =>
        {
            async Task<Exception?> Fail(GuardProbeFixture fixture)
            {
                await using (fixture)
                {
                    await fixture.InitializeAsync();
                    return Record.Exception(() => fixture.Services);
                }
            }

            static Exception Root(Exception e) { while (e.InnerException is not null && e is not InvalidOperationException) e = e.InnerException; return e; }

            var guard = await Fail(new GuardProbeFixture("https://forum.example", "Development"));
            var absent = await Fail(new GuardProbeFixture(null, "Development", afterConfigure: b => b.UseSetting("Curia:PublicOrigin", null)));
            Assert.True(guard is not null, "the guard's start did not fail");
            Assert.True(absent is not null, "R11.109's absent-origin start did not fail");
            Assert.Equal(typeof(InvalidOperationException), Root(absent!).GetType());
            Assert.Equal(typeof(InvalidOperationException), Root(guard!).GetType());
        });
    }
}
