using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Curia.Api.Tests.OriginProbe;

namespace Curia.Api.Tests;

/// <summary>The one collection that writes a process environment variable (ORG1's fallback row), so that no other host starts beside it.</summary>
[CollectionDefinition("origin-env", DisableParallelization = true)]
public sealed class OriginEnvironmentGroup;

/// <summary>
/// <c>Curia.Api.PublicOrigin</c> as the probes read it: <b>by reflection</b>, so that a probe written before the type exists is red by an assertion that says it does not, and not by a compile error (the plan's
/// Task 2). The shape is the plan's Task 3: <c>Parse(string?)</c> returning a <c>Result&lt;PublicOrigin&gt;</c>, a <c>Value</c>, and two names the docs are held to.
/// </summary>
internal static class PublicOriginReflection
{
    private const string TypeName = "Curia.Api.PublicOrigin";

    internal static Type Type() =>
        typeof(Program).Assembly.GetType(TypeName)
        ?? throw new Xunit.Sdk.XunitException($"{TypeName} does not exist: nothing in the Forum parses the origin it is told, so the grammar of R11.109 is not held anywhere");

    /// <summary>Whether <paramref name="text"/> parses, the parsed value as text, and what the refusal said (its type, title and detail joined).</summary>
    internal static (bool Ok, string? Value, string Message) Parse(string? text)
    {
        var parse = Type().GetMethod("Parse", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(string)])
            ?? throw new Xunit.Sdk.XunitException($"{TypeName}.Parse(string) does not exist");
        var result = parse.Invoke(null, [text])!;
        var arguments = new object?[] { null, null };
        var ok = (bool)result.GetType().GetMethod("TryGetValue")!.Invoke(result, arguments)!;
        if (ok) return (true, (string?)arguments[0]!.GetType().GetProperty("Value")!.GetValue(arguments[0]), string.Empty);

        var error = arguments[1]!;
        string Member(string name) => (string?)error.GetType().GetProperty(name)!.GetValue(error) ?? string.Empty;
        return (false, null, $"{Member("Type")} {Member("Title")} {Member("Detail")}");
    }

    /// <summary>The string constant the type declares under <paramref name="name"/> (the configuration key and the environment variable, as the code spells them).</summary>
    internal static string Constant(string name) =>
        (string?)Type().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue()
        ?? throw new Xunit.Sdk.XunitException($"{TypeName}.{name} does not exist");
}

/// <summary>
/// R11.109 over the real host (the plan's ORG1 and ORG3) and over the parser (ORG2). <b>Red on the tree by their own assertions</b>: the unmodified Forum starts with no origin, starts with a replaced key
/// set, and parses nothing.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("origin-env")]
public sealed class PublicOriginStartTests
{
    private const string EnvironmentVariable = "CURIA_PUBLIC_ORIGIN";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every message in an exception chain, so a refusal wrapped by the host's own builders is still read whole.</summary>
    private static string Chain(Exception? exception)
    {
        var messages = new List<string>();
        for (var e = exception; e is not null; e = e.InnerException) messages.Add(e.GetType().Name + ": " + e.Message);
        return string.Join(" | ", messages);
    }

    private static async Task<string?> StartAsync(Action<IWebHostBuilder> setOrigin)
    {
        await using var fixture = new OriginSettingFixture(setOrigin);
        await fixture.InitializeAsync();
        var failure = Record.Exception(() => fixture.Services);
        return failure is null ? null : Chain(failure);
    }

    /// <summary>
    /// ORG1. A host told no origin, an empty one and a whitespace one fails <b>at host construction</b>, before any request, with a message that names <c>Curia:PublicOrigin</c> and <c>CURIA_PUBLIC_ORIGIN</c>.
    /// Red on the tree: the host starts (and mints tokens for a name nobody chose).
    /// </summary>
    [Fact]
    public async Task R11_109_ORG1_AHostWithNoOriginDoesNotStart()
    {
        var previous = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Environment.SetEnvironmentVariable(EnvironmentVariable, null);
        try
        {
            var failures = new List<string>();
            foreach (var (name, setting) in new (string, Action<IWebHostBuilder>)[]
            {
                ("no setting", _ => { }),
                ("an empty setting", b => b.UseSetting("Curia:PublicOrigin", string.Empty)),
                ("a whitespace setting", b => b.UseSetting("Curia:PublicOrigin", "   ")),
            })
            {
                var refusal = await StartAsync(setting);
                Expect(failures, refusal is not null, $"{name}: expected the host to refuse to start, and it started");
                if (refusal is null) continue;
                Expect(failures, refusal.Contains("Curia:PublicOrigin", StringComparison.Ordinal) && refusal.Contains(EnvironmentVariable, StringComparison.Ordinal),
                    $"{name}: expected the refusal to name Curia:PublicOrigin and {EnvironmentVariable}, it says: {refusal}");
            }

            NoFailures(failures, "R11.109: the Forum does not start without its origin");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// ORG1, the fallback: with no setting and the variable set, the host starts <b>and the variable is the name</b> (a token it mints carries it). The configuration key is read first and the variable second,
    /// the order every sibling uses. Red on the tree (the token says <c>https://forum.local</c>).
    /// </summary>
    [Fact]
    public async Task R11_109_ORG1_TheEnvironmentVariableIsTheNameWhenNoSettingIsGiven()
    {
        var previous = Environment.GetEnvironmentVariable(EnvironmentVariable);
        Environment.SetEnvironmentVariable(EnvironmentVariable, "http://localhost");
        try
        {
            await using var fixture = new OriginSettingFixture(_ => { });
            await fixture.InitializeAsync();
            var (_, dpop) = await EnrolledAsync(fixture, "org1env", Ct);
            using var client = fixture.Client;
            var token = AccessToken(await TokenAsync(client, "/oauth/token", dpop, "http://localhost/oauth/token", "http://localhost/oauth/token", fixture.Now, Ct));
            Assert.True(token is not null, "a host told its name only by CURIA_PUBLIC_ORIGIN did not mint a token for credentials made for it");
            Assert.Equal("http://localhost", (string?)Claims(token!)["iss"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// ORG3. <c>Curia:Issuer</c>, <c>Curia:Audience</c>, or both, set beside a good origin: the start is refused, naming <c>Curia:PublicOrigin</c>. A key the build no longer reads is never silently ignored.
    /// Red on the tree: both keys are the keys it reads.
    /// </summary>
    [Fact]
    public async Task R11_109_ORG3_AReplacedKeyRefusesTheStart()
    {
        var failures = new List<string>();
        foreach (var (name, keys) in new (string, string[])[] { ("Curia:Issuer", ["Curia:Issuer"]), ("Curia:Audience", ["Curia:Audience"]), ("both", ["Curia:Issuer", "Curia:Audience"]) })
        {
            var refusal = await StartAsync(b =>
            {
                b.UseSetting("Curia:PublicOrigin", "http://localhost");
                foreach (var key in keys) b.UseSetting(key, "https://old.example");
            });
            Expect(failures, refusal is not null, $"{name} set: expected the host to refuse to start, and it started");
            if (refusal is not null)
                Expect(failures, refusal.Contains("Curia:PublicOrigin", StringComparison.Ordinal), $"{name} set: expected the refusal to name Curia:PublicOrigin, it says: {refusal}");
        }

        NoFailures(failures, "R11.109: a key the build has stopped reading is refused, never ignored");
    }

    /// <summary>
    /// ORG2, the grammar. Each value is refused with the canonical form it would have been accepted as named (where that can be said without the offending part) and the offender quoted, never echoed raw; a value
    /// carrying userinfo is <b>never quoted at all</b> (amendment 1, #6). Every accepted form is checked against <b>the client's own builder</b>: <c>new Uri(new Uri(origin), "/oauth/token").ToString()</c> must equal
    /// <c>origin + "/oauth/token"</c>, so the Forum's string is the string the client signs. The round trip does not see a trailing dot or userinfo (T0-3), so those rows belong to the parser's own rules and the row
    /// names which check owns it. Red on the tree: <c>Curia.Api.PublicOrigin</c> does not exist.
    /// </summary>
    [Fact]
    public void R11_109_ORG2_TheGrammarRefusesWhatIsNotItsOwnCanonicalForm()
    {
        var accepted = new[]
        {
            "http://localhost", "https://forum.example", "http://127.0.0.1:5000", "https://forum.example:8443", "http://[::1]:5000", "http://[2001:db8::1]:5000",
            "http://xn--bcher-kva.example", "http://xn--bcher-kva.example:8443",
        };
        var failures = new List<string>();
        foreach (var origin in accepted)
        {
            var (ok, value, message) = PublicOriginReflection.Parse(origin);
            Expect(failures, ok && value == origin, $"accepted {origin}: the parser said {(ok ? "ok with " + value : message)}");
            Expect(failures, new Uri(new Uri(origin), "/oauth/token").ToString() == origin + "/oauth/token", $"accepted {origin}: the client signs {new Uri(new Uri(origin), "/oauth/token")}, which is not {origin}/oauth/token");
        }

        // (value, canonical form the refusal names or null, the check that owns the row: "grammar", "round trip" or "both")
        var refused = new (string Value, string? Canonical, string Owner)[]
        {
            ("https://forum.example/", "https://forum.example", "both"),
            ("https://forum.example/path", "https://forum.example", "both"),
            ("https://forum.example?x=1", "https://forum.example", "both"),
            ("https://forum.example#f", "https://forum.example", "both"),
            ("HTTPS://forum.example", "https://forum.example", "both"),
            ("https://Forum.Example", "https://forum.example", "both"),
            ("https://forum.example:443", "https://forum.example", "both"),
            ("http://forum.example:80", "http://forum.example", "both"),
            ("http://forum.example.", "http://forum.example", "grammar"),
            ("http://forum.example.:8443", "http://forum.example:8443", "grammar"),
            ("http://forum_x.example", null, "grammar"),
            ("ftp://forum.example", null, "both"),
            ("forum.example", null, "both"),
            ("/forum", null, "both"),
            (" http://forum.example", null, "both"),
            ("http://forum.example ", null, "both"),
            ("http://127.1", null, "round trip"),
            ("http://0x7f.1", null, "round trip"),
            ("http://[0:0:0:0:0:0:0:1]", null, "round trip"),
            ("http://[2001:DB8::1]:5000", null, "round trip"),
        };
        foreach (var (value, canonical, owner) in refused)
        {
            var (ok, _, message) = PublicOriginReflection.Parse(value);
            Expect(failures, !ok, $"refused ({owner}) {value}: the parser accepted it");
            if (ok) continue;
            if (canonical is not null) Expect(failures, message.Contains(canonical, StringComparison.Ordinal), $"refused ({owner}) {value}: expected the refusal to name {canonical}, it says: {message}");
        }

        // Added by Task 6 (FORG11a): a trailing dot is refused for its own reason and says so; the empty-label rule would refuse it too, so refusal alone does not show the dot rule is there.
        foreach (var value in new[] { "http://forum.example.", "http://forum.example.:8443" })
        {
            var (_, _, message) = PublicOriginReflection.Parse(value);
            Expect(failures, message.Contains("ends in a dot", StringComparison.Ordinal), $"refused (grammar) {value}: expected the refusal to say the host ends in a dot, it says: {message}");
        }

        // Userinfo: refused, and nothing of the value is quoted (R10.27's shape: a configured password must not reach the start log).
        foreach (var value in new[] { "http://svc:s3cretpw@forum.example", "http://svc@forum.example", "http://:@forum.example" })
        {
            var (ok, _, message) = PublicOriginReflection.Parse(value);
            Expect(failures, !ok, $"refused (grammar) {value}: the parser accepted it");
            Expect(failures, !message.Contains("s3cretpw", StringComparison.Ordinal) && !message.Contains("svc", StringComparison.Ordinal), $"refused (grammar) {value}: the refusal quotes the userinfo: {message}");
        }

        // A U-label host is refused (configure the A-label, non-property (i)); it is signed as a U-label and sent as an A-label (T0-3).
        var (idnOk, _, _) = PublicOriginReflection.Parse("http://bücher.example");
        Expect(failures, !idnOk, "refused (both) a U-label host: the parser accepted it");
        var (nullOk, _, _) = PublicOriginReflection.Parse(null);
        Expect(failures, !nullOk, "refused: a null value was accepted");
        NoFailures(failures, "R11.109: the origin is its own canonical form and nothing else");
    }

    /// <summary>
    /// ORG14. The README's "Running it" and <c>CLAUDE.md</c> name <c>CURIA_PUBLIC_ORIGIN</c> and <c>Curia:PublicOrigin</c> <b>as the code spells them</b> (the constants are read by reflection from
    /// <c>Curia.Api.PublicOrigin</c>, the docs as text), the paragraph of <c>CLAUDE.md</c> that names how the Forum is started names the origin, and neither document offers <c>Curia:Issuer</c> or
    /// <c>Curia:Audience</c> as a way to set the name. Red on the tree.
    /// </summary>
    [Fact]
    public void R11_109_ORG14_TheDocsSayHowTheForumIsToldItsName()
    {
        var key = PublicOriginReflection.Constant("ConfigurationKey");
        var variable = PublicOriginReflection.Constant("EnvironmentVariable");
        var readme = File.ReadAllText(RepoFile("README.md"));
        var claude = File.ReadAllText(RepoFile("CLAUDE.md"));
        var failures = new List<string>();
        foreach (var (name, text) in new[] { ("README.md", readme), ("CLAUDE.md", claude) })
        {
            Expect(failures, text.Contains(variable, StringComparison.Ordinal), $"{name} does not name {variable}");
            Expect(failures, text.Contains(key, StringComparison.Ordinal), $"{name} does not name {key}");
        }

        Expect(failures, !readme.Contains("Curia:Issuer", StringComparison.Ordinal) && !readme.Contains("Curia:Audience", StringComparison.Ordinal), "README.md names Curia:Issuer or Curia:Audience");
        foreach (var paragraph in claude.Split("\n\n"))
        {
            var startsTheForum = paragraph.Contains("CURIA_EVENTS_POSTGRES", StringComparison.Ordinal) && paragraph.Contains("CURIA_ISSUER_SIGNING_KEY_PEM", StringComparison.Ordinal);
            if (startsTheForum && paragraph.Contains("startup fails loudly", StringComparison.Ordinal))
                Expect(failures, paragraph.Contains(variable, StringComparison.Ordinal), "CLAUDE.md's paragraph on running the Forum names the start variables and not " + variable);
            foreach (var old in new[] { "Curia:Issuer", "Curia:Audience" })
                if (paragraph.Contains(old, StringComparison.Ordinal))
                    Expect(failures, paragraph.Contains("refus", StringComparison.Ordinal) || paragraph.Contains("replac", StringComparison.Ordinal), $"CLAUDE.md names {old} without saying it is refused or replaced: {paragraph[..Math.Min(120, paragraph.Length)]}");
        }

        NoFailures(failures, "R11.109: the docs name the origin's setting as the code spells it");
    }

    /// <summary>
    /// ORG14b (Task 6, FSPEC): the errata's paragraphs for R5.27 and R11.109 spell what the code does: the refusal's problem type and the rule for an explicit default port, and the two settings and the two
    /// refused keys. An edit of the text that the code does not follow (or the reverse) was read by no probe before this one (FSPECb, FSPECc and FSPECd survived <c>check-spec.py</c> and every test).
    /// </summary>
    [Fact]
    public void R11_109_ORG14b_TheErrataSpellWhatTheCodeDoes()
    {
        var errata = File.ReadAllLines(RepoFile("curia-whitepaper-ERRATA-AND-ADDENDUM.md"));
        string Paragraph(string id)
        {
            var found = errata.Where(l => l.StartsWith($"**{id}** ", StringComparison.Ordinal)).ToArray();
            Assert.True(found.Length == 1, $"the errata have {found.Length} paragraphs that begin **{id}**, expected one");
            return found[0];
        }

        var hostRefusalType = (string?)typeof(Program).Assembly.GetType("Curia.Api.HostNotServed")
            ?.GetField("Type", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)?.GetRawConstantValue()
            ?? throw new Xunit.Sdk.XunitException("Curia.Api.HostNotServed.Type does not exist");
        var key = PublicOriginReflection.Constant("ConfigurationKey");
        var variable = PublicOriginReflection.Constant("EnvironmentVariable");
        var failures = new List<string>();

        var host = Paragraph("R5.27");
        Expect(failures, host.Contains($"`{hostRefusalType}`", StringComparison.Ordinal), $"R5.27 does not name the refusal's type {hostRefusalType}");
        Expect(failures, host.Contains("ASCII case-insensitively", StringComparison.Ordinal), "R5.27 does not say the comparison is ASCII case-insensitive");
        Expect(failures, host.Contains("being the same as none", StringComparison.Ordinal), "R5.27 does not say an explicit default port is the same as none");

        var start = Paragraph("R11.109");
        foreach (var name in new[] { key, variable, "Curia:Issuer", "Curia:Audience" })
            Expect(failures, start.Contains($"`{name}`", StringComparison.Ordinal), $"R11.109 does not name {name}");
        Expect(failures, start.IndexOf(key, StringComparison.Ordinal) < start.IndexOf(variable, StringComparison.Ordinal), $"R11.109 does not read {key} before {variable}");

        NoFailures(failures, "R5.27 and R11.109 spell what the code does");
    }

    private static string RepoFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, name))) dir = dir.Parent;
        return dir is null ? throw new InvalidOperationException(name + " not found") : Path.Combine(dir.FullName, name);
    }
}

/// <summary>
/// ORG15 (ruling 8): every fixture names itself truly. Each <see cref="ForumFixture"/> subclass of the test assembly that can be built without arguments, started, hands out a client that dials the authority of
/// its <see cref="ForumFixture.PublicOrigin"/> (its <c>BaseAddress</c>, or the <c>Host</c> it forces), and the host received exactly that origin as its setting. <b>Green once Task 2's conversion lands, on the
/// unmodified Forum</b>, and red the moment a fixture sets one origin and dials another (FORG15). It is what makes D41's own sentence, "every fixture's base address equals the configured origin", a thing the
/// suite holds.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FixtureNamesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R5_26_ORG15_EveryFixtureDialsTheNameItsHostWasConfiguredWith()
    {
        var fixtures = typeof(ForumFixture).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsPublic: true } && t.IsSubclassOf(typeof(ForumFixture)) && t.GetConstructor(Type.EmptyTypes) is not null)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .ToArray();
        Assert.NotEmpty(fixtures);
        var failures = new List<string>();
        foreach (var type in fixtures)
        {
            await using var fixture = (ForumFixture)Activator.CreateInstance(type)!;
            await fixture.InitializeAsync();
            using var client = fixture.Client;
            var dialled = client.DefaultRequestHeaders.Host ?? client.BaseAddress!.Authority;
            var named = new Uri(fixture.PublicOrigin).Authority;
            var configured = fixture.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Curia:PublicOrigin"];
            Expect(failures, string.Equals(dialled, named, StringComparison.Ordinal), $"{type.Name}: its client dials {dialled} and its PublicOrigin is {fixture.PublicOrigin}");
            Expect(failures, string.Equals(configured, fixture.PublicOrigin, StringComparison.Ordinal), $"{type.Name}: its PublicOrigin is {fixture.PublicOrigin} and the host received {configured ?? "no setting"}");
        }

        NoFailures(failures, $"R5.26 (ruling 8): fixtures checked: {fixtures.Length}");
        _ = Ct;
    }
}
