using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Serving;
using Curia.Domain.Tests.Authorization;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// Errata G49 (R11.65) at the adapter: SEB43 (<c>curia_budget</c>), SEB44 (the receipts print the standing), SEB45 and SEB48 (a refusal prints the Forum's
/// instant and no sentence says a budget "resets" without the readout), SEB46 (the pointer), SEB47 (the version), SEB51 (the tool is not a poll target) and
/// SEB56 (the adapter's prose limits are the published ones). The words are R11.65's, parsed from the errata; the limits are Table 11's, R7.26's and R7.22's.
/// <c>curia_budget</c> does not exist on the seam tree, so SEB43, SEB46 and SEB51 are red by assertion; SEB56 is a control and is green.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetToolTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-g49-").FullName;

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Tiers = ["T0", "T1", "T2"];

    private ForumTools ToolsOver(StubLog log, bool identity = true)
    {
        ForumWriter? writer = null;
        if (identity)
        {
            Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
            _owned.Add(agent!);
            writer = new ForumWriter(agent!, new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System), TimeProvider.System);
        }

        return new ForumTools(log.Client(), MarkingMode.None, new HeadStore(_home), writer);
    }

    private static string Text(CallToolResult result) => string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static List<(string Name, string Description, McpServerTool Tool)> Registered(ForumTools tools) =>
        [.. ToolCatalogue.Build(tools).Select(t => (t.ProtocolTool.Name, t.ProtocolTool.Description ?? string.Empty, t))];

    private static ResourceKind? BudgetKind() => Enum.TryParse<ResourceKind>("Budget", out var kind) ? kind : null;

    /// <summary>
    /// SEB43. <c>curia_budget</c>: registered if and only if an identity is configured; read-only and idempotent; no parameters; its description carries
    /// <c>TierSpan.For(Budget, Read)</c> and the snapshot sentence; any argument is refused by name with nothing sent (R11.37); its text equals the
    /// command's lines for the same stub, after exactly one request.
    /// </summary>
    [Fact]
    public async Task R11_65_SEB43_TheBudgetTool()
    {
        using var log = new StubLog();
        var withIdentity = Registered(ToolsOver(log)).SingleOrDefault(t => t.Name == "curia_budget");
        Assert.True(withIdentity.Tool is not null, "Expected: curia_budget registered with an identity Actual: absent");
        Assert.DoesNotContain(Registered(ToolsOver(log, identity: false)), t => t.Name == "curia_budget");

        var protocol = withIdentity.Tool.ProtocolTool;
        Assert.True(protocol.Annotations?.ReadOnlyHint == true && protocol.Annotations?.IdempotentHint == true, "Expected: read-only and idempotent");
        Assert.True(!protocol.InputSchema.TryGetProperty("properties", out var properties) || !properties.EnumerateObject().Any(), "Expected: no parameters");

        var kind = BudgetKind();
        Assert.True(kind is not null && withIdentity.Description.EndsWith(TierSpan.For(kind.Value, ActionKind.Read), StringComparison.Ordinal), "Expected: the description ends with the composed tier span of budget/read");
        Assert.Contains(PublishedBudgetReadout.Phrase("snapshot and not a reservation"), withIdentity.Description, StringComparison.Ordinal);

        await using var session = await McpProtocolSession.StartAsync(ToolsOver(log), "https://agents.example/alice");
        log.Requests.Clear();
        var refused = await session.CallAsync("curia_budget", new Dictionary<string, object?> { ["agent"] = "someone-else" });
        Assert.True(refused.IsError && refused.Text.Contains("agent", StringComparison.Ordinal) && !log.Requests.Any(r => r == "GET /v1/budget"), $"Expected: an argument refused by name with nothing sent Actual: {refused.Text}");

        var answered = await session.CallAsync("curia_budget", []);
        Assert.False(answered.IsError, answered.Text);
        Assert.Equal(1, log.Requests.Count(r => r == "GET /v1/budget"));
    }

    private static readonly (string Tool, string Budget)[] Senders =
    [
        ("curia_ask", "posting"), ("curia_comment", "posting"), ("curia_answer", "posting"), ("curia_revise", "posting"), ("curia_resolve", "acceptance"), ("curia_flag", "flag"),
    ];

    private async Task<string> CallAsync(StubLog log, string tool)
    {
        var tools = ToolsOver(log);
        try
        {
            var result = tool switch
            {
                "curia_ask" => await tools.AskAsync("b", "A title?", "A body.", null, null, Ct),
                "curia_answer" => await tools.AnswerAsync(StubLog.PostId, "An answer.", null, Ct),
                "curia_comment" => await tools.CommentAsync(StubLog.PostId, "A comment.", null, Ct),
                "curia_revise" => await tools.ReviseAsync(StubLog.PostId, "A corrected body.", null, null, null, Ct),
                "curia_resolve" => await tools.ResolveAsync(StubLog.AnswerPostId, Ct),
                "curia_flag" => await tools.FlagAsync(StubLog.PostId, "spam", "this reads as spam", Ct),
                _ => throw new InvalidOperationException($"no call for {tool}"),
            };
            return (result.IsError == true ? "error: " : "ok: ") + Text(result);
        }
        catch (ModelContextProtocol.McpException refused)
        {
            return "error: " + refused.Message;
        }
    }

    /// <summary>
    /// SEB44, the tool half. The receipts print the standing, data-driven over the tools that send: a <c>budget</c> member prints a line stating the standing
    /// after the act (the member as sent); none prints <c>budget NOT STATED</c>; the replay of the standing acceptance prints the budget as sent (unchanged).
    /// </summary>
    [Fact]
    public async Task R11_65_SEB44_TheReceiptsPrintTheStandingAfterTheAct()
    {
        var problems = new List<string>();
        foreach (var (tool, budget) in Senders)
        {
            var text = await ReceiptAsync(tool, $$"""{"name":"{{budget}}","limit":9,"used":7,"remaining":2}""", false);
            var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("budget", StringComparison.Ordinal));
            if (line is null || !Regex.IsMatch(line, $@"\b{budget}\b.*\b9\b.*\b7\b.*\b2\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))) problems.Add($"{tool}, a budget member: Expected: a budget line stating {budget} 9 7 2 Actual: \"{text}\"");

            var none = await ReceiptAsync(tool, null, false);
            if (!Regex.IsMatch(none, @"(?m)^budget\s+NOT STATED", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))) problems.Add($"{tool}, no budget member: Expected: budget NOT STATED Actual: \"{none}\"");
        }

        var replayed = await ReceiptAsync("curia_resolve", """{"name":"acceptance","limit":10,"used":4,"remaining":6}""", true);
        if (!Regex.IsMatch(replayed, @"(?m)^budget\s.*\b4\b.*\b6\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2))) problems.Add($"curia_resolve, a replay: Expected: the standing as sent (used 4, remaining 6) Actual: \"{replayed}\"");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private async Task<string> ReceiptAsync(string tool, string? budgetJson, bool alreadyAccepted)
    {
        using var log = new StubLog();
        log.ReceiptBudgetJson = budgetJson;
        if (alreadyAccepted) log.AcceptanceAlreadyAccepted = true;
        return await CallAsync(log, tool);
    }

    private static string[] BudgetReasons() =>
        [.. AccessPolicy.DenialReasons.Where(r => r.StartsWith("table-11/", StringComparison.Ordinal) && r.EndsWith("-budget-exhausted", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private static (string Tool, string Budget) ToolOf(string reason) =>
        reason.Contains("rate", StringComparison.Ordinal) ? ("curia_ask", "posting") : reason.Contains("accept", StringComparison.Ordinal) ? ("curia_resolve", "acceptance") : ("curia_flag", "flag");

    private async Task<string> RefusedAsync(string tool, string reason, string suffix)
    {
        using var log = new StubLog();
        Refuse(log, reason, suffix);
        return await CallAsync(log, tool);
    }

    private static void Refuse(StubLog log, string reason, string suffix)
    {
        log.DenialDetailSuffix = suffix;
        if (reason.Contains("rate", StringComparison.Ordinal)) log.DeniesSubmissionsForReason = reason;
        else if (reason.Contains("accept", StringComparison.Ordinal)) log.RefusesAcceptancesAs = reason;
        else log.RefusesFlagsAs = reason;
    }

    /// <summary>
    /// SEB45 and SEB48's behavioural leg, the tool half: for each of the three budget reasons (the policy's enumeration) the tool that spends it relays the
    /// Forum's instant as given, names <c>curia_budget</c> in the remedy and does not say the budget "resets"; a detail without an instant says
    /// <c>next slot NOT STATED</c>.
    /// </summary>
    [Fact]
    public async Task R11_65_SEB45_ARefusalPrintsTheForumsInstantAndNamesTheReadout()
    {
        var instant = "2026-10-31T09:30:00.1234560+00:00";
        var token = PublishedBudgetReadout.RefusalToken();
        var problems = new List<string>();

        foreach (var reason in BudgetReasons())
        {
            var (tool, _) = ToolOf(reason);
            var text = await RefusedAsync(tool, reason, $" {token}{instant}");
            var remedy = text.Replace($"{reason} tier=T0 {token}{instant}", string.Empty, StringComparison.Ordinal);
            if (!remedy.Contains(instant, StringComparison.Ordinal)) problems.Add($"{tool}/{reason}: Expected: the instant {instant} in the remedy Actual: \"{text}\"");
            if (!remedy.Contains("curia_budget", StringComparison.Ordinal)) problems.Add($"{tool}/{reason}: Expected: the remedy names curia_budget Actual: \"{text}\"");
            if (remedy.Contains("resets", StringComparison.OrdinalIgnoreCase)) problems.Add($"{tool}/{reason}: Expected: no \"resets\" Actual: \"{text}\"");

            var bare = await RefusedAsync(tool, reason, string.Empty);
            if (!bare.Contains(PublishedBudgetReadout.Span("next slot NOT STATED"), StringComparison.Ordinal)) problems.Add($"{tool}/{reason}: Expected: next slot NOT STATED for a detail without an instant Actual: \"{bare}\"");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// SEB46. The pointer: every tool whose description carries a budget sentence (the catalogue's enumeration, with R11.64's five and <c>curia_flag</c>) says
    /// where the budget can be read; a tool added with a budget sentence and no pointer is red. (<c>curia help</c>'s is SEB42's.)
    /// </summary>
    [Fact]
    public void R11_65_SEB46_EveryBudgetedToolDescriptionPointsAtTheReadout()
    {
        using var log = new StubLog();
        var catalogue = Registered(ToolsOver(log));
        var named = PublishedBudgetGate.ToolNames().Append("curia_flag").ToHashSet(StringComparer.Ordinal);
        var budgeted = catalogue.Where(t => t.Name != "curia_budget" && (named.Contains(t.Name) || t.Description.Contains("budget", StringComparison.OrdinalIgnoreCase))).ToArray();
        Assert.NotEmpty(budgeted);

        var without = budgeted.Where(t => !t.Description.Contains("curia_budget", StringComparison.Ordinal)).Select(t => t.Name).ToArray();
        Assert.True(without.Length == 0, $"Expected: every budgeted tool names curia_budget Actual: {string.Join(", ", without)} do not");
    }

    /// <summary>
    /// SEB47. The version: <c>ToolCatalogue.Version</c> equals the version R11.65 names, parsed from the errata. (CHN51 and the three literal pins are
    /// re-pointed to the same parse in the commit that bumps the version: one parser, not two.)
    /// </summary>
    [Fact]
    public void R11_65_SEB47_TheVersionIsTheErratas()
    {
        Assert.True(PublishedBudgetReadout.Version() == ToolCatalogue.Version, $"Expected: {PublishedBudgetReadout.Version()} Actual: {ToolCatalogue.Version}");
    }

    /// <summary>
    /// SEB47's literal twin (the D90 review): every other version fact reads the same errata sentence, so a change made to both the code and that sentence
    /// would pass them all. This one is written by hand and moves only when someone means the version to move.
    /// </summary>
    [Fact]
    public void R11_65_SEB47_TheVersionIs090() => Assert.Equal("0.9.0", ToolCatalogue.Version);

    /// <summary>
    /// SEB51, the tool half. The tool's description says that every receipt already states the standing after the act, that the readout is read before the
    /// first act and that it is a snapshot: R11.65's words.
    /// </summary>
    [Fact]
    public void R11_65_SEB51_TheToolSaysItIsNotAPollTarget()
    {
        using var log = new StubLog();
        var tool = Registered(ToolsOver(log)).SingleOrDefault(t => t.Name == "curia_budget");
        Assert.True(tool.Tool is not null, "Expected: curia_budget registered Actual: absent");

        var missing = new[]
            {
                PublishedBudgetReadout.Phrase("every receipt already states the standing after the act"),
                PublishedBudgetReadout.Phrase("snapshot and not a reservation"),
                PublishedBudgetReadout.Phrase("before the first act"),
            }
            .Where(p => !tool.Description.Contains(p, StringComparison.Ordinal))
            .ToArray();
        Assert.True(missing.Length == 0, $"Expected: the description to say {string.Join(" | ", missing)} Actual: \"{tool.Description}\"");
    }

    private static string[] Lacks(string text, int[] limits) => PublishedBudgetReadout.LimitsMissingInContext(text, limits);

    /// <summary>
    /// SEB56, the adapter half (a control: green on the seam tree; red under FSEB65). The words each budget refusal relays carry the limits Table 11, R7.26
    /// and R7.22 publish, and <c>curia_resolve</c>'s description carries the acceptance limits.
    /// </summary>
    [Fact]
    public async Task R11_65_SEB56_TheAdaptersProseLimitsAreThePublishedOnes()
    {
        var limits = PublishedBudgetReadout.Limits([.. Tiers.Select(t => PublishedTable11.Rows[t].PostsPerDay!.Value)]);
        var problems = new List<string>();

        foreach (var reason in BudgetReasons())
        {
            var (tool, budget) = ToolOf(reason);
            var text = await RefusedAsync(tool, reason, string.Empty);
            if (Lacks(text, limits[budget]) is { Length: > 0 } missing) problems.Add($"{tool} ({budget}): Expected: limits {string.Join('/', limits[budget])} Missing: {string.Join(',', missing)} in \"{text}\"");
        }

        using var stub = new StubLog();
        var resolve = Registered(ToolsOver(stub)).Single(t => t.Name == "curia_resolve").Description;
        if (Lacks(resolve, limits["acceptance"]) is { Length: > 0 } lacking) problems.Add($"curia_resolve's description: Expected: limits {string.Join('/', limits["acceptance"])} Missing: {string.Join(',', lacking)}");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>The reasoned rows of SEB48's source leg: a line with the word that is not an agent-facing sentence about a budget, and why. Each is a file and the text of its line.</summary>
    private static readonly (string File, string Line, string Reason)[] NotStale =
    [
        ("src/Curia.Domain.Primitives/UlidGenerator.cs", "resets the randomness", "a new millisecond resets the generator's randomness: nothing to do with a budget"),
        ("src/Curia.Client/ForumClient.cs", "no waiting resets", "a comment on how quarantine is classified, which no waiting changes: true of a quarantine and says nothing to an agent about a budget"),
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// SEB48, the source leg (the behavioural leg is SEB45's and the CLI's). No line of <c>src/</c> says a budget "resets" (the word the trailing window does
    /// not have, and the one that told an agent to wait without telling it when) outside the reasoned rows: the Task 0 list was
    /// <c>ForumResult.cs</c>, <c>WriteTools.cs</c>, <c>ToolText.cs</c> and <c>Help.cs</c>; <c>Program.cs:165</c> (about owner attestation) and
    /// <c>README.md:305</c> are not stale and say nothing the scan finds.
    /// </summary>
    [Fact]
    public void R11_65_SEB48_NoSentenceSaysABudgetResets()
    {
        var root = RepoRoot();
        var stale = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetRelativePath(root, f).Replace('\\', '/'), Number: i + 1, Line: line)))
            .Where(l => Regex.IsMatch(l.Line, @"\bresets\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)))
            .Where(l => !NotStale.Any(n => n.File == l.File && l.Line.Contains(n.Line, StringComparison.Ordinal) && n.Reason.Length > 0))
            .Select(l => $"{l.File}:{l.Number}")
            .ToArray();

        Assert.True(stale.Length == 0, $"Expected: no sentence saying a budget resets Actual: {string.Join(", ", stale)}");
    }
}
