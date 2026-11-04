#pragma warning disable CA1416 // the kept-act files are mode 0600 and 0700 (R11.79): the probes read and set Unix modes, as PrivateFiles does

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// One printed command a real run of the client showed, with the site it came from and its spelling normalized (the agent flag, a digest and a post id left out).
/// </summary>
internal sealed record PrintedLine(string Scenario, string Command, string Spelling, bool Placeholder);

/// <summary>
/// The twelve sites of plan ruling 23 that print an identity command for the reader to run, each driven into the state it prints in on a host of two profiles
/// (<c>"B-two"</c> and <c>"a-one"</c>), the printing profile being <c>a-one</c> (errata G59, R11.82 (b)). The scenarios are the plan's "found at run time": the commands are read out of the
/// real output, never listed from the code.
/// </summary>
internal sealed partial class PrintedScenarios
{
    internal const string Printer = "a-one";
    internal const string Other = "B-two";
    private const string UnknownDigest = "sha256:" + "e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1";
    private const string Parent = "01PARENT00000000000000000A";

    /// <summary>The spellings ruling 23 tabulates, by site: the 12 sites and their 15 spellings (asserted after the list).</summary>
    internal static readonly (string Scenario, string[] Spellings)[] Table =
    [
        ("act line after a 503", ["curia resend <digest>"]),
        ("resend refusal", ["curia acts --discard <digest>"]),
        ("stop sentence", ["curia resend <digest>"]),
        ("listing footer", ["curia resend <digest>", "curia acts --discard <digest>"]),
        ("damaged line", ["curia acts --discard <digest>"]),
        ("unknown line", ["curia resend <digest>"]),
        ("closing line", ["curia resend <digest>"]),
        ("kept acts full", ["curia acts", "curia acts --discard <digest>"]),
        ("no act of that digest", ["curia acts"]),
        ("budget refusal", ["curia budget"]),
        ("receipt without a budget", ["curia budget"]),
        ("flag receipt", ["curia flags", "curia flags <post-id>"]),
    ];

    private readonly AgentHost _host;

    internal PrintedScenarios(AgentHost host) => _host = host;

    internal List<(string Scenario, AgentRun Run)> Runs { get; } = [];

    /// <summary>Called with each scenario's run right after it, while the host is in the state the scenario left it.</summary>
    internal Func<string, AgentRun, Task>? After { get; set; }

    private async Task<AgentRun> RunAsync(string scenario, params string[] args)
    {
        var run = await _host.RunAsync(args);
        Runs.Add((scenario, run));
        if (After is { } after) await after(scenario, run);
        return run;
    }

    internal string ActDigest { get; set; } = string.Empty;

    /// <summary>Drives every scenario once; the runs are kept in <see cref="Runs"/>.</summary>
    internal async Task RunAllAsync()
    {
        var comment = new[] { "comment", "--board", "dotnet", "--parent", Parent, "--body", "A body.", "--agent", Printer };

        // 1. the act line: a post answered 503 keeps the act and says how to send it again.
        await RunAsync("act line after a 503", comment);

        // 2 and 3. a resend refused 503: the first prints the sentence about the first send, the second the stop sentence.
        var (digest, _) = _host.PlantAct(Printer, attempts: 0, lastAnswer: null);
        ActDigest = digest;
        await RunAsync("resend refusal", "resend", digest, "--agent", Printer);
        await RunAsync("stop sentence", "resend", digest, "--agent", Printer);

        // 4. a listing with a kept act, and 5. one with a damaged file named for a digest.
        await RunAsync("listing footer", "acts", "--agent", Printer);
        var damaged = Path.Combine(_host.Store.DirectoryFor(Printer), "acts", new string('f', 64) + ".act");
        await File.WriteAllTextAsync(damaged, "not a header\n");
        File.SetUnixFileMode(damaged, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        await RunAsync("damaged line", "acts", "--agent", Printer);
        File.Delete(damaged);

        // 6 and 7. recheck of the digest of an act this host kept that the Forum does not know (the case the line is written for): the unknown line and the closing line, given the agent. The printed
        // resend is then run as printed, and it finds the act under the profile that printed it (a digest nothing kept would refuse as "not kept" whoever it acted as).
        _host.OnOther = request => request.RequestUri!.AbsolutePath.StartsWith("/v1/posts/batch", StringComparison.Ordinal)
            ? ScriptedForum.Json(200, "{\"items\":[" + StubLog.BatchItem(digest, "unknown") + "]}")
            : ScriptedForum.Problem(404, "curia/posts/not-found", "No such post");
        await RunAsync("unknown line", "recheck", digest, "--agent", Printer, "--forum", AgentHost.ForumAddress.ToString());
        await RunAsync("closing line", "recheck", digest, "--agent", Printer, "--forum", AgentHost.ForumAddress.ToString());

        // 9. a resend of a digest nothing kept.
        await RunAsync("no act of that digest", "resend", UnknownDigest, "--agent", Printer);

        // 10. a refusal that spends the budget; 11. a receipt that does not say where the budget stands; 12. a flag receipt.
        _host.OnPost = (_, _) => ScriptedForum.Problem(403, "curia/authz/denied", "Denied", "table-11/rate-budget-exhausted next_slot_at=2026-01-01T00:00:00Z");
        await RunAsync("budget refusal", comment);
        _host.OnPost = (_, body) => ScriptedForum.Json(201,
            "{\"post_id\":\"01TESTRECEIPTPOST000000000A\",\"digest\":\"" + ReceivedAct.DigestOf(body!) + "\",\"server_ts\":\"1970-01-01T00:00:00.0000000+00:00\",\"risk_flags\":[],\"vector_index\":\"indexed\"}");
        await RunAsync("receipt without a budget", comment);
        _host.OnOther = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/flags", StringComparison.Ordinal)
            ? ScriptedForum.Json(201, "{\"post_id\":\"" + Parent + "\",\"kind\":\"spam\",\"raised_at\":\"2026-10-29T12:00:00.0000000+00:00\",\"post_served\":true}")
            : ScriptedForum.Problem(404, "curia/posts/not-found", "No such post");
        await RunAsync("flag receipt", "flag", Parent, "--kind", "spam", "--rationale", "A reason.", "--agent", Printer);
        _host.OnPost = null;
        _host.OnOther = null;

        // 8. the directory full: 64 kept acts, then a write.
        for (var i = 0; i < 64; i++) _host.PlantAct(Printer, body: "Filler " + i.ToString(CultureInfo.InvariantCulture));
        await RunAsync("kept acts full", comment);
        foreach (var file in Directory.GetFiles(Path.Combine(_host.Store.DirectoryFor(Printer), "acts"))) File.Delete(file);
    }

    /// <summary>Every backticked <c>curia &lt;identity verb&gt;</c> in a run's text, with its spelling normalized.</summary>
    internal static IReadOnlyList<PrintedLine> CommandsOf(string scenario, AgentRun run)
    {
        var found = new List<PrintedLine>();
        foreach (Match m in Backticked().Matches(run.Stdout + "\n" + run.Stderr))
        {
            var command = CliRun.Fold(m.Groups[1].Value).Trim();
            if (!IdentityCommand().IsMatch(command)) continue;
            var spelling = AgentFlag().Replace(command, string.Empty);
            spelling = Regex.Replace(spelling, @"sha256:[0-9a-f]{64}", "<digest>", RegexOptions.CultureInvariant);
            spelling = Regex.Replace(spelling, @"01[0-9A-Z]{24}", "<post-id>", RegexOptions.CultureInvariant);
            found.Add(new PrintedLine(scenario, command, spelling.Trim(), command.Contains('<', StringComparison.Ordinal)));
        }

        // R10.88's pointer is prose and not a backticked span: "curia flags lists what you raised, and curia flags <post-id> what was raised against a post of yours".
        foreach (Match m in UnquotedFlags().Matches(CliRun.Fold(run.Stdout + " " + run.Stderr)))
        {
            var command = m.Value.Trim();
            found.Add(new PrintedLine(scenario, command, AgentFlag().Replace(command, string.Empty).Trim(), command.Contains('<', StringComparison.Ordinal)));
        }

        return found;
    }

    /// <summary>Spellings that name a verb in a clause and are not a command to run (ruling 23's left-out sites): they are asserted to carry no flag.</summary>
    internal static readonly string[] LeftOut = ["curia resend"];

    /// <summary>The words of a printed command as an argument list for <c>Program.RunAsync</c>, split as a POSIX shell splits single-quoted words; a placeholder is filled with a real value.</summary>
    internal List<string> Argv(string command)
    {
        var filled = command.Replace("<digest>", ActDigest, StringComparison.Ordinal).Replace("<post-id>", Parent, StringComparison.Ordinal);
        var words = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;
        foreach (var c in filled)
        {
            if (c == '\'') { quoted = !quoted; started = true; continue; }
            if (c == ' ' && !quoted)
            {
                if (started) words.Add(current.ToString());
                current.Clear();
                started = false;
                continue;
            }

            current.Append(c);
            started = true;
        }

        if (started) words.Add(current.ToString());
        Assert.Equal("curia", words[0]);
        return words[1..];
    }

    [GeneratedRegex(@"`([^`]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Backticked();

    [GeneratedRegex(@"^curia (resend|acts|budget|flags)\b", RegexOptions.CultureInvariant)]
    private static partial Regex IdentityCommand();

    [GeneratedRegex(@"(?<!`)\bcuria flags(?: <post-id>)?(?: --agent (?:'[^']*'|<name>))?(?= lists| what)", RegexOptions.CultureInvariant)]
    private static partial Regex UnquotedFlags();

    [GeneratedRegex(@" --agent (?:'[^']*'|<name>)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex AgentFlag();
}

/// <summary>
/// CWK8 and CWK55 (errata G59, R11.82 (b); plan ruling 23): the commands a client prints for the agent to run carry the agent, found at run time and run as printed. Each probe is in its
/// failing form: on the seam no printed command carries <c>--agent</c>, so a command printed by <c>a-one</c> acts as <c>B-two</c>, the ordinal-first profile.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each host is a using declaration; the analyzer loses the ownership across the awaits.")]
[Collection("console")]
public sealed class PrintedCommandRunTests
{
    private const string Flagged = " --agent 'a-one'";

    private static async Task<(AgentHost Host, PrintedScenarios Scenarios)> StartAsync()
    {
        var host = new AgentHost(PrintedScenarios.Other, PrintedScenarios.Printer);
        host.PlantAct(PrintedScenarios.Other, body: "B-two's own act.");
        var scenarios = new PrintedScenarios(host);
        await scenarios.RunAllAsync();
        return (host, scenarios);
    }

    /// <summary>
    /// CWK8. The printed commands, run. The 12 sites and 15 spellings of ruling 23 (each count asserted after the list): every command is read out of a real output, carries <c> --agent 'a-one'</c>,
    /// and run as printed acts as <c>a-one</c>, exit not 1; the recheck commands carry no flag; <c>NOT REACHED</c> for any site a scenario did not reach. Predicted red: <c>printed
    /// "curia resend sha256:..." acted as "B-two", printed by "a-one"</c>.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK8_EveryPrintedIdentityCommandActsAsTheProfileThatPrintedIt()
    {
        Assert.Equal(12, PrintedScenarios.Table.Length);
        Assert.Equal(15, PrintedScenarios.Table.Sum(r => r.Spellings.Length));

        using var host = new AgentHost(PrintedScenarios.Other, PrintedScenarios.Printer);
        host.PlantAct(PrintedScenarios.Other, body: "B-two's own act.");
        var scenarios = new PrintedScenarios(host);
        var problems = new List<string>();
        var reached = new HashSet<string>(StringComparer.Ordinal);
        scenarios.After = async (scenario, run) =>
        {
            var printed = PrintedScenarios.CommandsOf(scenario, run);
            var wanted = PrintedScenarios.Table.Single(r => r.Scenario == scenario).Spellings;
            foreach (var spelling in wanted)
                if (printed.Any(p => p.Spelling == spelling)) reached.Add(scenario + "|" + spelling);

            foreach (var command in printed.Where(p => wanted.Contains(p.Spelling)).DistinctBy(p => p.Command))
            {
                if (!command.Command.EndsWith(Flagged, StringComparison.Ordinal)) problems.Add($"[{scenario}] printed \"{command.Command}\" with no flag");

                // Run as printed, on a copy of the host as the scenario left it (a discard must not change what a later scenario runs on).
                using var copy = host.Clone();
                var ran = await copy.RunAsync([.. scenarios.Argv(command.Command)]);
                // A command with no flag acts as the first profile found, which holds no act of the printing profile's: the refusal names it as "no act of that digest is kept".
                var unflagged = !scenarios.Argv(command.Command).Contains("--agent");
                var acted = ran.ActedAs.Contains(PrintedScenarios.Other) || (unflagged && ran.ActedAs.Count == 0) ? $"acted as \"{PrintedScenarios.Other}\"" : ran.ActedAs.Contains(PrintedScenarios.Printer) ? "acted as \"a-one\"" : "acted as no one";
                if (ran.Exit == 1 || ran.ActedAs.Contains(PrintedScenarios.Other))
                    problems.Add($"[{scenario}] printed \"{command.Command}\" {acted}, printed by \"a-one\" (exit {ran.Exit}: {Shorten(ran.Text)})");
            }

            foreach (var recheck in Regex.Matches(CliRun.Fold(run.Stdout + " " + run.Stderr), @"`(curia recheck [^`]*)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value))
                if (recheck.Contains("--agent", StringComparison.Ordinal)) problems.Add($"[{scenario}] a recheck command carries a flag: \"{recheck}\"");
        };

        await scenarios.RunAllAsync();
        foreach (var (scenario, spellings) in PrintedScenarios.Table)
            foreach (var spelling in spellings)
                if (!reached.Contains(scenario + "|" + spelling)) problems.Add($"NOT REACHED: [{scenario}] \"{spelling}\"");

        Red.None(problems, "every printed command ends with --agent 'a-one' and, run as printed, acts as a-one; no recheck command carries the flag; every site reached");
    }

    private static string Shorten(string text) => text.Length <= 110 ? text.Trim() : text[..110].Trim() + "...";

    /// <summary>
    /// CWK55. The printed commands, found at run time: every backticked <c>curia &lt;identity verb&gt;</c> extracted from the real outputs of the states the table's sites print in is contained
    /// in ruling 23's table (a printer the table does not name is a finding), and every row of the table was reached. Predicted red on the seam: <c>printed "curia acts --discard sha256:..."
    /// with no flag</c> (the containment holds on the seam, as the spellings are the same; the flag is what is missing).
    /// </summary>
    [Fact]
    public async Task R11_82_CWK55_ThePrintedCommandsAreExactlyThoseTheTableNames()
    {
        var (host, scenarios) = await StartAsync();
        using (host)
        {
            var allowed = PrintedScenarios.Table.SelectMany(r => r.Spellings).ToHashSet(StringComparer.Ordinal);
            var problems = new List<string>();
            var reached = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (scenario, run) in scenarios.Runs)
            {
                foreach (var command in PrintedScenarios.CommandsOf(scenario, run))
                {
                    if (PrintedScenarios.LeftOut.Contains(command.Spelling)) continue;
                    if (!allowed.Contains(command.Spelling)) problems.Add($"[{scenario}] a printed command the table does not name: \"{command.Command}\"");
                    else if (!command.Command.EndsWith(Flagged, StringComparison.Ordinal)) problems.Add($"[{scenario}] printed \"{command.Command}\" with no flag");
                    reached.Add(scenario + "|" + command.Spelling);
                }
            }

            foreach (var (scenario, spellings) in PrintedScenarios.Table)
                foreach (var spelling in spellings)
                    if (!reached.Contains(scenario + "|" + spelling)) problems.Add($"NOT REACHED: [{scenario}] \"{spelling}\"");

            // The left-out site reached here: the damaged line's bare "curia resend" is a verb a clause says refuses the file, not a command to run, and carries no flag.
            var damaged = scenarios.Runs.Single(r => r.Scenario == "damaged line").Run;
            if (!Regex.IsMatch(damaged.Text, @"`curia resend` \(in", RegexOptions.CultureInvariant)) problems.Add("the left-out \"`curia resend` refuses it\" clause was not printed, so the control did not run");
            if (damaged.Text.Contains("`curia resend --agent", StringComparison.Ordinal)) problems.Add("a left-out site was given a flag: \"`curia resend --agent\"");

            Red.None(problems, "every printed identity command is in the table and carries the agent; every row reached; the left-out site carries no flag");
        }
    }

    // ----- CWK10 -----

    /// <summary>The rule of R11.82 applied to a published text: each of the commands the rule names, as a span, ends with <c> --agent &lt;slug&gt;</c>; <c>curia recheck</c> is left as it is.</summary>
    internal static string ApplyRule(string published) =>
        Regex.Replace(published, @"`(curia (?:resend <digest>|acts --discard <digest>|budget|flags|acts))`", "`$1 --agent <slug>`", RegexOptions.CultureInvariant);

    /// <summary>
    /// CWK10. The rule applied to the published words: the identity commands are found in G58's published paragraphs by pattern, the rule is applied to the text (a regex over the errata, a
    /// different artifact from the printers), and each printed line holds every stretch of the result between placeholders (at least 20 characters, trap 141), so a printer that
    /// forgets the flag is red and so is one that adds it to <c>curia recheck</c>. Predicted red: <c>printed line differs: missing " --agent"</c>.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK10_ThePrintedLinesAreThePublishedWordsWithTheRuleApplied()
    {
        var (host, scenarios) = await StartAsync();
        using (host)
        {
            var digest = scenarios.ActDigest;
            var published = new (string Name, string Scenario, string Words)[]
            {
                ("act words", "act line after a 503", PublishedResendWords.ActWords),
                ("stop words", "stop sentence", PublishedResendWords.StopWords),
                ("resend refusal words", "resend refusal", PublishedResendWords.ResendRefusalWords),
                ("unknown line", "unknown line", PublishedResendWords.UnknownLine),
                ("closing line", "closing line", PublishedResendWords.ClosingLine),
                ("listing last line", "listing footer", PublishedResendWords.ListingFooterWords),
            };
            Assert.Equal(6, published.Length);

            var problems = new List<string>();
            foreach (var (name, scenario, words) in published)
            {
                var run = scenarios.Runs.First(r => r.Scenario == scenario).Run;
                var printed = run.Text;
                var expected = PublishedResendWords.As(ApplyRule(words).Replace("<slug>", "'a-one'", StringComparison.Ordinal), "a-one");
                if (!expected.Contains("--agent 'a-one'", StringComparison.Ordinal) && name != "resend refusal words") problems.Add($"{name}: the rule found no command in the published text");

                // The published text holds placeholders (<n>, <u>, <instant>, <digest>); each stretch of it between two is matched alone, so a placeholder never defeats the match.
                var stretches = Regex.Split(expected, @"<(?:n|u|instant|digest|post id)>", RegexOptions.CultureInvariant)
                    .Select(s => CliRun.Fold(s).Trim()).Where(s => s.Length >= 20).ToArray();
                var checkedAny = false;
                foreach (var stretch in stretches)
                {
                    checkedAny = true;
                    if (!printed.Contains(stretch, StringComparison.Ordinal)) problems.Add($"{name}: printed line differs: missing \"{Tail(stretch)}\"");
                }

                if (!checkedAny) problems.Add($"{name}: no stretch of 20 characters or more to compare");
                if (words.Contains("curia recheck", StringComparison.Ordinal) && printed.Contains("curia recheck " + digest + " --agent", StringComparison.Ordinal)) problems.Add($"{name}: the flag was added to curia recheck");
            }

            Red.None(problems, "each printed line holds the published stretches with the rule applied, and the recheck command stays as it is");
        }
    }

    private static string Tail(string text) => text.Length <= 90 ? text : "..." + text[^90..];

    // ----- CWK9 -----

    private const string UnknownAsked = "sha256:" + "e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2e2";

    /// <summary>
    /// CWK9. The <c>unknown</c> line. <c>recheck --agent B-two</c> prints the resend command with <c>'B-two'</c>; <c>recheck</c> with none prints <c>--agent &lt;name&gt;</c> and the sentence that <c>&lt;name&gt;</c>
    /// is the profile that kept the act, on a host of one, two and no profile alike; exit 5 and exactly one request in each, and no profile read to fill it. Predicted red: <c>expected "--agent",
    /// found none</c>.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK9_TheUnknownLineSaysWhichProfileToResendAs()
    {
        var problems = new List<string>();
        foreach (var names in new[] { Array.Empty<string>(), ["a-one"], ["B-two", "a-one"] })
        {
            using var host = new AgentHost(names);
            host.OnOther = request => request.RequestUri!.AbsolutePath.StartsWith("/v1/posts/batch", StringComparison.Ordinal)
                ? ScriptedForum.Json(200, "{\"items\":[" + StubLog.BatchItem(UnknownAsked, "unknown") + "]}")
                : ScriptedForum.Problem(404, "curia/posts/not-found", "No such post");

            var bare = await host.RunAsync("recheck", UnknownAsked, "--forum", AgentHost.ForumAddress.ToString());
            var resend = Regex.Match(bare.Text, @"`curia resend " + Regex.Escape(UnknownAsked) + @"([^`]*)`", RegexOptions.CultureInvariant);
            var label = $"{names.Length} profile(s)";
            if (bare.Exit != 5) problems.Add($"{label}, no flag: exit {bare.Exit}, expected 5");
            if (bare.Requests.Count != 1) problems.Add($"{label}, no flag: {bare.Requests.Count} request(s), expected 1");
            if (!resend.Success || resend.Groups[1].Value != " --agent <name>") problems.Add($"{label}, no flag: expected \"--agent <name>\", found \"{(resend.Success ? resend.Groups[1].Value.Trim() : "none")}\"");
            if (!bare.Text.Contains("<name>", StringComparison.Ordinal) || !bare.Text.Contains("is the profile that kept the act", StringComparison.Ordinal)) problems.Add($"{label}, no flag: the sentence that <name> is the profile that kept the act is missing");

            if (names.Length == 0) continue;
            var named = await host.RunAsync("recheck", UnknownAsked, "--agent", names[0], "--forum", AgentHost.ForumAddress.ToString());
            var flagged = Regex.Match(named.Text, @"`curia resend " + Regex.Escape(UnknownAsked) + @"([^`]*)`", RegexOptions.CultureInvariant);
            if (named.Exit != 5) problems.Add($"{label}, --agent {names[0]}: exit {named.Exit}, expected 5");
            if (named.Requests.Count != 1) problems.Add($"{label}, --agent {names[0]}: {named.Requests.Count} request(s), expected 1");
            if (!flagged.Success || flagged.Groups[1].Value != $" --agent '{names[0]}'") problems.Add($"{label}, --agent {names[0]}: expected \"--agent\", found \"{(flagged.Success ? flagged.Groups[1].Value.Trim() : "none")}\"");
        }

        Red.None(problems, "the flag with the given profile where recheck was given one, and --agent <name> with the sentence where it was not, on every host; exit 5, one request");
    }
}
