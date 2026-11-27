using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// The deployment pack's document probes (errata G83; plan <c>docs/superpowers/plans/2026-11-26-the-deployment-pack.md</c>): every command in a runbook is read against the tool that runs it, every
/// configuration file against the rule it carries, each from an artifact the document is not (the shipped binaries' help, the source's variable names, the errata's table, the white paper's R6.39,
/// the source's string literals). The shipped binaries are <b>run</b> the way an operator runs them (trap 303): <c>curia-operator --help</c> exits 3 without a database variable, and DPK1 and DPK5
/// supply a dummy one in the child's environment only, DPK1b holding the defect (item 397 (g)) so that it cannot be fixed with the probes still papering over it.
/// DPK1 to DPK5, DPK7 and DPK9 live here; DPK6 (the restore post-check's SQL) is in <c>Curia.Infrastructure.Tests</c> and DPK8 (the monitor line) in <c>Curia.Api.Tests</c>.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class DeploymentDocsTests
{
    private static readonly string[] NewRunbooks = ["hosted-instance", "reverse-proxy", "head-timer", "operator-keys", "backup-restore"];

    private static string Root => PublishedEdge.RepoRoot();

    private static string Text(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    private static string Flat(string text) => Whitespace().Replace(text, " ");

    private static string Runbook(string name) => Text($"docs/runbooks/{name}.md");

    private static IEnumerable<string> AllRunbookPaths() => Directory.GetFiles(Path.Combine(Root, "docs", "runbooks"), "*.md").Order(StringComparer.Ordinal);

    /// <summary>The bodies of every fenced block of a markdown text, whatever its language.</summary>
    private static List<string> Fences(string markdown) => FencePattern().Matches(markdown).Select(m => m.Groups[1].Value).ToList();

    private static List<string> FencesOfLanguage(string markdown, string language) =>
        Regex.Matches(markdown, "```" + language + "\n(.*?)\n```", RegexOptions.Singleline, TimeSpan.FromSeconds(5)).Select(m => m.Groups[1].Value).ToList();

    /// <summary>The README's hosting section, or an empty string when it has none.</summary>
    private static string ReadmeHostingSection()
    {
        var readme = Text("README.md");
        var start = readme.IndexOf("\n## Hosting a beta instance", StringComparison.Ordinal);
        if (start < 0) return string.Empty;
        var end = readme.IndexOf("\n## ", start + 4, StringComparison.Ordinal);
        return end < 0 ? readme[start..] : readme[start..end];
    }

    private sealed record Run(int Exit, string Out, string Err);

    /// <summary>One of the shipped binaries beside this test, run as <c>dotnet &lt;name&gt;.dll</c> in an environment holding no <c>CURIA_*</c> or <c>Curia__*</c> variable but the ones given.</summary>
    private static async Task<Run> RunShippedAsync(string assembly, string[] args, Dictionary<string, string>? env = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assembly + ".dll");
        Assert.True(File.Exists(path), $"{assembly}.dll is not beside this test");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(path);
        foreach (var a in args) start.ArgumentList.Add(a);
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("CURIA_", StringComparison.Ordinal) || k.StartsWith("Curia__", StringComparison.Ordinal)).ToList())
            start.Environment.Remove(key);
        foreach (var (k, v) in env ?? []) start.Environment[k] = v;
        start.Environment["HOME"] = Path.GetTempPath();
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"could not start {assembly}");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        return new Run(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    private static Task<Run> OperatorHelpAsync() =>
        RunShippedAsync("curia-operator", ["--help"], new Dictionary<string, string> { ["CURIA_EVENTS_POSTGRES"] = "Host=unused" });

    /// <summary>The command lines of a fenced block: backslash continuations joined, comments and blank lines dropped, a leading prompt and environment assignments left in place.</summary>
    private static IEnumerable<string> CommandLines(string fence)
    {
        var joined = fence.Replace("\\\n", " ", StringComparison.Ordinal);
        foreach (var line in joined.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith('#')) continue;
            yield return t;
        }
    }

    private static bool IsNewRunbook(string relative) => NewRunbooks.Any(n => string.Equals(relative, $"docs/runbooks/{n}.md", StringComparison.Ordinal));

    private static void RequireRunbooksPresent(List<string> problems)
    {
        foreach (var name in NewRunbooks)
            if (Runbook(name).Trim().Length == 0)
                problems.Add($"docs/runbooks/{name}.md is empty");
    }

    // ---------------------------------------------------------------- DPK1

    /// <summary>
    /// DPK1. Every <c>curia-operator &lt;verb&gt;</c> in every runbook and in the README's fences is a verb the shipped tool's <c>--help</c> names; every variable in a fence that runs the operator is one
    /// that help names; the five runbooks between them name the verbs the pack needs; and the hosting runbook says, in one sentence, that the help needs the database variable set to any value (P3 (c)).
    /// Red on the seam: a runbook naming a removed verb, or one that is empty.
    /// </summary>
    [Fact]
    public async Task G83_DPK1_EveryOperatorVerbAndVariableInTheRunbooksIsOneTheShippedToolNames()
    {
        var problems = new List<string>();
        RequireRunbooksPresent(problems);
        var help = await OperatorHelpAsync().ConfigureAwait(true);
        if (help.Exit != 0) problems.Add($"curia-operator --help exits {help.Exit} even with CURIA_EVENTS_POSTGRES set to a dummy value, not 0");

        var documents = AllRunbookPaths().Select(p => (Name: Path.GetRelativePath(Root, p), Fences: Fences(File.ReadAllText(p))))
            .Append((Name: "README.md", Fences: Fences(Text("README.md")))).ToList();
        var named = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var (name, fences) in documents)
        {
            foreach (var fence in fences.Where(f => f.Contains("curia-operator", StringComparison.Ordinal)))
            {
                foreach (var line in CommandLines(fence).Where(l => l.Contains("curia-operator ", StringComparison.Ordinal)))
                {
                    var m = OperatorVerb().Match(line);
                    if (!m.Success) continue;
                    if (IsNewRunbook(name)) named.Add(m.Groups[1].Value);
                    if (!help.Out.Contains($"curia-operator {m.Groups[1].Value}", StringComparison.Ordinal))
                        problems.Add($"{name} runs curia-operator {m.Groups[1].Value}, which --help does not name");
                }

                foreach (var variable in VariableName().Matches(fence).Select(m => m.Value).Distinct(StringComparer.Ordinal))
                    if (!help.Out.Contains(variable, StringComparison.Ordinal))
                        problems.Add($"{name}'s operator commands name {variable}, which --help does not");
            }
        }

        foreach (var needed in new[] { "sign-head", "lifecycle-audit", "publish-declaration-key", "publish-operator-key", "attest-owner" })
            if (!named.Contains(needed))
                problems.Add($"no runbook runs curia-operator {needed}");

        var hosting = Flat(Runbook("hosted-instance"));
        if (!HelpNeedsVariableSentence().IsMatch(hosting))
            problems.Add("hosted-instance.md does not say that curia-operator --help needs CURIA_EVENTS_POSTGRES set to any value");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// DPK1b, a known-defect row (item 397 (g), trap 303): the shipped binary run without <c>CURIA_EVENTS_POSTGRES</c> exits 3 on <c>--help</c>. It passes today and goes red the day the operator's
    /// help works offline; that day its author deletes the dummy from DPK1 and DPK5, this row and the runbook's sentence.
    /// </summary>
    [Fact]
    public async Task G83_DPK1b_TheOperatorsHelpStillNeedsTheDatabaseVariable()
    {
        var run = await RunShippedAsync("curia-operator", ["--help"]).ConfigureAwait(true);
        Assert.True(
            run.Exit == 3 && run.Err.Contains("CURIA_EVENTS_POSTGRES is not set", StringComparison.Ordinal),
            $"curia-operator --help without CURIA_EVENTS_POSTGRES now exits {run.Exit}: item 397 (g) is fixed. Delete the dummy variable from DPK1 and DPK5, this row, the sentence in docs/runbooks/hosted-instance.md and G83's non-property (f).");
    }

    // ---------------------------------------------------------------- DPK2

    /// <summary>
    /// DPK2. Every <c>curia &lt;verb&gt;</c> in every runbook and in the README's fences is a verb <c>curia help</c> lists, and every flag on that command line is in <c>curia &lt;verb&gt; --help</c>.
    /// The flag is read from the command line and the help from the tool. The runbooks between them run <c>enrol</c> and <c>verify</c>.
    /// </summary>
    [Fact]
    public async Task G83_DPK2_EveryClientVerbAndFlagInTheRunbooksIsOneTheShippedClientNames()
    {
        var problems = new List<string>();
        RequireRunbooksPresent(problems);
        var overview = await RunShippedAsync("curia", ["help"]).ConfigureAwait(true);
        if (overview.Exit != 0) problems.Add($"curia help exits {overview.Exit}");

        var helps = new Dictionary<string, string>(StringComparer.Ordinal);
        var named = new SortedSet<string>(StringComparer.Ordinal);
        var documents = AllRunbookPaths().Select(p => (Name: Path.GetRelativePath(Root, p), Fences: Fences(File.ReadAllText(p))))
            .Append((Name: "README.md", Fences: Fences(Text("README.md")))).ToList();
        foreach (var (name, fences) in documents)
        {
            foreach (var line in fences.SelectMany(CommandLines))
            {
                var m = ClientVerb().Match(line);
                if (!m.Success) continue;
                var verb = m.Groups[1].Value;
                if (IsNewRunbook(name)) named.Add(verb);
                if (!overview.Out.Contains($"curia {verb}", StringComparison.Ordinal))
                {
                    problems.Add($"{name} runs curia {verb}, which curia help does not list");
                    continue;
                }

                if (!helps.TryGetValue(verb, out var help))
                {
                    var run = await RunShippedAsync("curia", [verb, "--help"]).ConfigureAwait(true);
                    helps[verb] = help = run.Out;
                    if (run.Exit != 0) problems.Add($"curia {verb} --help exits {run.Exit}");
                }

                foreach (var flag in Flag().Matches(m.Groups[2].Value).Select(f => f.Value).Distinct(StringComparer.Ordinal))
                    if (!help.Contains(flag, StringComparison.Ordinal))
                        problems.Add($"{name} gives curia {verb} the flag {flag}, which curia {verb} --help does not name");
            }
        }

        foreach (var needed in new[] { "enrol", "verify" })
            if (!named.Contains(needed))
                problems.Add($"no runbook runs curia {needed}");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ---------------------------------------------------------------- DPK3

    private static string AllSource() =>
        string.Join('\n', Directory.GetFiles(Path.Combine(Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(File.ReadAllText));

    /// <summary>The variables the Forum refuses to start without: the names its start refusals tell the operator to set, and the environment constants of the origin and the model directory.</summary>
    private static SortedSet<string> RequiredStartVariables()
    {
        var found = new SortedSet<string>(StringComparer.Ordinal);
        var program = Text("src/Curia.Api/Program.cs");
        foreach (Match refusal in StartRefusal().Matches(program))
            foreach (Match v in Regex.Matches(refusal.Value, @"\bCURIA_[A-Z_]+\b", RegexOptions.None, TimeSpan.FromSeconds(5)))
                found.Add(v.Value);
        foreach (var file in new[] { "src/Curia.Api/PublicOrigin.cs", "src/Curia.Infrastructure/Embedding/EmbeddingComposition.cs" })
            foreach (Match m in EnvironmentConstant().Matches(Text(file)))
                found.Add(m.Groups[1].Value);
        return found;
    }

    /// <summary>
    /// DPK3, both directions. Every <c>CURIA_*</c> or <c>Curia__*</c> name the hosting runbook mentions is a name some file under <c>src/</c> reads (a grep over the source, not over the runbooks);
    /// every variable the Forum refuses to start without is in the hosting runbook; and the runbook leaves <c>AllowedHosts</c> unset (item 390 as G82 records it).
    /// </summary>
    [Fact]
    public void G83_DPK3_TheHostingRunbookNamesTheVariablesTheSourceReadsAndTheOnesTheForumRequires()
    {
        var problems = new List<string>();
        var runbook = Runbook("hosted-instance");
        if (runbook.Trim().Length == 0) problems.Add("docs/runbooks/hosted-instance.md is empty");

        var source = AllSource();
        foreach (var name in VariableName().Matches(runbook).Select(m => m.Value).Distinct(StringComparer.Ordinal))
        {
            var key = name.StartsWith("Curia__", StringComparison.Ordinal) ? name.Replace("__", ":", StringComparison.Ordinal) : name;
            if (!source.Contains(key, StringComparison.Ordinal)) problems.Add($"the hosting runbook names {name}, which nothing under src/ reads");
        }

        var required = RequiredStartVariables();
        foreach (var name in new[] { "CURIA_EVENTS_POSTGRES", "CURIA_ISSUER_SIGNING_KEY_PEM", "CURIA_EMBEDDING_MODEL_DIR", "CURIA_PUBLIC_ORIGIN" })
            if (!required.Contains(name)) problems.Add($"the source no longer requires {name}: this probe's own list is stale");

        foreach (var name in required)
            if (!runbook.Contains(name, StringComparison.Ordinal)) problems.Add($"the Forum refuses to start without {name} and the hosting runbook does not name it");

        if (Fences(runbook).Any(f => f.Contains("AllowedHosts", StringComparison.Ordinal)))
            problems.Add("a command block of the hosting runbook sets AllowedHosts, which the Forum's Host check (R5.27) is meant to replace");
        if (!AllowedHostsUnset().IsMatch(Flat(runbook)))
            problems.Add("the hosting runbook does not say AllowedHosts is left unset");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ---------------------------------------------------------------- DPK4

    private sealed record Recipe(List<NgxDirective> Http, List<NgxDirective> Locations, List<NgxDirective> Full)
    {
        internal static Recipe Load() => new(
            NginxConf.Parse(Text("deploy/proxy/curia-http.conf")),
            NginxConf.Parse(Text("deploy/proxy/curia-locations.conf")),
            NginxConf.Parse(Text("deploy/proxy/nginx.conf")));
    }

    /// <summary>The arguments of the enrolment route's location (G86): case-insensitive, anchored at both ends, an optional trailing slash. Task 0 measured that <c>location = /v1/agents</c> lets 9 of the 17 spellings the Forum accepts through.</summary>
    private const string EnrolmentLocationArgs = "~* ^/v1/agents/?$";

    private static IEnumerable<string> ProxySetHeader(IEnumerable<NgxDirective> locations, string header) =>
        NginxConf.Find(locations, "proxy_set_header").Where(d => d.Args.Count == 2 && string.Equals(d.Args[0], header, StringComparison.OrdinalIgnoreCase)).Select(d => d.Args[1]);

    /// <summary>The body a named location returns, with its status, its <c>default_type</c> and the <c>error_page</c> that reaches it.</summary>
    private static (int Status, string Body, string ContentType)? NamedAnswer(IEnumerable<NgxDirective> locations, string name)
    {
        var loc = NginxConf.Location(locations, name);
        var ret = loc?.Children("return").FirstOrDefault();
        if (loc is null || ret is null || ret.Args.Count < 2) return null;
        return (int.Parse(ret.Args[0], CultureInfo.InvariantCulture), ret.Args[1], loc.Children("default_type").FirstOrDefault() is { Args.Count: > 0 } dt ? dt.Args[0] : string.Empty);
    }

    private static string? ErrorPageTarget(NgxDirective location, int code) =>
        location.Children("error_page").Where(e => e.Args.Contains(code.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal))
            .Select(e => e.Args[^1]).FirstOrDefault(a => a.StartsWith('@'));

    /// <summary>
    /// DPK4. The three recipe files are read against the rule each carries. The <c>Host</c> line is <c>$http_host</c> (the header as received, which keeps a non-default port); no forwarded header is
    /// trusted for a name and the three <c>X-Forwarded-*</c> are overwritten, never appended to; both limited routes have a <c>limit_req</c> and the zone's rate and burst are at or below G83's table, which
    /// is <b>parsed from the errata</b>, and above zero; the body cap equals R6.39's submission size, parsed from the white paper; <c>429</c> and <c>413</c> answer a body the client's reader can read, with the
    /// type <c>curia/request/too-large</c> read from the Forum's source; <c>/health</c> is public. "No code reads a forwarded header" is ORG13's (amendment C5); this probe asserts that ORG13 still bans the
    /// names (a narrowing fails here and not silently), and says so: ORG13 reads <c>Curia.Api.dll</c> only.
    /// </summary>
    [Fact]
    public void G83_DPK4_TheRecipeCarriesTheRulesItExistsFor()
    {
        var problems = new List<string>();
        var recipe = Recipe.Load();
        var all = recipe.Http.Concat(recipe.Locations).Concat(recipe.Full).ToList();

        // the Host line
        var hostValues = ProxySetHeader(recipe.Locations, "Host").ToList();
        if (hostValues.Count == 0) problems.Add("curia-locations.conf holds no proxy_set_header Host");
        foreach (var v in hostValues.Where(v => !string.Equals(v, "$http_host", StringComparison.Ordinal)))
            problems.Add($"proxy_set_header Host is {v}, not $http_host (R7.33 (a): the header as received)");

        // forwarded headers: overwritten from the connection, never appended and never a name
        foreach (var d in NginxConf.Descend(all).Select(x => x.Directive).Where(d => d.Args.Any(a => a.Contains("$proxy_add_x_forwarded_for", StringComparison.Ordinal) || a.Contains("$http_x_forwarded", StringComparison.Ordinal) || a.Contains("$http_forwarded", StringComparison.Ordinal))))
            problems.Add($"{d.Text} carries a client's forwarded header onward");
        foreach (var (header, value) in new[] { ("X-Forwarded-For", "$remote_addr"), ("X-Forwarded-Proto", "$scheme"), ("X-Forwarded-Host", "$http_host") })
        {
            var set = ProxySetHeader(recipe.Locations, header).ToList();
            if (set.Count == 0) problems.Add($"curia-locations.conf does not overwrite {header}");
            foreach (var v in set.Where(v => !string.Equals(v, value, StringComparison.Ordinal)))
                problems.Add($"{header} is set from {v}, not {value}");
        }

        // RFC 7239's Forwarded is cleared, not passed (live finding 2): an empty value is not sent upstream
        var forwarded = ProxySetHeader(recipe.Locations, "Forwarded").ToList();
        if (forwarded.Count == 0) problems.Add("curia-locations.conf does not clear Forwarded (proxy_set_header Forwarded \"\"): a client's RFC 7239 header would reach the upstream");
        foreach (var v in forwarded.Where(v => v.Length != 0)) problems.Add($"Forwarded is set to '{v}', not cleared");

        // zones and locations against G83's table
        var table = PublishedEdge.Zones();
        foreach (var zone in table)
        {
            var declared = NginxConf.Find(recipe.Http, "limit_req_zone").FirstOrDefault(z => z.Args.Any(a => a.StartsWith($"zone={zone.Name}:", StringComparison.Ordinal)));
            if (declared is null) { problems.Add($"curia-http.conf declares no limit_req_zone {zone.Name}"); continue; }
            var rateArg = declared.Args.FirstOrDefault(a => a.StartsWith("rate=", StringComparison.Ordinal));
            if (rateArg is null) { problems.Add($"{zone.Name} has no rate"); continue; }
            var rate = NginxConf.RatePerSecond(rateArg["rate=".Length..]);
            if (rate <= 0 || rate > zone.RatePerSecond) problems.Add($"{zone.Name}'s rate is {rate} r/s, not above 0 and at most G83's {zone.RatePerSecond}");
            if (!declared.Args.Contains("$binary_remote_addr", StringComparer.Ordinal)) problems.Add($"{zone.Name} is not keyed on $binary_remote_addr");

            var path = zone.Route.Split('?')[0];
            // G86: the enrolment zone's location is the case-insensitive pattern, not an exact match (an exact match is the bypass, DPK4g)
            var locationArgs = zone.Name == "curia_enrol" ? EnrolmentLocationArgs : "= " + path;
            var location = NginxConf.Location(recipe.Locations, locationArgs);
            var limit = location?.Children("limit_req").FirstOrDefault(l => l.Args.Contains($"zone={zone.Name}", StringComparer.Ordinal));
            if (limit is null) { problems.Add($"location {locationArgs} has no limit_req zone={zone.Name}"); continue; }
            var burstArg = limit.Args.FirstOrDefault(a => a.StartsWith("burst=", StringComparison.Ordinal));
            var burst = burstArg is null ? 0 : int.Parse(burstArg["burst=".Length..], CultureInfo.InvariantCulture);
            if (burst <= 0 || burst > zone.Burst) problems.Add($"{zone.Name}'s burst is {burst}, not above 0 and at most G83's {zone.Burst}");
        }

        if (!NginxConf.Find(recipe.Http, "limit_req_status").Any(s => s.Args.SequenceEqual(["429"]))) problems.Add("curia-http.conf does not set limit_req_status 429");

        // body cap, from the white paper
        var cap = PublishedEdge.SubmissionCapBytes();
        var sizes = NginxConf.Find(recipe.Locations, "client_max_body_size").Select(d => NginxConf.SizeBytes(d.Args[0])).ToList();
        if (sizes.Count == 0) problems.Add("curia-locations.conf sets no client_max_body_size");
        foreach (var size in sizes.Where(s => s != cap)) problems.Add($"client_max_body_size is {size} bytes, not R6.39's {cap}");

        // readable refusals
        var tooLargeType = "curia/request/too-large";
        if (!AllSource().Contains($"\"{tooLargeType}\"", StringComparison.Ordinal)) problems.Add($"the Forum's source has no type {tooLargeType}: this probe's own constant is stale");
        void Answers(string where, int code, string contentType, string member, string value)
        {
            var loc = NginxConf.Location(recipe.Locations, where);
            var target = loc is null ? null : ErrorPageTarget(loc, code);
            if (target is null) { problems.Add($"location {where} has no error_page {code} to a named location"); return; }
            var answer = NamedAnswer(recipe.Locations, target);
            if (answer is null) { problems.Add($"the named location {target} returns nothing"); return; }
            if (answer.Value.Status != code) problems.Add($"{target} returns {answer.Value.Status}, not {code}");
            if (!string.Equals(answer.Value.ContentType, contentType, StringComparison.Ordinal)) problems.Add($"{target} answers as '{answer.Value.ContentType}', not '{contentType}'");
            if (!answer.Value.Body.Contains($"\"{member}\":\"{value}\"", StringComparison.Ordinal)) problems.Add($"{target}'s body has no \"{member}\":\"{value}\"");
        }

        Answers("= /v1/enroll/nonce", 429, "application/problem+json", "type", "curia/edge/rate-limited");
        Answers("= /v1/enroll/nonce", 413, "application/problem+json", "type", tooLargeType);
        Answers("= /oauth/token", 429, "application/json", "error", "temporarily_unavailable");
        Answers("= /oauth/token", 413, "application/json", "error", "invalid_request");
        Answers("/", 413, "application/problem+json", "type", tooLargeType);

        // /health is public: no allow, deny or limit
        var health = NginxConf.Location(recipe.Locations, "= /health");
        if (health is null) problems.Add("curia-locations.conf has no location = /health");
        else if (health.Block!.Any(d => d.Name is "allow" or "deny" or "limit_req")) problems.Add("location = /health carries an allow, deny or limit_req (P8: the monitor and readiness R1 read it from the public side)");

        // nginx.conf: the Host must reach the Forum
        var serverNames = NginxConf.Find(recipe.Full, "server_name").SelectMany(d => d.Args).ToList();
        if (serverNames.Count == 0 || serverNames.Any(n => n != "_")) problems.Add($"nginx.conf names {string.Join(", ", serverNames)}; every server_name must be _ so that a foreign Host reaches the Forum's own refusal");
        if (NginxConf.Find(recipe.Full, "return").Any(r => r.Args.Count > 0 && r.Args[0] == "444")) problems.Add("nginx.conf answers 444: the edge would make the Forum's Host refusal its own");
        var redirect = NginxConf.Find(recipe.Full, "return").FirstOrDefault(r => r.Args.Count == 2 && r.Args[0] == "301");
        if (redirect is null) problems.Add("nginx.conf has no 301 redirect from port 80");
        else if (redirect.Args[1].Contains("$host", StringComparison.Ordinal) || redirect.Args[1].Contains("$http_host", StringComparison.Ordinal)) problems.Add("the port-80 redirect is built from the request's host, not the literal name");
        foreach (var inc in new[] { "curia-http.conf", "curia-locations.conf" })
            if (!NginxConf.Find(recipe.Full, "include").Any(i => i.Args.SequenceEqual([inc]))) problems.Add($"nginx.conf does not include {inc}");

        // ORG13 still bans the names (amendment C5; its scope is Curia.Api.dll only)
        var org13 = Text("tests/Curia.Architecture.Tests/OriginFenceTests.cs");
        var banned = BannedLiterals().Match(org13);
        foreach (var name in new[] { "Host", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Forwarded-For", "Forwarded" })
            if (!banned.Success || !banned.Groups[1].Value.Contains($"\"{name}\"", StringComparison.Ordinal))
                problems.Add($"OriginFenceTests' BannedLiterals no longer bans \"{name}\", so 'no Forum code reads a forwarded header' is undischarged");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// G86_DPK4e (errata G86, R7.33 (rev.) (d)): the enrolment route is closed by default and opened by a file of addresses. (i) a location with the arguments <c>~* ^/v1/agents/?$</c> exists and no
    /// location is <c>= /v1/agents</c> (the exact match is the bypass, measured: 9 of 17 spellings); (ii) it includes <c>curia-enrolment-allow.conf</c> before a <c>deny all</c> (an <c>allow</c> after
    /// <c>deny all</c> is dead) and holds no <c>allow all</c>; (iii) its <c>error_page 403</c> reaches a named location answering <c>403</c>, <c>application/problem+json</c>, with the type the errata's R7.33 (rev.)
    /// names (read from the errata and compared with the recipe, not typed here); (iv) the shipped allow file holds no <c>allow</c> line (a shipped address is an opening); (v) its 413 and 429 pages are kept (an
    /// <c>error_page</c> inside a location replaces the server's); (vi) <c>nginx.conf</c>'s header lists the file among the files copied together.
    /// </summary>
    [Fact]
    public void G86_DPK4e_TheEnrolmentRouteIsClosedByDefaultAndOpenedByAFileOfAddresses()
    {
        var problems = new List<string>();
        var recipe = Recipe.Load();
        var type = PublishedEdge.EnrolmentClosedType();

        if (NginxConf.Location(recipe.Locations, "= /v1/agents") is not null) problems.Add("curia-locations.conf has location = /v1/agents: an exact match is the bypass (Task 0: 9 of the 17 spellings the Forum accepts reach it)");
        var location = NginxConf.Location(recipe.Locations, EnrolmentLocationArgs);
        if (location is null) { problems.Add($"curia-locations.conf has no location {EnrolmentLocationArgs}"); }
        else
        {
            var block = location.Block!.ToList();
            var include = block.FindIndex(d => d.Name == "include" && d.Args.SequenceEqual(["curia-enrolment-allow.conf"]));
            var deny = block.FindIndex(d => d.Name == "deny" && d.Args.SequenceEqual(["all"]));
            if (include < 0) problems.Add("the enrolment location does not include curia-enrolment-allow.conf");
            if (deny < 0) problems.Add("the enrolment location has no deny all: the route is open by default");
            if (include >= 0 && deny >= 0 && include > deny) problems.Add("the enrolment location includes the allow file after deny all: an allow after deny all is dead");
            if (block.Any(d => d.Name == "allow")) problems.Add("the enrolment location holds an allow directive of its own; the operator's addresses are in the include only");

            var target = ErrorPageTarget(location, 403);
            var answer = target is null ? null : NamedAnswer(recipe.Locations, target);
            if (target is null) problems.Add("the enrolment location has no error_page 403 to a named location: a refusal would be nginx's HTML page, which curia cannot read");
            else if (answer is null) problems.Add($"the named location {target} returns nothing");
            else
            {
                if (answer.Value.Status != 403) problems.Add($"{target} returns {answer.Value.Status}, not 403");
                if (!string.Equals(answer.Value.ContentType, "application/problem+json", StringComparison.Ordinal)) problems.Add($"{target} answers as '{answer.Value.ContentType}', not application/problem+json");
                if (!answer.Value.Body.Contains($"\"type\":\"{type}\"", StringComparison.Ordinal)) problems.Add($"{target}'s body does not carry \"type\":\"{type}\", the type R7.33 (rev.) names");
            }

            foreach (var (code, page) in new[] { (413, "@curia_too_large"), (429, "@curia_rate_limited") })
                if (ErrorPageTarget(location, code) != page) problems.Add($"the enrolment location's error_page {code} is not {page}: a location's error_page replaces the server's");
        }

        if (!File.Exists(Path.Combine(Root, "deploy", "proxy", "curia-enrolment-allow.conf"))) problems.Add("deploy/proxy/curia-enrolment-allow.conf is not shipped: the include has nothing to read");
        else
            foreach (var d in NginxConf.Parse(Text("deploy/proxy/curia-enrolment-allow.conf")).Where(d => d.Name is "allow" or "deny"))
                problems.Add($"the shipped curia-enrolment-allow.conf holds '{d.Text}': a shipped address is an opening");

        var header = string.Join('\n', Text("deploy/proxy/nginx.conf").Split('\n').TakeWhile(l => l.StartsWith('#')));
        if (!header.Contains("curia-enrolment-allow.conf", StringComparison.Ordinal)) problems.Add("nginx.conf's header does not list curia-enrolment-allow.conf among the files copied together");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ---------------------------------------------------------------- DPK5

    private static Dictionary<string, List<string>> Unit(string relative)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var raw in Text(relative).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('[')) continue;
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0) continue;
            var key = line[..eq];
            if (!result.TryGetValue(key, out var values)) result[key] = values = [];
            values.Add(line[(eq + 1)..]);
        }

        return result;
    }

    /// <summary>
    /// DPK5. The head timer's units: <c>ExecStart</c> is <c>curia-operator sign-head --by &lt;role&gt; --if-grown</c> (the verb and the flag in the shipped tool's help); the <c>EnvironmentFile</c> it names has
    /// an example in the repository that holds <c>CURIA_LOG_SIGNING_KEY_PEM</c> and neither the declaration key nor the operator-acts key (R12.18, read from the help); no <c>Environment=</c> names either;
    /// <c>SuccessExitStatus</c> does not include 6 (the stall is a failure); <c>OnFailure</c> names a unit in <c>deploy/systemd</c> that says it alerts no one; the timer has a cadence and is persistent; and the
    /// existing sign-head tests the runbook cites by name still exist.
    /// </summary>
    [Fact]
    public async Task G83_DPK5_TheHeadTimerUnitsCarryR12_18AndTheStall()
    {
        var problems = new List<string>();
        var help = await OperatorHelpAsync().ConfigureAwait(true);
        if (help.Exit != 0) problems.Add($"curia-operator --help exits {help.Exit}");
        var service = Unit("deploy/systemd/curia-sign-head.service");
        var timer = Unit("deploy/systemd/curia-sign-head.timer");

        var exec = service.GetValueOrDefault("ExecStart")?.FirstOrDefault();
        var m = exec is null ? null : ExecStart().Match(exec);
        if (m is null || !m.Success) problems.Add($"ExecStart is '{exec}', not 'curia-operator sign-head --by <role> --if-grown'");
        else
        {
            if (!help.Out.Contains("curia-operator sign-head", StringComparison.Ordinal)) problems.Add("the shipped help does not name sign-head");
            if (!help.Out.Contains("--if-grown", StringComparison.Ordinal)) problems.Add("the shipped help does not name --if-grown");
        }

        var envFile = service.GetValueOrDefault("EnvironmentFile")?.FirstOrDefault()?.TrimStart('-');
        if (envFile is null) problems.Add("the service names no EnvironmentFile");
        else
        {
            var example = Text("deploy/systemd/" + Path.GetFileName(envFile) + ".example");
            if (!example.Contains("CURIA_LOG_SIGNING_KEY_PEM", StringComparison.Ordinal)) problems.Add("the environment file's example does not hold CURIA_LOG_SIGNING_KEY_PEM");
            foreach (var other in new[] { "CURIA_DECLARATION_SIGNING_KEY_PEM", "CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM" })
                if (example.Contains(other, StringComparison.Ordinal)) problems.Add($"the environment file's example holds {other} (R12.18)");
        }

        foreach (var value in service.GetValueOrDefault("Environment") ?? [])
            foreach (var other in new[] { "CURIA_DECLARATION_SIGNING_KEY_PEM", "CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM" })
                if (value.Contains(other, StringComparison.Ordinal)) problems.Add($"Environment= names {other} (R12.18)");
        if (!help.Out.Contains("R12.18", StringComparison.Ordinal)) problems.Add("the shipped help no longer states R12.18, which this probe reads the environment rule from");

        foreach (var status in service.GetValueOrDefault("SuccessExitStatus") ?? [])
            if (status.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("6")) problems.Add("SuccessExitStatus includes 6: the stall (a forged row follows the latest head) would read as success");

        var onFailure = service.GetValueOrDefault("OnFailure")?.FirstOrDefault();
        if (onFailure is null) problems.Add("the service has no OnFailure=");
        else if (File.Exists(Path.Combine(Root, "deploy", "systemd", onFailure)))
        {
            if (!Flat(Text("deploy/systemd/" + onFailure)).Contains("alerts no one", StringComparison.Ordinal)) problems.Add($"{onFailure} does not state that it alerts no one");
        }
        else problems.Add($"OnFailure names {onFailure}, which is not in deploy/systemd (the owner's unit is named in the runbook, not here)");

        if (!timer.ContainsKey("OnCalendar") && !timer.ContainsKey("OnUnitActiveSec")) problems.Add("the timer has no cadence");
        if (!(timer.GetValueOrDefault("Persistent")?.Contains("true") ?? false)) problems.Add("the timer is not Persistent=true");

        var runbook = Runbook("head-timer");
        foreach (var test in new[] { "R6_63_HSU8_IfGrownSignsNothingOnALogThatHasNotGrown", "LRV15_AfterAPlainSignHeadCoversAForgedRowTheNextIfGrownRuns_Control" })
        {
            if (!runbook.Contains(test, StringComparison.Ordinal)) problems.Add($"head-timer.md does not cite {test}");
            if (!AllTestSource().Contains($"Task {test}(", StringComparison.Ordinal)) problems.Add($"the test {test} the runbook cites no longer exists");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ---------------------------------------------------------------- DPK10

    /// <summary>The <c>NAME=</c> assignments of an environment-file example (comments and blank lines dropped).</summary>
    private static List<string> AssignedNames(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('=', 2)[0]).ToList();

    /// <summary>
    /// DPK10 (cold read, defect 3). The Forum's unit: a service user that is not root; an <c>EnvironmentFile</c> whose example holds exactly the variables the Forum refuses to start without (read from the source,
    /// as DPK3 reads them) and none of the operator's three signing keys; <c>ASPNETCORE_ENVIRONMENT=Production</c>; the loopback address the edge's upstream names; <c>ExecStart</c> the published <c>Curia.Api</c>; a
    /// restart limit; no start timeout; <c>LimitCORE=0</c>; and the hosting runbook says where the file lives and its mode.
    /// </summary>
    [Fact]
    public void G83_DPK10_TheForumsUnitCarriesWhatTheRunbookSaysItDoes()
    {
        var problems = new List<string>();
        var unitText = Text("deploy/systemd/curia-forum.service");
        var service = Unit("deploy/systemd/curia-forum.service");
        string One(string key) => service.GetValueOrDefault(key)?.FirstOrDefault() ?? string.Empty;
        string[] operatorKeys = ["CURIA_LOG_SIGNING_KEY_PEM", "CURIA_DECLARATION_SIGNING_KEY_PEM", "CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM"];

        var user = One("User");
        if (user.Length == 0 || user == "root") problems.Add($"User is '{user}': the Forum runs as its own unprivileged user");

        var envFile = One("EnvironmentFile");
        if (envFile != "/etc/curia/forum.env") problems.Add($"EnvironmentFile is '{envFile}', not /etc/curia/forum.env (the path the hosting runbook states)");
        else
        {
            var example = Text("deploy/systemd/" + Path.GetFileName(envFile) + ".example");
            var names = AssignedNames(example);
            var required = RequiredStartVariables();
            foreach (var name in required.Where(r => !names.Contains(r, StringComparer.Ordinal))) problems.Add($"the environment example lacks {name}, which the Forum refuses to start without");
            foreach (var name in names.Where(n => !required.Contains(n))) problems.Add($"the environment example holds {name}, which is not a variable the Forum requires to start");
            foreach (var key in operatorKeys.Where(k => example.Contains(k, StringComparison.Ordinal))) problems.Add($"the environment example names {key} (R12.18, and a core of the Forum holds its environment)");
            if (example.Contains("Development", StringComparison.OrdinalIgnoreCase) || names.Contains("ASPNETCORE_ENVIRONMENT", StringComparer.Ordinal) || names.Contains("DOTNET_ENVIRONMENT", StringComparer.Ordinal)) problems.Add("the environment example sets or names the hosting environment: the unit's Production is the one place it is set, and a Development Forum serves a page that carries the request's credentials (G84, item 399 (c); R11.112)");
        }

        foreach (var key in operatorKeys.Where(k => unitText.Contains(k, StringComparison.Ordinal))) problems.Add($"the Forum's unit names {key}");
        if (unitText.Contains("AllowedHosts", StringComparison.Ordinal) && unitText.Split('\n').Any(l => !l.TrimStart().StartsWith('#') && l.Contains("AllowedHosts", StringComparison.Ordinal))) problems.Add("the unit sets AllowedHosts");

        var environment = service.GetValueOrDefault("Environment") ?? [];
        if (!environment.Contains("ASPNETCORE_ENVIRONMENT=Production", StringComparer.Ordinal)) problems.Add("Environment= does not set ASPNETCORE_ENVIRONMENT=Production");
        // R11.112 (errata G85): DOTNET_ENVIRONMENT beats ASPNETCORE_ENVIRONMENT when both are set (measured), so the unit sets both; the guard is the control and the unit is a belt.
        if (!environment.Contains("DOTNET_ENVIRONMENT=Production", StringComparer.Ordinal)) problems.Add("Environment= does not set DOTNET_ENVIRONMENT=Production (the host takes DOTNET_ENVIRONMENT before ASPNETCORE_ENVIRONMENT, so a Development value of it beats the unit's other line: R11.112)");
        foreach (var variable in new[] { "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" })
            foreach (var line in environment.Where(e => e.StartsWith(variable + "=", StringComparison.Ordinal) && e != variable + "=Production"))
                problems.Add($"Environment= sets {variable} to something other than Production: {line}");
        if (environment.Any(e => e.Contains("Development", StringComparison.OrdinalIgnoreCase))) problems.Add("Environment= names Development");
        foreach (var variable in environment.Select(e => e.Split('=', 2)[0]).Where(n => n.StartsWith("CURIA_", StringComparison.Ordinal)))
            if (!AllSource().Contains(variable, StringComparison.Ordinal)) problems.Add($"Environment= sets {variable}, which nothing under src/ reads");

        var urls = environment.FirstOrDefault(e => e.StartsWith("ASPNETCORE_URLS=", StringComparison.Ordinal))?["ASPNETCORE_URLS=".Length..] ?? string.Empty;
        var upstream = NginxConf.Find(NginxConf.Parse(Text("deploy/proxy/nginx.conf")), "server").FirstOrDefault(s => s.Args.Count == 1 && s.Args[0].StartsWith("127.0.0.1:", StringComparison.Ordinal))?.Args[0];
        if (upstream is null) problems.Add("nginx.conf names no loopback upstream");
        else if (urls != $"http://{upstream}") problems.Add($"ASPNETCORE_URLS is '{urls}', not http://{upstream} (the upstream nginx.conf names, loopback only)");

        var exec = One("ExecStart");
        var project = "src/Curia.Api/Curia.Api.csproj";
        if (!File.Exists(Path.Combine(Root, project)) || Text(project).Contains("<AssemblyName>", StringComparison.Ordinal)) problems.Add("Curia.Api's executable is no longer named after its project: ExecStart is read against that");
        if (exec != "/opt/curia/api/Curia.Api") problems.Add($"ExecStart is '{exec}', not the published /opt/curia/api/Curia.Api");
        if (One("WorkingDirectory") != "/opt/curia/api") problems.Add("WorkingDirectory is not the publish directory");

        if (One("Restart") != "on-failure") problems.Add("Restart is not on-failure");
        foreach (var key in new[] { "StartLimitIntervalSec", "StartLimitBurst" })
            if (!int.TryParse(One(key), CultureInfo.InvariantCulture, out var value) || value <= 0) problems.Add($"{key} is '{One(key)}', not a positive integer (a bad origin would restart-loop for ever)");
        if (One("TimeoutStartSec") is not ("infinity" or "0")) problems.Add($"TimeoutStartSec is '{One("TimeoutStartSec")}': a reindex of a long log is a start longer than any fixed timeout (README, 'Running it', 4)");
        if (One("LimitCORE") != "0") problems.Add("LimitCORE is not 0: a core of this process holds CURIA_ISSUER_SIGNING_KEY_PEM");

        var hosting = Flat(Runbook("hosted-instance"));
        foreach (var needed in new[] { "deploy/systemd/curia-forum.service", "/etc/curia/forum.env", "mode 0600", "ASPNETCORE_ENVIRONMENT=Production", "TimeoutStartSec=infinity", "LimitCORE=0" })
            if (!hosting.Contains(needed, StringComparison.Ordinal)) problems.Add($"the hosting runbook does not say '{needed}'");
        if (hosting.Contains("this pack ships none", StringComparison.Ordinal)) problems.Add("the hosting runbook still says the pack ships no Forum unit");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// DEV11 (errata G85, R11.112; amendment 1 ruling 4(b)). No file under <c>deploy/</c> or <c>docs/runbooks/</c>, and no line of the README's hosting section, sets <c>ASPNETCORE_ENVIRONMENT</c> or
    /// <c>DOTNET_ENVIRONMENT</c> to <c>Development</c> in any case (the forms <c>NAME=value</c>, <c>NAME: value</c>, <c>export NAME=value</c>, <c>-e NAME=value</c>), so no operator command the repository gives starts a
    /// hosted Forum in Development. The runbook quotes the refusal words, which name both variables and Production and never Development after one.
    /// </summary>
    [Fact]
    public void R11_112_DEV11_NoDeployFileOrRunbookSetsAHostingEnvironmentToDevelopment()
    {
        var problems = new List<string>();
        var files = new List<string>();
        foreach (var dir in new[] { "deploy", "docs/runbooks" })
            files.AddRange(Directory.EnumerateFiles(Path.Combine(Root, dir), "*", SearchOption.AllDirectories));
        files.Add(Path.Combine(Root, "README.md"));
        var assignment = EnvironmentAssignment();
        foreach (var file in files)
            foreach (var (line, number) in File.ReadAllLines(file).Select((l, i) => (l, i + 1)))
                if (assignment.Match(line) is { Success: true } m) problems.Add($"{Path.GetRelativePath(Root, file)}:{number} sets {m.Groups[1].Value} to Development: {line.Trim()}");

        // the pattern itself is held: it finds each form it is written for
        foreach (var form in new[] { "ASPNETCORE_ENVIRONMENT=Development", "export DOTNET_ENVIRONMENT=development", "docker run -e ASPNETCORE_ENVIRONMENT=DEVELOPMENT x", "DOTNET_ENVIRONMENT: Development", "Environment=ASPNETCORE_ENVIRONMENT=Development" })
            if (!assignment.IsMatch(form)) problems.Add($"the DEV11 pattern does not find '{form}'");
        foreach (var form in new[] { "ASPNETCORE_ENVIRONMENT=Production", "unset DOTNET_ENVIRONMENT", "set both to Production" })
            if (assignment.IsMatch(form)) problems.Add($"the DEV11 pattern finds '{form}', which is not an assignment of Development");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [GeneratedRegex(@"\b(ASPNETCORE_ENVIRONMENT|DOTNET_ENVIRONMENT)\s*[=:]\s*['""]?development\b", RegexOptions.IgnoreCase)]
    private static partial Regex EnvironmentAssignment();

    // ---------------------------------------------------------------- DPK11

    private static readonly string[] DeployToolRunbooks = ["hosted-instance", "backup-restore"];

    [GeneratedRegex(@"dotnet publish (src/\S+) -c Release -o (\S+)")]
    private static partial Regex PublishCommand();

    [GeneratedRegex(@"\d{4}-\d{2}-\d{2}|\$\(date")]
    private static partial Regex DatedName();

    /// <summary>
    /// DPK11 (cold read, defects 1, 2, 4 and 5). The provisioning section sits before Start order and its commands are read against the repository: the three <c>dotnet publish</c> lines name projects that exist, in
    /// Release, to the directories the units run from (with the link the head timer's unit runs); the SDK install names the version <c>global.json</c> pins; the first three commands are the clone, the checkout and
    /// the SDK, in that order; Postgres 18 and pgvector are what CI runs. The roles are assigned once (the database's owner applies the migrations, the operator the keys, the owner the announcement), every Start-order
    /// step names who runs it, the declaration and operator-acts keys are published before the first head and the log key by it, and the three runbooks agree. How a precondition is confirmed (D125's wait and the tests that flip,
    /// the owner's written note, the offline key, <c>--probe-limits</c>'s bound) is read against the source; no dump file name carries a date; and the 429 sentence states the measured behaviour.
    /// </summary>
    [Fact]
    public void G83_DPK11_ProvisioningRolesAndConfirmationAreStatedAndReadAgainstTheRepository()
    {
        var problems = new List<string>();
        var raw = Runbook("hosted-instance");
        var hosting = Flat(raw);
        int At(string heading) => raw.IndexOf(heading, StringComparison.Ordinal);

        // provisioning sits before Start order, after the refused start
        if (At("## Provisioning") < 0 || At("## Start order") < 0 || At("## Provisioning") > At("## Start order")) problems.Add("the section 'Provisioning' is missing or after 'Start order'");
        if (At("## Who does what") < 0 || At("## Who does what") > At("## Preconditions")) problems.Add("'Who does what' is missing or after 'Preconditions'");

        var provisioning = At("## Provisioning") >= 0 && At("## Start order") > At("## Provisioning") ? raw[At("## Provisioning")..At("## Start order")] : string.Empty;
        var fences = Fences(provisioning);
        var commands = fences.SelectMany(CommandLines).ToList();
        if (commands.Count < 3 || !commands[0].StartsWith("git clone ", StringComparison.Ordinal) || !commands[1].Contains("checkout", StringComparison.Ordinal) || !commands[2].Contains("dotnet-install.sh", StringComparison.Ordinal))
            problems.Add("the first three commands are not the clone, the checkout and the SDK install");

        var sdk = Regex.Match(Text("global.json"), "\"version\":\\s*\"([^\"]+)\"", RegexOptions.None, TimeSpan.FromSeconds(5)).Groups[1].Value;
        if (sdk.Length == 0 || !commands.Any(c => c.Contains("dotnet-install.sh", StringComparison.Ordinal) && c.Contains($"--version {sdk}", StringComparison.Ordinal)))
            problems.Add($"no install command names the SDK global.json pins ({sdk})");
        foreach (var needed in new[] { "PostgreSQL 18", "pgvector", "python3", "named commit or tag" })
            if (!Flat(provisioning).Contains(needed, StringComparison.Ordinal)) problems.Add($"provisioning does not say '{needed}'");
        if (!Text(".github/workflows/ci.yml").Contains("pgvector/pgvector:pg18", StringComparison.Ordinal)) problems.Add("CI no longer runs pgvector/pgvector:pg18: 'PostgreSQL 18' is read against it");

        // R11.112 (errata G85, amendment 1 ruling 5): a Release publish on an operator's box ships PDBs whose frames name the builder's directory, and a refused start prints frames. In a git clone, as the runbook's
        // first commands make one, ContinuousIntegrationBuild maps them to /_/ (measured by the stage's Task 6; without the clone it maps nothing). This row restates the doc; the measurement is the evidence.
        var publishLines = commands.Where(c => c.StartsWith("dotnet publish ", StringComparison.Ordinal)).ToList();
        if (publishLines.Count != 3) problems.Add($"provisioning has {publishLines.Count} dotnet publish lines, expected three");
        foreach (var line in publishLines.Where(l => !l.Contains("-p:ContinuousIntegrationBuild=true", StringComparison.Ordinal))) problems.Add($"a publish line does not carry -p:ContinuousIntegrationBuild=true, so a refused start's frames name the builder's directory: {line}");

        var published = PublishCommand().Matches(provisioning).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);
        foreach (var (project, executable) in new[] { ("src/Curia.Api", "Curia.Api"), ("src/Curia.Operator", "curia-operator"), ("src/Curia.Client.Cli", "curia") })
        {
            var csproj = Path.Combine(Root, project, Path.GetFileName(project) + ".csproj");
            if (!File.Exists(csproj)) { problems.Add($"{project} has no project file"); continue; }
            if (!published.ContainsKey(project)) { problems.Add($"provisioning does not publish {project} (dotnet publish {project} -c Release -o <dir>)"); continue; }
            var assembly = Regex.Match(File.ReadAllText(csproj), "<AssemblyName>([^<]+)</AssemblyName>", RegexOptions.None, TimeSpan.FromSeconds(5));
            var name = assembly.Success ? assembly.Groups[1].Value : Path.GetFileName(project);
            if (name != executable) problems.Add($"{project} publishes an executable named {name}, not {executable}");
        }

        if (published.GetValueOrDefault("src/Curia.Api") + "/Curia.Api" != Unit("deploy/systemd/curia-forum.service").GetValueOrDefault("ExecStart")?.FirstOrDefault()) problems.Add("the Forum's publish directory is not the one the unit's ExecStart runs");
        var headExec = Unit("deploy/systemd/curia-sign-head.service").GetValueOrDefault("ExecStart")?.FirstOrDefault() ?? string.Empty;
        var headPath = headExec.Split(' ')[0];
        var link = commands.FirstOrDefault(c => c.StartsWith("ln -s ", StringComparison.Ordinal) && c.EndsWith(headPath, StringComparison.Ordinal));
        if (link is null || !link.Contains($"{published.GetValueOrDefault("src/Curia.Operator")}/curia-operator ", StringComparison.Ordinal))
            problems.Add($"no link puts the published curia-operator at {headPath}, the path the head timer's unit runs");
        if (!commands.Any(c => c.StartsWith("ln -s ", StringComparison.Ordinal) && c.Contains($"{published.GetValueOrDefault("src/Curia.Client.Cli")}/curia /usr/local/bin/curia", StringComparison.Ordinal))) problems.Add("no link puts the published curia on the path");
        foreach (var user in new[] { Unit("deploy/systemd/curia-forum.service"), Unit("deploy/systemd/curia-sign-head.service") }.Select(u => u["User"][0]))
            if (!commands.Any(c => c.StartsWith("useradd ", StringComparison.Ordinal) && c.EndsWith($" {user}", StringComparison.Ordinal))) problems.Add($"provisioning creates no user {user}, which a unit runs as");

        // roles: assigned once
        var who = At("## Who does what") >= 0 && At("## Preconditions") > At("## Who does what") ? raw[At("## Who does what")..At("## Preconditions")] : string.Empty;
        foreach (var needed in new[] { "applies db/0001 to db/0006", "generates, backs up and publishes the three keys", "announces the instance and the three `kid`s", "application role never applies a migration" })
            if (!Flat(who).Contains(needed, StringComparison.Ordinal)) problems.Add($"'Who does what' does not say '{needed}'");
        var pre = At("## Preconditions") >= 0 && At("## The environment") > At("## Preconditions") ? Flat(raw[At("## Preconditions")..At("## The environment")]) : string.Empty;
        if (!pre.Contains("CONFIRMS", StringComparison.Ordinal) || !pre.Contains("written note", StringComparison.Ordinal) || !pre.Contains("keeps the notes with the announcement", StringComparison.Ordinal)) problems.Add("Preconditions does not say they are what the owner CONFIRMS, in a written note the operator keeps with the announcement");
        foreach (var contradiction in new[] { "by its owner", "are generated, backed up, published once" })
            if (pre.Contains(contradiction, StringComparison.Ordinal)) problems.Add($"Preconditions still says '{contradiction}', the owner's doing what Start order gives another role");

        var start = At("## Start order") >= 0 && At("## Readiness") > At("## Start order") ? raw[At("## Start order")..At("## Readiness")] : string.Empty;
        var steps = Regex.Matches(start, @"^(\d+)\. (.*)$", RegexOptions.Multiline, TimeSpan.FromSeconds(5)).Select(m => (N: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), Line: m.Groups[2].Value)).ToList();
        if (steps.Count < 9) problems.Add($"Start order has {steps.Count} steps");
        foreach (var (n, line) in steps)
            if (!Regex.IsMatch(line, @"\((the database's owner|the operator|the owner)\b", RegexOptions.None, TimeSpan.FromSeconds(5))) problems.Add($"Start order step {n} names no role that runs it");
        var declaration = steps.FindIndex(s => s.Line.Contains("declaration key and the operator-acts key", StringComparison.Ordinal));
        var firstHead = steps.FindIndex(s => s.Line.Contains("first head", StringComparison.Ordinal));
        if (declaration < 0 || firstHead < 0 || declaration > firstHead) problems.Add("the declaration and operator-acts keys are not published before the first head");
        if (declaration >= 0 && steps[declaration].Line.Contains("log key", StringComparison.Ordinal) && !steps[declaration].Line.Contains("not published here", StringComparison.Ordinal)) problems.Add("the key step publishes the log key before the first sign-head");
        foreach (var name in new[] { "operator-keys", "head-timer" })
        {
            var text = Flat(Runbook(name));
            if (!text.Contains("first `sign-head`", StringComparison.Ordinal) || !text.Contains("log key", StringComparison.Ordinal)) problems.Add($"{name}.md does not say the log key is published by the first sign-head");
            if (!Regex.IsMatch(text, @"back[^.]{0,40} (up )?(each key )?(up )?first|backed up before|back each up|Back each key up first", RegexOptions.None, TimeSpan.FromSeconds(5))) problems.Add($"{name}.md does not say to back a key up before its publication");
        }

        // how a precondition is confirmed
        // D125's confirmation (what the operator reads and the tests it names) is BND11's, moved there from here (errata G84, amendment 1 ruling 8).
        if (!hosting.Contains("a channel is the owner's to choose", StringComparison.Ordinal)) problems.Add("the hosting runbook does not say 'a channel is the owner's to choose'");
        var keys = Flat(Runbook("operator-keys"));
        if (!keys.Contains("carried", StringComparison.Ordinal) || !keys.Contains("R12.18", StringComparison.Ordinal) || !keys.Contains("holds none of the other two keys", StringComparison.Ordinal)) problems.Add("operator-keys.md does not say how the offline declaration key meets the publish command");
        var readiness = Text("tools/deploy/readiness.py");
        var burst = Regex.Match(readiness, @"--burst"", type=int, default=(\d+)", RegexOptions.None, TimeSpan.FromSeconds(5));
        var extra = Regex.Match(readiness, @"count = args\.burst \+ (\d+)", RegexOptions.None, TimeSpan.FromSeconds(5));
        if (!burst.Success || !extra.Success) problems.Add("readiness.py's flood count is no longer burst + n: the runbook's bound is read against it");
        else if (!hosting.Contains($"{int.Parse(burst.Groups[1].Value, CultureInfo.InvariantCulture) + int.Parse(extra.Groups[1].Value, CultureInfo.InvariantCulture)} requests per route", StringComparison.Ordinal)) problems.Add("the runbook states no count of requests per route that matches readiness.py's flood");
        if (!hosting.Contains("Run it once, before announcing", StringComparison.Ordinal)) problems.Add("the runbook does not say whether to run --probe-limits before announcing");
        foreach (var (name, fencesOf) in DeployToolRunbooks.Select(n => (n, Fences(Runbook(n)))))
            foreach (var line in fencesOf.SelectMany(CommandLines).Where(l => l.Contains("readiness.py", StringComparison.Ordinal) || l.Contains("backup-drill.py", StringComparison.Ordinal)))
                foreach (var flag in Flag().Matches(line).Select(f => f.Value))
                    if (!Text("tools/deploy/" + (line.Contains("readiness.py", StringComparison.Ordinal) ? "readiness.py" : "backup-drill.py")).Contains($"\"{flag}\"", StringComparison.Ordinal)) problems.Add($"{name} gives a deploy tool the flag {flag}, which the tool does not define");
        if (Fences(Runbook("backup-restore")).Any(f => DatedName().IsMatch(f))) problems.Add("backup-restore.md's commands carry a dated file name or $(date): a placeholder is what an example should carry");

        var proxy = Flat(Runbook("reverse-proxy"));
        if (proxy.Contains("is not measured by this document", StringComparison.Ordinal)) problems.Add("reverse-proxy.md still says curia's behaviour on a 429 is not measured");
        foreach (var needed in new[] { "does not wait and does not retry on a `429`", "exits **8**", "must read the printed type" })
            if (!proxy.Contains(needed, StringComparison.Ordinal)) problems.Add($"reverse-proxy.md does not state the measured 429 behaviour: '{needed}'");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    private static string AllTestSource() =>
        string.Join('\n', Directory.GetFiles(Path.Combine(Root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)).Select(File.ReadAllText));

    // ---------------------------------------------------------------- DPK7

    /// <summary>
    /// What a deployment document cites that does not resolve, by the scan DPK7 and its red twin share. <c>R&lt;n&gt;.&lt;n&gt;</c> must have a defining <c>**R&lt;n&gt;.&lt;n&gt;**</c> in the white paper or the errata
    /// or a row of the errata's index; <c>G&lt;n&gt;</c> a <c>## G&lt;n&gt; </c> heading; <c>item &lt;n&gt;</c> a register line <c>(&lt;n&gt;)</c> on this branch (a held item is cited through the errata
    /// entry that names it: amendment C10); <c>D&lt;n&gt;</c> a register line or a mention in the errata; a backticked repository path must exist.
    /// </summary>
    internal static List<string> UnresolvedCitations(string document)
    {
        var problems = new List<string>();
        var paper = Text("curia-agent-forum-WHITEPAPER.md");
        var errata = Text("curia-whitepaper-ERRATA-AND-ADDENDUM.md");
        var register = Text("IMPLEMENTATION_PLAN.md");
        foreach (var r in Requirement().Matches(document).Select(m => m.Value).Distinct(StringComparer.Ordinal))
        {
            var defined = paper.Contains($"**{r}**", StringComparison.Ordinal) || errata.Contains($"**{r}**", StringComparison.Ordinal)
                || errata.Contains($"| {r} |", StringComparison.Ordinal) || errata.Contains($"| {r} (", StringComparison.Ordinal) || paper.Contains($"| {r} |", StringComparison.Ordinal);
            if (!defined) problems.Add($"{r} is defined nowhere");
        }

        foreach (var g in Errata().Matches(document).Select(m => m.Value).Distinct(StringComparer.Ordinal))
            if (!errata.Contains($"\n## {g} ", StringComparison.Ordinal)) problems.Add($"{g} has no heading in the errata");
        foreach (var n in Item().Matches(document).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal))
            if (!register.Contains($"\n({n}) ", StringComparison.Ordinal)) problems.Add($"item {n} is not in the register on this branch");
        foreach (var d in Defect().Matches(document).Select(m => m.Value).Distinct(StringComparer.Ordinal))
            if (!register.Contains($"\n({d}) ", StringComparison.Ordinal) && !Regex.IsMatch(errata, $@"\b{d}\b", RegexOptions.None, TimeSpan.FromSeconds(5)))
                problems.Add($"{d} is neither in the register nor in the errata");
        foreach (var path in RepoPath().Matches(document).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal))
        {
            if (path.Contains('<', StringComparison.Ordinal) || path.Contains('*', StringComparison.Ordinal) || path.Contains('{', StringComparison.Ordinal)) continue;
            if (!File.Exists(Path.Combine(Root, path)) && !Directory.Exists(Path.Combine(Root, path.TrimEnd('/')))) problems.Add($"the path {path} does not exist");
        }

        return problems;
    }

    /// <summary>
    /// DPK7. The five runbooks and the README's hosting section cite only what exists. Citations are scanned by <see cref="UnresolvedCitations"/>; <c>check-spec.py</c> does not reach
    /// <c>docs/runbooks/</c> (measured: Task 0 finding 6), so this scan is the only one.
    /// </summary>
    [Fact]
    public void G83_DPK7_EveryCitationInTheDeploymentDocumentsResolves()
    {
        var problems = new List<string>();
        RequireRunbooksPresent(problems);
        foreach (var name in NewRunbooks)
            problems.AddRange(UnresolvedCitations(Runbook(name)).Select(p => $"{name}.md: {p}"));
        var section = ReadmeHostingSection();
        if (section.Trim().Length == 0) problems.Add("the README has no '## Hosting a beta instance' section");
        problems.AddRange(UnresolvedCitations(section).Select(p => $"README hosting section: {p}"));
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>FDPK7, the scan's red twin (amendment C10): a document citing <c>R999.99</c>, <c>G9999</c>, <c>item 99999</c>, <c>D99999</c> and a missing path is flagged for each, so that DPK7's green is not a scan that sees nothing.</summary>
    [Fact]
    public void G83_FDPK7_TheScanFlagsWhatDoesNotResolve()
    {
        var problems = UnresolvedCitations("See R999.99, G9999, item 99999, D99999 and `docs/runbooks/no-such-runbook.md`; also R6.39 and G82.");
        Assert.True(problems.Count == 5 && problems.All(p => !p.StartsWith("R6.39", StringComparison.Ordinal) && !p.StartsWith("G82", StringComparison.Ordinal)), "the scan flagged: " + string.Join("; ", problems));
    }

    // ---------------------------------------------------------------- BND11

    /// <summary>
    /// BND11 (errata G84, R6.68 (rev.); D125 closed): the four documents that spoke of the Forum's body bound say it is built. None says the Forum has no bound of its own, none names the probes the stage deleted, and each states the
    /// Forum's figure as the white paper's R6.39 number plus one byte, parsed from the white paper (the same figure as <see cref="PublishedRequestBound.Cap"/>, so the document is read against R6.39 and not against
    /// the code). The hosting runbook names G84's Status and the three probes the operator runs (BND1, BND2, BND6), and those tests exist; the edge's runbook says the bound is not a bound on memory or on bodies in flight.
    /// Moved here, not duplicated, from DPK11's four assertions about D125's wait.
    /// </summary>
    [Fact]
    public void R6_68_BND11_TheDocumentsSayTheForumBoundsABodyAndStateTheFigure()
    {
        var problems = new List<string>();
        var figure = PublishedRequestBound.Cap().ToString("N0", CultureInfo.InvariantCulture);
        var documents = new[] { "docs/runbooks/reverse-proxy.md", "docs/runbooks/hosted-instance.md", "README.md", "deploy/proxy/curia-locations.conf" };
        foreach (var path in documents)
        {
            var text = Flat(Text(path));
            if (NoBoundOfItsOwn().IsMatch(text)) problems.Add($"{path} still says the Forum has no bound of its own");
            if (!text.Contains(figure, StringComparison.Ordinal)) problems.Add($"{path} does not state the Forum's figure {figure}");
            foreach (var gone in new[] { "BCAP1", "BCAP2", "BodyBoundNotYetBuiltTests" })
                if (text.Contains(gone, StringComparison.Ordinal)) problems.Add($"{path} names {gone}, which the stage deleted");
        }

        var hosting = Flat(Runbook("hosted-instance"));
        foreach (var needed in new[] { "G84's Status", "BND1", "BND2", "BND6" })
            if (!hosting.Contains(needed, StringComparison.Ordinal)) problems.Add($"the hosting runbook does not say '{needed}'");
        foreach (var test in new[] { "BodyBoundTests", "BodyBoundKestrelTests" })
        {
            if (!hosting.Contains(test, StringComparison.Ordinal)) problems.Add($"the hosting runbook does not name {test}");
            if (!AllTestSource().Contains("class " + test, StringComparison.Ordinal) && !AllTestSource().Contains("class " + test + "(", StringComparison.Ordinal)) problems.Add($"the test class '{test}' the runbook names does not exist");
        }

        var proxy = Flat(Runbook("reverse-proxy"));
        if (!proxy.Contains("in flight", StringComparison.Ordinal)) problems.Add("reverse-proxy.md does not say the bound is not a bound on bodies in flight");
        if (!proxy.Contains("G84", StringComparison.Ordinal)) problems.Add("reverse-proxy.md does not name G84");
        if (proxy.Contains("a client screens before it sends", StringComparison.Ordinal)) problems.Add("reverse-proxy.md says a client screens before it sends, which it does not (item 399 (g))");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [GeneratedRegex(@"no bound of its own( on a request body)?( until D125)?|has no body bound of its own|the only 1 MiB bound", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex NoBoundOfItsOwn();

    // ---------------------------------------------------------------- DPK9

    /// <summary>The text of every string literal in a C# file, concatenated in order, with the two origin constants expanded and any other interpolation replaced by a marker.</summary>
    private static string LiteralText(string relative)
    {
        var sb = new StringBuilder();
        foreach (Match m in StringLiteral().Matches(Text(relative)))
            sb.Append(m.Groups[1].Value.Replace("\\\"", "\"", StringComparison.Ordinal));
        return sb.ToString()
            .Replace("{ConfigurationKey}", "Curia:PublicOrigin", StringComparison.Ordinal)
            .Replace("{EnvironmentVariable}", "CURIA_PUBLIC_ORIGIN", StringComparison.Ordinal);
    }

    /// <summary>
    /// DPK9. The hosting runbook's section "What a refused start looks like" quotes the Forum's own refusal words (one <c>&gt; </c> line per case): at least the four measured bad-origin cases, each phrase (split at
    /// colon-space, because the source builds a message from several literals) a string literal of <c>PublicOrigin.cs</c>. It starts no process and says so: the exit code and the stack are observed in the live run only.
    /// </summary>
    [Fact]
    public void G83_DPK9_TheRefusedStartQuotationsAreTheSourcesOwnWords()
    {
        var problems = new List<string>();
        var runbook = Runbook("hosted-instance");
        var start = runbook.IndexOf("What a refused start looks like", StringComparison.Ordinal);
        if (start < 0) problems.Add("hosted-instance.md has no section 'What a refused start looks like'");
        else
        {
            var end = runbook.IndexOf("\n## ", start, StringComparison.Ordinal);
            var section = end < 0 ? runbook[start..] : runbook[start..end];
            var quotes = section.Split('\n').Where(l => l.StartsWith("> ", StringComparison.Ordinal)).Select(l => l[2..].Trim()).ToList();
            if (quotes.Count < 4) problems.Add($"the section quotes {quotes.Count} refusals, not the four measured cases (absent, no scheme, trailing slash, upper-case scheme)");
            // R11.112 (errata G85): the pair's refusal is quoted too; its one hole is the origin, which the runbook fills with its example (DEV6 reads the whole text against HostingGuard.Words).
            var source = LiteralText("src/Curia.Api/PublicOrigin.cs") + LiteralText("src/Curia.Api/HostingGuard.cs").Replace("{Origin}", "https://forum.example", StringComparison.Ordinal);
            foreach (var quote in quotes)
                foreach (var part in quote.Split(": ", StringSplitOptions.RemoveEmptyEntries))
                    if (!source.Contains(part.Trim().TrimEnd('.'), StringComparison.Ordinal))
                        problems.Add($"the quoted phrase '{part}' is no string literal of PublicOrigin.cs");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("```[a-z]*\n(.*?)\n```", RegexOptions.Singleline)]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"\bCURIA_[A-Z_]+\b|\bCuria__[A-Za-z_]+\b")]
    private static partial Regex VariableName();

    [GeneratedRegex(@"curia-operator\s+([a-z][a-z-]*)")]
    private static partial Regex OperatorVerb();

    [GeneratedRegex(@"(?:^|[\s;&|(])curia\s+([a-z][a-z-]*)(.*)$")]
    private static partial Regex ClientVerb();

    [GeneratedRegex(@"--[a-z][a-z-]*")]
    private static partial Regex Flag();

    [GeneratedRegex(@"curia-operator --help[^.]{0,120}CURIA_EVENTS_POSTGRES[^.]{0,120}any value")]
    private static partial Regex HelpNeedsVariableSentence();

    [GeneratedRegex(@"AllowedHosts[^.]{0,200}\b(unset|not set|left unset)\b|\b(unset|not set|left unset)\b[^.]{0,200}AllowedHosts")]
    private static partial Regex AllowedHostsUnset();

    [GeneratedRegex(@"throw new InvalidOperationException\((?:[^;]*?)\);", RegexOptions.Singleline)]
    private static partial Regex StartRefusal();

    [GeneratedRegex(@"const string EnvironmentVariable = ""(CURIA_[A-Z_]+)""")]
    private static partial Regex EnvironmentConstant();

    [GeneratedRegex(@"BannedLiterals = \[(.*?)\];", RegexOptions.Singleline)]
    private static partial Regex BannedLiterals();

    [GeneratedRegex(@"^(?:\S*/)?curia-operator sign-head --by \S+ --if-grown$")]
    private static partial Regex ExecStart();

    [GeneratedRegex(@"\bR\d+\.\d+\b")]
    private static partial Regex Requirement();

    [GeneratedRegex(@"\bG\d+\b")]
    private static partial Regex Errata();

    [GeneratedRegex(@"\bitem (\d+)\b")]
    private static partial Regex Item();

    [GeneratedRegex(@"\bD\d+\b")]
    private static partial Regex Defect();

    [GeneratedRegex(@"`((?:docs|deploy|tools|src|tests|db|conformance)/[^`\s]+)`")]
    private static partial Regex RepoPath();

    [GeneratedRegex(@"""((?:[^""\\\n]|\\.)*)""")]
    private static partial Regex StringLiteral();
}
