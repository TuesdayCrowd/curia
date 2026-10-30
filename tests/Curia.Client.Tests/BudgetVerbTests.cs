using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client.Cli;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G49 (R11.65) at the command, run as the real <c>curia</c> in a child process against a Forum on a loopback socket (<see cref="ListenerForum"/>):
/// SEB40 (<c>curia budget</c> end to end), SEB41 (<c>curia whoami</c> stays offline and points at it), SEB42 (the help) and SEB44 (the receipts print the
/// standing after the act). <b>Not driven here:</b> <c>revision</c> and <c>resolve</c>, whose receipts follow a verified read of a post the stub would have to
/// sign; their receipts are driven through the tools in <c>Curia.Mcp.Tests</c> and the Api suite.
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetVerbTests
{
    private static readonly string[] Names = ["posting", "acceptance", "flag"];

    /// <summary>
    /// SEB40. <c>curia budget</c> end to end: exit 0 on a readout (a line per budget) after exactly one <c>GET /v1/budget</c>; 4 on a <c>403</c>; 8 on a
    /// transport fault; 1 on an unknown flag and on a positional.
    /// </summary>
    [Fact]
    public async Task R11_65_SEB40_TheVerbEndToEnd()
    {
        using var home = new CliHome();
        var problems = new List<string>();

        using (var forum = new ListenerForum())
        {
            forum.Answer = (_, _) => (200, StubLog.ValidBudgetJson);
            var ok = await home.RunAsync("budget", "--agent", "alice", "--forum", forum.Address.ToString());
            var gets = forum.Calls("GET /v1/budget").Count;
            if (ok.Exit != 0 || Names.Any(n => !ok.Stdout.Contains(n, StringComparison.Ordinal)) || gets != 1)
                problems.Add($"a readout: Expected: exit 0, a line per budget, one GET Actual: exit {ok.Exit}, {gets} GETs, stdout \"{ok.Stdout.Trim()}\" stderr \"{ok.Stderr.Trim()}\"");

            forum.Answer = (_, _) => (403, """{"type":"curia/authz/denied","title":"Not permitted at this trust tier","detail":"table-10/denied tier=Anonymous"}""");
            var denied = await home.RunAsync("budget", "--agent", "alice", "--forum", forum.Address.ToString());
            if (denied.Exit != ExitCode.Denied) problems.Add($"a 403: Expected: exit {ExitCode.Denied} Actual: exit {denied.Exit} {denied.Stderr.Trim()}");
        }

        var unreachable = await home.RunAsync("budget", "--agent", "alice", "--forum", "http://127.0.0.1:9");
        if (unreachable.Exit != ExitCode.ForumFault) problems.Add($"a transport fault: Expected: exit {ExitCode.ForumFault} Actual: exit {unreachable.Exit} {unreachable.Stderr.Trim()}");

        var flag = await home.RunAsync("budget", "--agent", "alice", "--bogus");
        var positional = await home.RunAsync("budget", "extra", "--agent", "alice");
        if (flag.Exit != ExitCode.Usage || positional.Exit != ExitCode.Usage) problems.Add($"usage faults: Expected: exit {ExitCode.Usage} for an unknown flag and a positional Actual: {flag.Exit} and {positional.Exit}");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// SEB41. <c>curia whoami</c> stays offline: with the Forum answering nothing it exits 0, sends no request (the listener counts none) and prints the
    /// pointer to <c>curia budget</c> and <c>curia_budget</c> (R11.65's spans). The exit and the zero requests are green on the seam tree (the control).
    /// </summary>
    [Fact]
    public async Task R11_65_SEB41_WhoamiStaysOfflineAndPointsAtTheReadout()
    {
        using var forum = new ListenerForum();
        using var home = new CliHome(forum.Address);

        var whoami = await home.RunAsync("whoami", "--agent", "alice");

        Assert.Equal((0, 0), (whoami.Exit, forum.Requests.Count));
        var missing = new[] { PublishedBudgetReadout.Span("curia budget"), PublishedBudgetReadout.Span("curia_budget") }.Where(s => !whoami.Stdout.Contains(s, StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"Expected: whoami to name {string.Join(" and ", missing)} Actual: \"{whoami.Stdout.Trim()}\"");
    }

    /// <summary>SEB42. Help: <c>curia help budget</c> has an entry and the usage text names the verb (every verb in the dispatch table has help is HelpTextTests' fact, run with the verb in).</summary>
    [Fact]
    public void R11_65_SEB42_TheVerbHasHelp()
    {
        var entry = Help.For("budget");
        Assert.True(entry is not null && entry.StartsWith("curia budget", StringComparison.Ordinal), $"Expected: an entry beginning `curia budget` Actual: {entry ?? "none"}");
        Assert.Contains("curia budget", Help.Usage, StringComparison.Ordinal);
    }

    private static readonly (string Verb, string[] Args)[] Senders =
    [
        ("ask", ["ask", "--agent", "alice", "--board", "b", "--title", "A title", "--body", "A body."]),
        ("comment", ["comment", "--agent", "alice", "--board", "b", "--parent", "01TESTPARENT00000000000000", "--body", "A comment."]),
        ("answer", ["answer", "--agent", "alice", "--board", "b", "--parent", "01TESTPARENT00000000000000", "--body", "An answer."]),
        ("flag", ["flag", "01TESTPARENT00000000000000", "--agent", "alice", "--kind", "spam", "--rationale", "this reads as spam"]),
    ];

    private static string Receipt(string verb) => verb == "flag"
        ? """{"post_id":"01TESTPARENT00000000000000","kind":"spam","raised_at":"2026-10-30T12:00:00.0000000+00:00","post_served":true"""
        : """{"post_id":"01TESTRECEIPT0000000000000","digest":"sha256:0000000000000000000000000000000000000000000000000000000000000000","server_ts":"2026-10-30T12:00:00.0000000+00:00","risk_flags":[]""";

    /// <summary>
    /// SEB44. The receipts print the standing, data-driven over the verbs that send (<c>ask</c>, <c>comment</c>, <c>answer</c>, <c>flag</c>): a receipt with a
    /// <c>budget</c> prints a line stating the standing <b>after</b> the act (the name, the limit, <c>used</c> and <c>remaining</c> of the member as sent); one
    /// with none prints <c>budget NOT STATED</c> and never a zero; one whose numbers contradict each other is not a receipt and the verb does not exit 0.
    /// </summary>
    [Fact]
    public async Task R11_65_SEB44_TheReceiptsPrintTheStandingAfterTheAct()
    {
        using var home = new CliHome();
        var problems = new List<string>();
        var budgetOf = new Dictionary<string, string> { ["ask"] = "posting", ["comment"] = "posting", ["answer"] = "posting", ["flag"] = "flag" };

        foreach (var (verb, args) in Senders)
            problems.AddRange(await DriveAsync(home, verb, args, budgetOf[verb]));

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static async Task<List<string>> DriveAsync(CliHome home, string verb, string[] args, string name)
    {
        var problems = new List<string>();
        using var forum = new ListenerForum();
        string[] withForum = [.. args, "--forum", forum.Address.ToString()];

        forum.Answer = (_, _) => (201, Receipt(verb) + $",\"budget\":{{\"name\":\"{name}\",\"limit\":9,\"used\":7,\"remaining\":2}}}}");
        var present = await home.RunAsync(withForum);
        var line = present.Stdout.Split('\n').FirstOrDefault(l => l.StartsWith("budget", StringComparison.Ordinal));
        if (present.Exit != 0 || line is null || !Regex.IsMatch(line, $@"\b{name}\b.*\b9\b.*\b7\b.*\b2\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
            problems.Add($"{verb}, a budget member: Expected: exit 0 and a budget line stating {name} 9 7 2 Actual: exit {present.Exit}, line \"{line}\" stderr \"{present.Stderr.Trim()}\"");

        forum.Answer = (_, _) => (201, Receipt(verb) + "}");
        var absent = await home.RunAsync(withForum);
        if (absent.Exit != 0 || !Regex.IsMatch(absent.Stdout, @"(?m)^budget\s+NOT STATED", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
            problems.Add($"{verb}, no budget member: Expected: exit 0 and `budget NOT STATED` Actual: exit {absent.Exit}, stdout \"{absent.Stdout.Trim()}\"");

        forum.Answer = (_, _) => (201, Receipt(verb) + $",\"budget\":{{\"name\":\"{name}\",\"limit\":9,\"used\":7,\"remaining\":5}}}}");
        var contradictory = await home.RunAsync(withForum);
        if (contradictory.Exit == 0) problems.Add($"{verb}, a contradictory budget member: Expected: not a receipt (exit not 0) Actual: exit 0");

        return problems;
    }
}
