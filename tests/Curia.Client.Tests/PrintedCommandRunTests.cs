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
/// The fourteen command-line sites (plan ruling 23's twelve, plus <c>whoami</c> and <c>enrol</c>, ruling 35) that print an identity command for the reader to run, each driven into the state
/// it prints in on a host of two profiles (<c>"B-two"</c> and <c>"a-one"</c>), the printing profile being <c>a-one</c> (<c>c-three</c> for <c>enrol</c>, which creates it) (errata G59, R11.82 (b)).
/// The scenarios are the plan's "found at run time": the commands are read out of the real output, never listed from the code. The fifteenth site, <c>curia-mcp</c>'s flag-receipt pointer, is
/// held by <c>PrintedAgentToolTests</c>.
/// </summary>
internal sealed partial class PrintedScenarios
{
    internal const string Printer = "a-one";
    internal const string Other = "B-two";
    private const string UnknownDigest = "sha256:" + "e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1e1";
    private const string Parent = "01PARENT00000000000000000A";

    /// <summary>The profile <c>enrol</c> creates in the <c>enrol</c> scenario.</summary>
    internal const string Enrolled = "c-three";

    /// <summary>
    /// The spellings plan rulings 23 and 35 tabulate for the command line, by site: 14 sites and 17 spellings (asserted after the list). With <c>curia-mcp</c>'s flag-receipt pointer (one site, one
    /// spelling, in <c>PrintedAgentToolTests</c>) the denominator is 15 sites and 18 spellings.
    /// </summary>
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
        ("whoami", ["curia budget"]),
        ("enrol", ["curia budget"]),
    ];

    /// <summary>Sites outside the command line that print an identity command (the <c>curia-mcp</c> flag-receipt pointer): the part of the denominator <c>PrintedAgentToolTests</c> holds.</summary>
    internal const int McpSites = 1;

    /// <summary>Spellings those sites print.</summary>
    internal const int McpSpellings = 1;

    /// <summary>The scenarios whose command ends <c> --agent &lt;name&gt;</c> whatever <c>--agent</c> the run was given: <c>recheck</c> reads no profile (plan ruling 38).</summary>
    internal static readonly string[] PlaceholderScenarios = ["unknown line", "closing line"];

    /// <summary>The profile a scenario's printed commands name.</summary>
    internal static string PrinterOf(string scenario) => scenario == "enrol" ? Enrolled : Printer;

    private readonly AgentHost _host;

    internal PrintedScenarios(AgentHost host) => _host = host;

    internal List<(string Scenario, AgentRun Run)> Runs { get; } = [];

    /// <summary>Called with each scenario's run right after it, while the host is in the state the scenario left it.</summary>
    internal Func<string, AgentRun, Task>? After { get; set; }

    private async Task<AgentRun> RunAsync(string scenario, params string[] args)
    {
        var run = await _host.RunAsync(args);
        if (scenario == "enrol") _host.AdoptEnrolled(Enrolled);
        Runs.Add((scenario, run));
        if (After is { } after) await after(scenario, run);
        return run;
    }

    internal string ActDigest { get; set; } = string.Empty;

    /// <summary>Drives every scenario once; the runs are kept in <see cref="Runs"/>.</summary>
    internal async Task RunAllAsync()
    {
        var comment = new[] { "comment", "--board", "dotnet", "--parent", Parent, "--body", "A body.", "--agent", Printer };

        // 13. whoami, which knows the profile it ran as; 14. enrol, which just created one. Both print the command that reads the tier (plan ruling 35).
        await RunAsync("whoami", "whoami", "--agent", Printer);
        _host.OnOther = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/agents"
            ? ScriptedForum.Json(201, "{\"agent_id\":\"" + AgentHost.AgentIdOf(Enrolled) + "\",\"kid\":\"" + Enrolled + "-1\",\"enrolled_at\":\"2026-01-01T00:00:00.0000000+00:00\",\"owner_verified\":false}")
            : ScriptedForum.Problem(404, "curia/posts/not-found", "No such post");
        await RunAsync("enrol", "enrol", "--agent", Enrolled, "--agent-id", AgentHost.AgentIdOf(Enrolled), "--forum", AgentHost.ForumAddress.ToString());
        _host.OnOther = null;

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

    /// <summary>
    /// Every <c>curia &lt;identity verb&gt;</c> in a run's text that is shown as a command to run, with its spelling normalized: in a backtick span, or in single quotes (plan ruling 35: quoting is not
    /// a criterion). The two published refusal sentences name the verb that was run in single quotes and are not commands; they are exempt, found by the words the errata publishes after the
    /// quoted verb (never listed here).
    /// </summary>
    internal static IReadOnlyList<PrintedLine> CommandsOf(string scenario, AgentRun run)
    {
        var found = new List<PrintedLine>();
        foreach (Match m in Backticked().Matches(run.Stdout + "\n" + run.Stderr))
        {
            var command = CliRun.Fold(m.Groups[1].Value).Trim();
            if (!IdentityCommand().IsMatch(command)) continue;
            found.Add(Line(scenario, command));
        }

        var folded = CliRun.Fold(run.Stdout + " " + run.Stderr);
        var tails = RefusalTails();
        foreach (Match m in SingleQuoted().Matches(folded))
        {
            var command = m.Groups[1].Value.Trim();
            if (tails.Any(t => string.CompareOrdinal(folded, m.Index + m.Length, t, 0, t.Length) == 0)) continue;
            found.Add(Line(scenario, command));
        }

        // R10.88's pointer is prose and not a backticked span: "curia flags lists what you raised, and curia flags <post-id> what was raised against a post of yours".
        foreach (Match m in UnquotedFlags().Matches(folded))
        {
            var command = m.Value.Trim();
            found.Add(new PrintedLine(scenario, command, AgentFlag().Replace(command, string.Empty).Trim(), command.Contains('<', StringComparison.Ordinal)));
        }

        return found;
    }

    private static PrintedLine Line(string scenario, string command)
    {
        var spelling = AgentFlag().Replace(command, string.Empty);
        spelling = Regex.Replace(spelling, @"sha256:[0-9a-f]{64}", "<digest>", RegexOptions.CultureInvariant);
        spelling = Regex.Replace(spelling, @"01[0-9A-Z]{24}", "<post-id>", RegexOptions.CultureInvariant);
        return new PrintedLine(scenario, command, spelling.Trim(), command.Contains('<', StringComparison.Ordinal));
    }

    /// <summary>The words the errata publishes after the single-quoted <c>'curia &lt;verb&gt;'</c> of each refusal sentence (up to the next placeholder), each at least 20 characters (trap 141).</summary>
    private static string[] RefusalTails()
    {
        var tails = new List<string>();
        foreach (var words in new[] { PublishedAgentWords.SeveralWords, PublishedAgentWords.BlankWords })
        {
            const string Quoted = "'curia <verb>'";
            var at = words.IndexOf(Quoted, StringComparison.Ordinal);
            Assert.True(at >= 0, "the published refusal words no longer quote 'curia <verb>'");
            var tail = words[(at + Quoted.Length)..];
            var next = tail.IndexOf('<', StringComparison.Ordinal);
            if (next >= 0) tail = tail[..next];
            Assert.True(tail.Length >= 20, $"the words after the quoted verb are under 20 characters: \"{tail}\"");
            tails.Add(tail);
        }

        return [.. tails];
    }

    /// <summary>
    /// What CWK55 finds in one run: a printed command outside <paramref name="allowed"/>, or one allowed that does not end with the expected agent. <paramref name="reached"/> collects the
    /// scenario and spelling pairs seen. Shared by the fact and its negative control, so the control shows the extraction sees what it would otherwise miss.
    /// </summary>
    internal static List<string> Findings(string scenario, AgentRun run, HashSet<string> allowed, HashSet<string> reached)
    {
        var problems = new List<string>();
        var expectedEnd = PlaceholderScenarios.Contains(scenario) ? " --agent <name>" : $" --agent '{PrinterOf(scenario)}'";
        foreach (var command in CommandsOf(scenario, run))
        {
            if (LeftOut.Contains(command.Spelling)) continue;
            if (!allowed.Contains(command.Spelling)) problems.Add($"[{scenario}] a printed command the table does not name: \"{command.Command}\"");
            else if (!command.Command.EndsWith(expectedEnd, StringComparison.Ordinal)) problems.Add($"[{scenario}] printed \"{command.Command}\" with no flag");
            reached.Add(scenario + "|" + command.Spelling);
        }

        return problems;
    }

    /// <summary>Spellings that name a verb in a clause and are not a command to run (ruling 23's left-out sites): they are asserted to carry no flag.</summary>
    internal static readonly string[] LeftOut = ["curia resend"];

    /// <summary>The words of a printed command as an argument list for <c>Program.RunAsync</c>, split as a POSIX shell splits single-quoted words; a placeholder is filled with a real value.</summary>
    internal List<string> Argv(string command)
    {
        var filled = command.Replace("<digest>", ActDigest, StringComparison.Ordinal).Replace("<post-id>", Parent, StringComparison.Ordinal).Replace("<name>", Printer, StringComparison.Ordinal);
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

    [GeneratedRegex(@"'(curia (?:resend|acts|budget|flags)\b[^']*)'", RegexOptions.CultureInvariant)]
    private static partial Regex SingleQuoted();

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
    private static async Task<(AgentHost Host, PrintedScenarios Scenarios)> StartAsync()
    {
        var host = new AgentHost(PrintedScenarios.Other, PrintedScenarios.Printer);
        host.PlantAct(PrintedScenarios.Other, body: "B-two's own act.");
        var scenarios = new PrintedScenarios(host);
        await scenarios.RunAllAsync();
        return (host, scenarios);
    }

    /// <summary>
    /// CWK8. The printed commands, run. The 15 sites and 18 spellings of plan rulings 23 and 35 (14 sites and 17 spellings on the command line, below; <c>curia-mcp</c>'s pointer in
    /// <c>PrintedAgentToolTests</c>; each count asserted after the list): every command is read out of a real output, carries <c> --agent '&lt;the profile that printed it&gt;'</c> (the two
    /// <c>recheck</c> scenarios, <c> --agent &lt;name&gt;</c>, whatever <c>--agent</c> the recheck was given: plan ruling 38), and run as printed acts as that profile, exit not 1; the recheck
    /// commands carry no flag; <c>NOT REACHED</c> for any site a scenario did not reach. Predicted red: <c>printed "curia resend sha256:..." acted as "B-two", printed by "a-one"</c>.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK8_EveryPrintedIdentityCommandActsAsTheProfileThatPrintedIt()
    {
        Assert.Equal(14, PrintedScenarios.Table.Length);
        Assert.Equal(17, PrintedScenarios.Table.Sum(r => r.Spellings.Length));
        Assert.Equal(15, PrintedScenarios.Table.Length + PrintedScenarios.McpSites);
        Assert.Equal(18, PrintedScenarios.Table.Sum(r => r.Spellings.Length) + PrintedScenarios.McpSpellings);

        using var host = new AgentHost(PrintedScenarios.Other, PrintedScenarios.Printer);
        host.PlantAct(PrintedScenarios.Other, body: "B-two's own act.");
        var scenarios = new PrintedScenarios(host);
        var problems = new List<string>();
        var reached = new HashSet<string>(StringComparer.Ordinal);
        scenarios.After = async (scenario, run) =>
        {
            var printer = PrintedScenarios.PrinterOf(scenario);
            var expectedEnd = PrintedScenarios.PlaceholderScenarios.Contains(scenario) ? " --agent <name>" : $" --agent '{printer}'";
            var printed = PrintedScenarios.CommandsOf(scenario, run);
            var wanted = PrintedScenarios.Table.Single(r => r.Scenario == scenario).Spellings;
            foreach (var spelling in wanted)
                if (printed.Any(p => p.Spelling == spelling)) reached.Add(scenario + "|" + spelling);

            foreach (var command in printed.Where(p => wanted.Contains(p.Spelling)).DistinctBy(p => p.Command))
            {
                if (!command.Command.EndsWith(expectedEnd, StringComparison.Ordinal)) problems.Add($"[{scenario}] printed \"{command.Command}\" with no flag");

                // Run as printed, on a copy of the host as the scenario left it (a discard must not change what a later scenario runs on).
                using var copy = host.Clone();
                var ran = await copy.RunAsync([.. scenarios.Argv(command.Command)]);
                // A command with no flag acts as the first profile found, which holds no act of the printing profile's: the refusal names it as "no act of that digest is kept".
                var unflagged = !scenarios.Argv(command.Command).Contains("--agent");
                var acted = ran.ActedAs.Contains(PrintedScenarios.Other) || (unflagged && ran.ActedAs.Count == 0) ? $"acted as \"{PrintedScenarios.Other}\"" : ran.ActedAs.Contains(printer) ? $"acted as \"{printer}\"" : "acted as no one";
                if (ran.Exit == 1 || ran.ActedAs.Contains(PrintedScenarios.Other))
                    problems.Add($"[{scenario}] printed \"{command.Command}\" {acted}, printed by \"{printer}\" (exit {ran.Exit}: {Shorten(ran.Text)})");
                else if (command.Spelling == "curia budget" && !ran.ActedAs.Contains(printer))
                    problems.Add($"[{scenario}] printed \"{command.Command}\" {acted}, printed by \"{printer}\" (exit {ran.Exit}: {Shorten(ran.Text)})");
            }

            foreach (var recheck in Regex.Matches(CliRun.Fold(run.Stdout + " " + run.Stderr), @"`(curia recheck [^`]*)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value))
                if (recheck.Contains("--agent", StringComparison.Ordinal)) problems.Add($"[{scenario}] a recheck command carries a flag: \"{recheck}\"");
        };

        await scenarios.RunAllAsync();
        foreach (var (scenario, spellings) in PrintedScenarios.Table)
            foreach (var spelling in spellings)
                if (!reached.Contains(scenario + "|" + spelling)) problems.Add($"NOT REACHED: [{scenario}] \"{spelling}\"");

        Red.None(problems, "every printed command ends with the --agent of the profile that printed it (--agent <name> after a recheck) and, run as printed, acts as it; no recheck command carries the flag; every site reached");
    }

    private static string Shorten(string text) => text.Length <= 110 ? text.Trim() : text[..110].Trim() + "...";

    /// <summary>
    /// CWK55. The printed commands, found at run time: every <c>curia &lt;identity verb&gt;</c> in a backtick span or in single quotes (plan ruling 35: quoting is not a criterion), extracted from the
    /// real outputs of the states the table's sites print in, is contained in the table (a printer the table does not name is a finding), carries the agent, and every row of the table was reached.
    /// The two published refusal sentences, which quote the verb that was run, are the only exemption. <b>Negative controls in the same fact</b>: a fixture line carrying <c>'curia budget'</c> is extracted
    /// and reported, and the refusal sentences instantiated with a verb are not extracted, so the extraction is shown to see what it missed and to leave out what is not a command. Predicted red:
    /// <c>printed "curia budget" with no flag</c> (the pointer <c>whoami</c> printed before ruling 35).
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
            foreach (var (scenario, run) in scenarios.Runs) problems.AddRange(PrintedScenarios.Findings(scenario, run, allowed, reached));

            foreach (var (scenario, spellings) in PrintedScenarios.Table)
                foreach (var spelling in spellings)
                    if (!reached.Contains(scenario + "|" + spelling)) problems.Add($"NOT REACHED: [{scenario}] \"{spelling}\"");

            // The left-out site reached here: the damaged line's bare "curia resend" is a verb a clause says refuses the file, not a command to run, and carries no flag.
            var damaged = scenarios.Runs.Single(r => r.Scenario == "damaged line").Run;
            if (!Regex.IsMatch(damaged.Text, @"`curia resend` \(in", RegexOptions.CultureInvariant)) problems.Add("the left-out \"`curia resend` refuses it\" clause was not printed, so the control did not run");
            if (damaged.Text.Contains("`curia resend --agent", StringComparison.Ordinal)) problems.Add("a left-out site was given a flag: \"`curia resend --agent\"");

            // Negative control 1: a single-quoted identity command with no flag is extracted and reported (the extraction sees what the backtick-only one missed).
            var fixture = new AgentRun(0, "Budgets: 'curia budget' reads where each of your budgets stands.", string.Empty, [], []);
            var control = PrintedScenarios.Findings("whoami", fixture, allowed, new HashSet<string>(StringComparer.Ordinal));
            if (!control.Any(f => f.Contains("printed \"curia budget\" with no flag", StringComparison.Ordinal))) problems.Add("the control did not report a single-quoted 'curia budget' with no flag: the extraction does not see single quotes");

            // Negative control 2: the two refusal sentences, as printed for a verb, are not commands.
            foreach (var verb in new[] { "budget", "acts", "resend", "flags" })
            {
                var several = PublishedAgentWords.SeveralWords.Replace("<verb>", verb, StringComparison.Ordinal);
                var blank = PublishedAgentWords.BlankWords.Replace("<verb>", verb, StringComparison.Ordinal);
                var exempt = PrintedScenarios.Findings("budget refusal", new AgentRun(1, string.Empty, several + "\n" + blank, [], []), allowed, new HashSet<string>(StringComparer.Ordinal));
                if (exempt.Count != 0) problems.Add($"the published refusal sentences for '{verb}' were read as printed commands: {string.Join("; ", exempt)}");
            }

            Red.None(problems, "every printed identity command, backticked or single-quoted, is in the table and carries the agent; every row reached; the left-out site carries no flag; the controls see 'curia budget' and leave the refusal sentences out");
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
                // recheck reads no profile (plan ruling 38): its two lines end --agent <name>, and the placeholder's sentence follows the command in the closing line.
                var placeholder = PrintedScenarios.PlaceholderScenarios.Contains(scenario);
                var ruled = ApplyRule(words);
                var expected = placeholder
                    ? PublishedResendWords.As(ruled, null).Replace("--agent <name>`.", "--agent <name>`. " + PublishedAgentWords.PlaceholderSentence, StringComparison.Ordinal)
                    : PublishedResendWords.As(ruled.Replace("<slug>", "'a-one'", StringComparison.Ordinal), "a-one");
                if (!expected.Contains(placeholder ? "--agent <name>" : "--agent 'a-one'", StringComparison.Ordinal) && name != "resend refusal words") problems.Add($"{name}: the rule found no command in the published text");

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
    /// CWK9. The <c>unknown</c> line. <c>recheck</c> prints <c>--agent &lt;name&gt;</c> and the sentence that <c>&lt;name&gt;</c> is the profile that kept the act, on a host of one, two and no
    /// profile alike, with <c>--agent</c> and without (plan ruling 38: <c>recheck</c> reads no profile, so a profile named to pick a Forum is not the profile that kept an act; CWK58 holds the
    /// equality); exit 5 and exactly one request in each, and no profile read to fill it. Predicted red: <c>expected "--agent &lt;name&gt;", found "--agent 'a-one'"</c> for the flagged run.
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
            if (!flagged.Success || flagged.Groups[1].Value != " --agent <name>") problems.Add($"{label}, --agent {names[0]}: expected \"--agent <name>\", found \"{(flagged.Success ? flagged.Groups[1].Value.Trim() : "none")}\"");
            if (!named.Text.Contains("is the profile that kept the act", StringComparison.Ordinal)) problems.Add($"{label}, --agent {names[0]}: the sentence that <name> is the profile that kept the act is missing");
        }

        Red.None(problems, "--agent <name> with the sentence on every host, with --agent and without; exit 5, one request");
    }

    // ----- CWK58 -----

    private static string WithoutInstants(string text) => Regex.Replace(text, @"answered at \S+Z", "answered at <instant>", RegexOptions.CultureInvariant);

    /// <summary>
    /// CWK58. <c>recheck</c> names no profile. On the two-profile host (<c>a-one</c> kept the act), <c>recheck &lt;digest&gt; --agent B-two</c>, the same with <c>--agent a-one</c> and with no flag print the
    /// same <c>unknown</c> line and the same closing line (apart from the reader's instant), each ending <c>--agent &lt;name&gt;</c> and the placeholder's sentence; the printed command run with
    /// <c>&lt;name&gt;</c> replaced by <c>a-one</c> resends, and with <c>B-two</c> refuses <c>no act of that digest</c>, as the sentence says (the one pair of runs that carries information about what
    /// <c>&lt;name&gt;</c> is). Predicted red: <c>--agent 'B-two'</c> in the <c>unknown</c> line.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK58_RecheckNamesNoProfileWhateverAgentItWasGiven()
    {
        var problems = new List<string>();
        using var host = new AgentHost(PrintedScenarios.Other, PrintedScenarios.Printer);
        var (digest, _) = host.PlantAct(PrintedScenarios.Printer, attempts: 0, lastAnswer: null);
        host.OnOther = request => request.RequestUri!.AbsolutePath.StartsWith("/v1/posts/batch", StringComparison.Ordinal)
            ? ScriptedForum.Json(200, "{\"items\":[" + StubLog.BatchItem(digest, "unknown") + "]}")
            : ScriptedForum.Problem(404, "curia/posts/not-found", "No such post");

        var forum = AgentHost.ForumAddress.ToString();
        var runs = new[]
        {
            ("--agent B-two", await host.RunAsync("recheck", digest, "--agent", PrintedScenarios.Other, "--forum", forum)),
            ("--agent a-one", await host.RunAsync("recheck", digest, "--agent", PrintedScenarios.Printer, "--forum", forum)),
            ("no flag", await host.RunAsync("recheck", digest, "--forum", forum)),
        };
        var reference = WithoutInstants(runs[2].Item2.Text);
        foreach (var (label, run) in runs)
        {
            if (run.Exit != 5) problems.Add($"{label}: exit {run.Exit}, expected 5");
            if (WithoutInstants(run.Text) != reference) problems.Add($"{label}: the output differs from the one printed with no flag: \"{Shorten(WithoutInstants(run.Text))}\"");
            var commands = Regex.Matches(run.Text, @"`(curia resend " + Regex.Escape(digest) + @"[^`]*)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).Distinct().ToArray();
            if (commands.Length != 1 || !commands[0].EndsWith(" --agent <name>", StringComparison.Ordinal)) problems.Add($"{label}: the printed command is not `... --agent <name>` ({string.Join(" | ", commands)})");
            if (!run.Text.Contains(PublishedAgentWords.PlaceholderSentence, StringComparison.Ordinal)) problems.Add($"{label}: the placeholder's sentence is missing");
        }

        var printed = Regex.Match(runs[2].Item2.Text, @"`(curia resend " + Regex.Escape(digest) + @" --agent <name>)`", RegexOptions.CultureInvariant);
        if (!printed.Success) problems.Add("no printed command to run");
        else
        {
            var scenarios = new PrintedScenarios(host) { ActDigest = digest };
            host.OnOther = null;
            var resent = await host.RunAsync([.. scenarios.Argv(printed.Groups[1].Value.Replace("<name>", PrintedScenarios.Printer, StringComparison.Ordinal))]);
            if (!resent.Requests.Any(r => r.Path == "/v1/posts") || resent.Text.Contains("no act of that digest", StringComparison.Ordinal)) problems.Add($"with a-one the printed command did not resend (exit {resent.Exit}: {Shorten(resent.Text)})");
            var wrong = await host.RunAsync([.. scenarios.Argv(printed.Groups[1].Value.Replace("<name>", PrintedScenarios.Other, StringComparison.Ordinal))]);
            if (wrong.Requests.Any(r => r.Path == "/v1/posts") || !wrong.Text.Contains("no act of that digest", StringComparison.Ordinal)) problems.Add($"with B-two the printed command did not refuse \"no act of that digest\" (exit {wrong.Exit}: {Shorten(wrong.Text)})");
        }

        Red.None(problems, "the same unknown and closing lines for B-two, a-one and no flag, each ending --agent <name> and the sentence; a-one resends and B-two refuses no act of that digest");
    }

    // ----- CWK59 -----

    /// <summary>
    /// CWK59. <c>whoami</c> and <c>enrol</c> print <c>curia budget</c> as a command that runs. The real output of <c>whoami</c> (as each of two profiles) and of <c>enrol</c> on the two-profile host: the
    /// command appears once, in a backtick span, with <c>--agent '&lt;the profile ran as&gt;'</c>; the other mentions say <c>that command</c>; the tier-reading sentence is stated once (D37); the printed command,
    /// run as printed, exits other than 1 and acts as that profile. Predicted red: <c>printed "curia budget" with no flag</c> (a printed identity command refuses on a host of several profiles).
    /// </summary>
    [Fact]
    public async Task R11_82_CWK59_WhoamiAndEnrolPrintTheBudgetCommandAsOneThatRuns()
    {
        var problems = new List<string>();
        using var host = new AgentHost(PrintedScenarios.Other, PrintedScenarios.Printer);
        host.OnOther = request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/agents"
            ? ScriptedForum.Json(201, "{\"agent_id\":\"" + AgentHost.AgentIdOf(PrintedScenarios.Enrolled) + "\",\"kid\":\"c-three-1\",\"enrolled_at\":\"2026-01-01T00:00:00.0000000+00:00\",\"owner_verified\":false}")
            : ScriptedForum.Problem(404, "curia/posts/not-found", "No such post");
        var scenarios = new PrintedScenarios(host);
        var outputs = new List<(string Label, string Profile, AgentRun Run)>
        {
            ("whoami as B-two", PrintedScenarios.Other, await host.RunAsync("whoami", "--agent", PrintedScenarios.Other)),
            ("whoami as a-one", PrintedScenarios.Printer, await host.RunAsync("whoami", "--agent", PrintedScenarios.Printer)),
        };
        outputs.Add(("enrol c-three", PrintedScenarios.Enrolled, await host.RunAsync("enrol", "--agent", PrintedScenarios.Enrolled, "--agent-id", AgentHost.AgentIdOf(PrintedScenarios.Enrolled), "--forum", AgentHost.ForumAddress.ToString())));
        host.AdoptEnrolled(PrintedScenarios.Enrolled);
        host.OnOther = null;

        foreach (var (label, profile, run) in outputs)
        {
            var text = run.Text;
            var spans = Regex.Matches(text, @"`(curia budget[^`]*)`", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
            if (spans.Length != 1 || spans[0] != $"curia budget --agent '{profile}'") problems.Add($"{label}: printed \"{string.Join(" | ", spans)}\" with no flag (expected exactly `curia budget --agent '{profile}'`)");
            var mentions = Regex.Count(text, @"curia budget", RegexOptions.CultureInvariant);
            if (mentions != 1) problems.Add($"{label}: \"curia budget\" is written {mentions} times, expected once (the others say \"that command\")");
            if (!text.Contains("that command", StringComparison.Ordinal)) problems.Add($"{label}: no mention says \"that command\"");
            var tier = Regex.Count(text, "states the tier as the Forum computes it now", RegexOptions.CultureInvariant);
            if (tier != 1) problems.Add($"{label}: the tier-reading sentence is stated {tier} times, expected once");
            if (run.Exit != 0) problems.Add($"{label}: exit {run.Exit}");

            if (spans.Length == 1)
            {
                using var copy = host.Clone();
                var ran = await copy.RunAsync([.. scenarios.Argv(spans[0])]);
                if (ran.Exit == 1 || !ran.ActedAs.Contains(profile) || ran.ActedAs.Any(a => a != profile))
                    problems.Add($"{label}: the printed command, run as printed, exit {ran.Exit}, acted as [{string.Join(", ", ran.ActedAs)}] (expected only {profile}): {Shorten(ran.Text)}");
            }
        }

        Red.None(problems, "curia budget written once in a backtick span with the profile ran as, the other mentions say that command, the tier sentence once, and the command acts as that profile");
    }

    // ----- CWK60 -----

    /// <summary>
    /// CWK60. The slug rule, run. A refusal that prints <c>curia acts</c> for the profile it ran as (<c>resend</c> of a digest nothing kept), on scratch homes with the slugs <c>a b</c>, <c>"x</c>,
    /// <c>a"b</c> and <c>x--y</c> (shell words, written: <c>--agent 'a b'</c>, run as printed, resolves that profile) and <c>a`b</c> (withheld in the published form, which says <c>the name of the
    /// profile you ran as</c> and not <c>named above</c>, and does not hold the slug). Predicted red: <c>named above</c> in the backtick case; <c>a b</c> and <c>"x</c> withheld.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK60_ASlugIsWrittenWhenAShellWordAndWithheldWhenItHoldsABacktick()
    {
        var problems = new List<string>();
        var withheldTail = PublishedAgentWords.WithheldForm.Split("<command>", 2)[1];
        Assert.True(withheldTail.Length >= 20, "the published withheld form is under 20 characters after <command>");
        foreach (var slug in new[] { "a b", "\"x", "a\"b", "x--y", "a`b" })
        {
            using var host = new AgentHost(slug);
            var scenarios = new PrintedScenarios(host);
            var run = await host.RunAsync("resend", IdentityVerbs.SomeDigest, "--agent", slug);
            var written = $"curia acts --agent '{slug}'";
            if (slug.Contains('`', StringComparison.Ordinal))
            {
                if (!run.Text.Contains("curia acts" + withheldTail, StringComparison.Ordinal)) problems.Add($"[{slug}] the withheld form was not printed (\"{Shorten(run.Text)}\")");
                if (run.Text.Contains("named above", StringComparison.Ordinal)) problems.Add($"[{slug}] the withheld form still says \"named above\"");
                if (run.Text.Contains(slug, StringComparison.Ordinal)) problems.Add($"[{slug}] the slug was printed");
                continue;
            }

            // The refusal's detail is a display literal (a JSON string), which escapes a quotation mark in the command; the command run is the unescaped one.
            if (!run.Text.Contains($"`{written.Replace("\"", "\\\"", StringComparison.Ordinal)}`", StringComparison.Ordinal)) problems.Add($"[{slug}] not written as `{written}` (\"{Shorten(run.Text)}\")");
            if (run.Text.Contains(withheldTail, StringComparison.Ordinal)) problems.Add($"[{slug}] withheld, though a shell reads it as one word");
            var ran = await host.RunAsync([.. scenarios.Argv(written)]);
            if (ran.Exit != 0) problems.Add($"[{slug}] the printed command, run as printed, exit {ran.Exit}: {Shorten(ran.Text)}");
        }

        Red.None(problems, "a b, \"x, a\"b and x--y written as shell words and resolved when run as printed; a`b withheld in the published words naming no profile above");
    }

    // ----- CWK61 -----

    /// <summary>
    /// CWK61. The five sentences plan ruling 39 publishes are printed as published: the withheld form, the placeholder's sentence, the <c>unknown</c> line's <c>curia-mcp</c> sentence and the two kept-act
    /// reasons, each read from the errata (twelve paragraphs, two reasons, asserted) and found in the real output of its state. A sentence changed in the code or in the errata is red naming it.
    /// Predicted red before the errata edit: <c>expected 12 word paragraphs, found 8</c>.
    /// </summary>
    [Fact]
    public async Task R11_82_CWK61_TheFiveSentencesArePrintedAsPublished()
    {
        Assert.Equal(12, PublishedAgentWords.AssertParagraphs());
        Assert.Equal(2, PublishedAgentWords.KeptActReasons.Length);
        var problems = new List<string>();

        // The placeholder's sentence and the curia-mcp sentence: the unknown line, from recheck.
        using (var host = new AgentHost(PrintedScenarios.Printer))
        {
            host.OnOther = request => request.RequestUri!.AbsolutePath.StartsWith("/v1/posts/batch", StringComparison.Ordinal)
                ? ScriptedForum.Json(200, "{\"items\":[" + StubLog.BatchItem(UnknownAsked, "unknown") + "]}")
                : ScriptedForum.Problem(404, "curia/posts/not-found", "No such post");
            var run = await host.RunAsync("recheck", UnknownAsked, "--forum", AgentHost.ForumAddress.ToString());
            if (!run.Text.Contains(PublishedAgentWords.PlaceholderSentence, StringComparison.Ordinal)) problems.Add($"the placeholder's sentence \"{PublishedAgentWords.PlaceholderSentence}\" is not printed");
            if (!run.Text.Contains(PublishedAgentWords.McpUnknownSentence, StringComparison.Ordinal)) problems.Add($"the curia-mcp sentence \"{PublishedAgentWords.McpUnknownSentence}\" is not printed");
        }

        // The withheld form: a slug holding a backtick.
        using (var host = new AgentHost("a`b"))
        {
            var run = await host.RunAsync("resend", IdentityVerbs.SomeDigest, "--agent", "a`b");
            var tail = PublishedAgentWords.WithheldForm.Split("<command>", 2)[1];
            if (!run.Text.Contains("curia acts" + tail, StringComparison.Ordinal)) problems.Add($"the withheld form \"{tail}\" is not printed");
        }

        // The two kept-act reasons: a header that names another kind, then another board, than its bytes hold.
        var reasons = PublishedAgentWords.KeptActReasons;
        using (var host = new AgentHost(PrintedScenarios.Printer))
        {
            var (kindDigest, _) = host.PlantAct(PrintedScenarios.Printer, headerKind: "answer");
            var kind = await host.RunAsync("resend", kindDigest, "--agent", PrintedScenarios.Printer);
            if (!kind.Text.Contains(reasons[0], StringComparison.Ordinal)) problems.Add($"the kept-act reason \"{reasons[0]}\" is not printed");
            var (boardDigest, _) = host.PlantAct(PrintedScenarios.Printer, body: "Another body.", headerBoard: "meta");
            var board = await host.RunAsync("resend", boardDigest, "--agent", PrintedScenarios.Printer);
            if (!board.Text.Contains(reasons[1], StringComparison.Ordinal)) problems.Add($"the kept-act reason \"{reasons[1]}\" is not printed");
        }

        Red.None(problems, "the withheld form, the placeholder's sentence, the curia-mcp sentence and both kept-act reasons are printed as the errata publishes them");
    }
}
