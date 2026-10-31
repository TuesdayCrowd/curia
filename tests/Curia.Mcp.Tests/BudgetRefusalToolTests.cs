using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Client;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.64 (errata G48) at the tools: the five tools R11.64 names relay the agent write gate's two refusals worded as <c>curia</c> words them (BRB43), their
/// descriptions state the wait, the queue, the order and the batch rule (BRB44), and none retries a refusal by itself (BRB45). The tool list is R11.64's own
/// (<see cref="PublishedBudgetGate.ToolNames"/>) and the catalogue's tools whose descriptions carry a budget sentence must include them all, so a tool added
/// with a budget sentence and without the bound is red. The clauses are R11.64's, each checked to be in the errata's paragraph.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetRefusalToolTests : IDisposable
{
    private readonly List<IDisposable> _owned = [];
    private readonly List<string> _homes = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        foreach (var home in _homes) Directory.Delete(home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    public static TheoryData<string> RelayedTools()
    {
        var data = new TheoryData<string>();
        foreach (var name in PublishedBudgetGate.ToolNames()) data.Add(name);
        return data;
    }

    private static string Text(CallToolResult result) =>
        string.Join('\n', result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    private static string Missing(string text, params string[] phrases) =>
        string.Join("; ", phrases.Where(p => !text.Contains(p, StringComparison.OrdinalIgnoreCase)).Select(p => $"missing \"{p}\""));

    private ForumTools AliceTools(StubLog log)
    {
        Assert.True(log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        var home = Directory.CreateTempSubdirectory("curia-mcp-g48-").FullName;
        _homes.Add(home);
        _owned.Add(agent!);
        var session = new ForumSession(log.Client(), agent!, log.Store, TimeProvider.System);
        return new ForumTools(log.Client(), MarkingMode.Datamark, new HeadStore(home), new ForumWriter(agent!, session, TimeProvider.System));
    }

    /// <summary>What the model reads when it calls <paramref name="tool"/> against a Forum that refuses the write as <paramref name="refusal"/>.</summary>
    private static async Task<string> CallAsync(StubLog log, string tool, ForumTools tools)
    {
        ArgumentNullException.ThrowIfNull(log);
        try
        {
            var result = tool switch
            {
                "curia_ask" => await tools.AskAsync("b", "A title?", "A body.", null, null, Ct),
                "curia_answer" => await tools.AnswerAsync(StubLog.PostId, "An answer.", null, Ct),
                "curia_comment" => await tools.CommentAsync(StubLog.PostId, "A comment.", null, Ct),
                "curia_revise" => await tools.ReviseAsync(StubLog.PostId, "A corrected body.", null, null, null, Ct),
                "curia_resolve" => await tools.ResolveAsync(StubLog.AnswerPostId, Ct),
                _ => throw new InvalidOperationException($"R11.64 names a tool this fact does not know how to call: {tool}"),
            };
            return (result.IsError == true ? "error: " : "not an error: ") + Text(result);
        }
        catch (ModelContextProtocol.McpException refused)
        {
            return "error: " + refused.Message;
        }
    }

    private static Refusal CliRefusalFor(StubIngestRefusal refusal) =>
        refusal == StubIngestRefusal.AgentInFlight
            ? new Refusal(RefusalKind.Conflict, PublishedBudgetGate.InFlightStatus(), new Error(PublishedBudgetGate.InFlightType(), "Another write of this agent is being recorded", "nothing was recorded, counted or spent; send it again"))
            : new Refusal(RefusalKind.ServerFault, PublishedBudgetGate.UnavailableStatus(), new Error(PublishedBudgetGate.UnavailableType(), "The write gate could not be reached; nothing was recorded"));

    private async Task<IEnumerable<string>> ProblemsOfAsync(string tool, StubIngestRefusal refusal)
    {
        using var log = new StubLog();
        log.RefusesSubmissionsAs = refusal;
        var relayed = await CallAsync(log, tool, AliceTools(log));
        var cli = CliRefusalFor(refusal).Summary;
        var clauses = refusal == StubIngestRefusal.AgentInFlight
            ? new[] { PublishedBudgetGate.Clause("was not recorded"), PublishedBudgetGate.Clause("neither a tier denial nor a spent budget") }
            : [PublishedBudgetGate.Clause("a fault of the Forum"), PublishedBudgetGate.Clause("nothing recorded")];
        return new[]
        {
            relayed.StartsWith("error: ", StringComparison.Ordinal) ? string.Empty : "not relayed as an error",
            Missing(relayed, clauses),
            relayed.Contains(cli, StringComparison.Ordinal) ? string.Empty : "not the words curia prints",
            relayed.Contains("trust tier", StringComparison.OrdinalIgnoreCase) ? "worded with a tier" : string.Empty,
        }.Where(p => p.Length > 0).Select(p => $"{refusal}: {p}");
    }

    /// <summary>How many POSTs to the write route one call made (a first DPoP request is answered with the nonce challenge, so a call that is served once makes two).</summary>
    private async Task<int> WritesMadeAsync(string tool, StubIngestRefusal? refusal)
    {
        using var log = new StubLog();
        log.RefusesSubmissionsAs = refusal;
        _ = await CallAsync(log, tool, AliceTools(log));
        return log.Requests.Count(r => r.StartsWith("POST /v1/posts", StringComparison.Ordinal) && !r.Contains("/batch", StringComparison.Ordinal));
    }

    /// <summary>BRB43. Every tool R11.64 names relays both refusals with the same words <c>curia</c> prints for the same document, as an error, with no tier span.</summary>
    [Theory]
    [MemberData(nameof(RelayedTools))]
    public async Task R11_64_BRB43_EveryWriteToolRelaysTheGateRefusalsInTheCliWords(string tool)
    {
        var problems = new List<string>();
        foreach (var refusal in new[] { StubIngestRefusal.AgentInFlight, StubIngestRefusal.GateUnavailable })
            problems.AddRange(await ProblemsOfAsync(tool, refusal));

        Said(string.Empty, string.Join("; ", problems));
    }

    private ForumTools ToolsOver(StubLog log) => AliceTools(log);

    /// <summary>BRB44, the tool half. The catalogue's tools whose descriptions carry a budget sentence include R11.64's five, and each states the wait, the queue, the order and the batch rule.</summary>
    [Fact]
    public void R11_64_BRB44_EveryBudgetedToolDescriptionStatesTheBound()
    {
        using var log = new StubLog();
        var tools = ToolsOver(log);
        var catalogue = ToolCatalogue.Build(tools).Select(t => (t.ProtocolTool.Name, Description: t.ProtocolTool.Description ?? string.Empty)).ToArray();
        // curia_budget (errata G49) is the readout itself and curia_flag's budget is the raiser's own gate (R7.22): neither is under R7.27's agent write gate.
        var budgeted = catalogue.Where(t => t.Name is not ("curia_budget" or "curia_flag") && t.Description.Contains("budget", StringComparison.OrdinalIgnoreCase)).ToArray();
        var named = PublishedBudgetGate.ToolNames();

        var seconds = PublishedBudgetGate.Wait().TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " seconds";
        var waiters = PublishedBudgetGate.Waiters().ToString(CultureInfo.InvariantCulture);
        var phrases = new[] { seconds, waiters, PublishedBudgetGate.Clause("one at a time in the order they reach the Forum"), PublishedBudgetGate.Clause("must not assume an order") };

        var problems = new List<string>();
        problems.AddRange(named.Where(n => budgeted.All(b => b.Name != n)).Select(n => $"{n} carries no budget sentence"));
        foreach (var (name, description) in budgeted.Concat(catalogue.Where(c => named.Contains(c.Name))).DistinctBy(t => t.Name))
        {
            var missing = Missing(description, phrases);
            if (missing.Length > 0) problems.Add($"{name}: {missing}");
        }

        Said(string.Empty, string.Join(" | ", problems));
    }

    /// <summary>BRB45. No hidden retry (a control): a Forum that answers agent-in-flight is sent to exactly once by each tool, and so is one that answers gate-unavailable. Green now; red under a client that retries by itself.</summary>
    [Theory]
    [MemberData(nameof(RelayedTools))]
    public async Task R11_64_BRB45_NoToolRetriesARefusalByItself(string tool)
    {
        var served = await WritesMadeAsync(tool, null);
        var inFlight = await WritesMadeAsync(tool, StubIngestRefusal.AgentInFlight);
        var unavailable = await WritesMadeAsync(tool, StubIngestRefusal.GateUnavailable);

        Said(
            $"a refused call makes as many writes as a served one ({served}): agent-in-flight {served}, gate-unavailable {served}",
            $"a refused call makes as many writes as a served one ({served}): agent-in-flight {inFlight}, gate-unavailable {unavailable}");
    }
}
