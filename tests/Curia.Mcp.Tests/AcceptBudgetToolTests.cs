using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// Errata G46 at the MCP adapter: R7.26 (the acceptance budget is worded by its reason, the descriptions say so) and R8.79 (a lost race is worded as a
/// race). Probes TRW24, TRW25 (the adapter's half), TRW26 and TRW53. The reason, the three numbers, the problem type and the phrases are the errata's
/// (<see cref="PublishedAcceptBudget"/>); the refusal reaches the model through the client's real classification of the stub's 403.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptBudgetToolTests : IDisposable
{
    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-acceptbudget-").FullName;
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned) owned.Dispose();
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ForumTools Tools()
    {
        Assert.True(_log.Store.Load("alice").TryGetValue(out var agent, out var error), error?.Detail);
        _owned.Add(agent!);
        var session = new ForumSession(_log.Client(), agent!, _log.Store, TimeProvider.System);
        return new ForumTools(_log.Client(), MarkingMode.Datamark, new HeadStore(_home), new ForumWriter(agent!, session, TimeProvider.System));
    }

    private static void Need(List<string> problems, bool ok, string what)
    {
        if (!ok) problems.Add(what);
    }

    /// <summary>
    /// TRW24. <c>curia_resolve</c> of an acceptance refused for the published reason is worded as the acceptance budget: the three numbers as R7.26
    /// publishes them (read from the errata and not from <c>TierPolicy</c>), that it resets, that it is not a tier denial, that the posting budget is
    /// untouched and that a replay is free; not the tier span, the flag budget's words or the advice default.
    /// </summary>
    [Fact]
    public async Task R7_26_TRW24_AnAcceptanceRefusedForTheBudgetIsWordedAsTheAcceptanceBudget()
    {
        _log.DeniesSubmissionsForReason = PublishedAcceptBudget.Reason();

        var refused = await Assert.ThrowsAsync<McpException>(() => Tools().ResolveAsync(StubLog.AnswerPostId, Ct));

        var message = refused.Message;
        var n = PublishedAcceptBudget.Numbers();
        var numbers = new Regex($@"\b{n[0]}\b.{{0,40}}T0.{{0,40}}\b{n[1]}\b.{{0,40}}T1.{{0,40}}\b{n[2]}\b.{{0,40}}T2", RegexOptions.CultureInvariant | RegexOptions.Singleline);
        var problems = new List<string>();
        Need(problems, message.StartsWith("REFUSED:", StringComparison.Ordinal), "does not start REFUSED:");
        Need(problems, message.Contains("acceptance budget", StringComparison.Ordinal), "does not say acceptance budget");
        Need(problems, numbers.IsMatch(message), "does not give the published numbers by tier");
        Need(problems, message.Contains("next slot NOT STATED", StringComparison.Ordinal) && !message.Contains("resets", StringComparison.OrdinalIgnoreCase), "does not state the Forum's instant (or says it resets: a trailing window has no reset, errata G49)");
        Need(problems, message.Contains("not a tier denial", StringComparison.Ordinal), "does not say it is not a tier denial");
        Need(problems, message.Contains("posting budget", StringComparison.Ordinal) && !message.Contains("posting budget is spent", StringComparison.Ordinal), "does not say the posting budget is untouched");
        Need(problems, message.Contains("replay", StringComparison.Ordinal), "does not say a replay is free");
        Need(problems, !message.Contains("trust tier", StringComparison.Ordinal), "says trust tier");
        Need(problems, !message.Contains("flag budget", StringComparison.Ordinal), "says flag budget");
        Need(problems, !message.Contains("Retrying will not change this", StringComparison.Ordinal), "says Retrying will not change this");

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Actual: \"{message}\"");
    }

    /// <summary>
    /// TRW25, the adapter's half. <c>curia_resolve</c>'s description states that an acceptance spends the acceptance budget (the three numbers from R7.26),
    /// not the posting budget, and that accepting the answer that already stands is free.
    /// </summary>
    [Fact]
    public void R7_26_TRW25_TheResolveDescriptionStatesTheAcceptanceBudget()
    {
        var description = ToolText.ResolveTemplate;
        var problems = new List<string>();
        Need(problems, description.Contains("acceptance budget", StringComparison.Ordinal), "does not say acceptance budget");
        Need(problems, PublishedAcceptBudget.Numbers().All(x => description.Contains(x.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)), "does not give the three numbers");
        Need(problems, description.Contains("posting budget", StringComparison.Ordinal), "does not say the posting budget is untouched");
        Need(problems, description.Contains("free", StringComparison.OrdinalIgnoreCase), "does not say a replay is free");

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Expected: description containing \"acceptance budget\"");
    }

    /// <summary>
    /// TRW55, the adapter's half. <c>curia_resolve</c>'s description says an acceptance spends the acceptance budget and not the posting budget, in R7.26's
    /// terms, and nowhere that it spends the posting budget (a description that names both budgets passes TRW25 whichever way round it says it).
    /// </summary>
    [Fact]
    public void R7_26_TRW55_TheResolveDescriptionSaysAnAcceptanceSpendsItsOwnBudgetAndNotThePostingBudget()
    {
        var problems = PublishedAcceptBudget.SpendProblems(ToolText.ResolveTemplate);

        Assert.True(problems.Length == 0, $"{string.Join("; ", problems)}. Expected: \"spends the acceptance budget, not the posting budget\"");
    }

    /// <summary>TRW26. <c>curia-mcp</c> was 0.8.9 here and was 0.8.10 under errata G47 and is 0.8.11 since errata G48 (the pin follows the version; the name keeps its id): the descriptions and a refusal's wording change; no tool is added.</summary>
    [Fact]
    public void R7_26_TRW26_CuriaMcpIs089() => Assert.True(string.Equals(PublishedReadersLastWords.Version(), ToolCatalogue.Version, StringComparison.Ordinal), $"Expected: {PublishedReadersLastWords.Version()} Actual: {ToolCatalogue.Version}");   // the pin follows the errata's version (R11.66 since errata G50)

    /// <summary>
    /// TRW53. <c>WriteRefused</c> for a lost race at <c>answer</c>/<c>accept</c> says the same as the CLI, in the adapter's frame: nothing was recorded, the
    /// acceptance may be sent again, it is neither a tier denial nor a spent budget; not the tier span, the budget words or the advice default.
    /// </summary>
    [Fact]
    public void R8_79_TRW53_TheAdapterWordsALostRaceAsARace()
    {
        string[] phrases = ["nothing was recorded", "sent again", "neither a tier denial nor a spent budget"];
        var paragraph = PublishedAcceptBudget.Paragraph("**R8.79**");
        Assert.All(phrases, p => Assert.Contains(p, paragraph, StringComparison.Ordinal));

        var refusal = new Refusal(
            RefusalKind.ServerFault,
            503,
            new Error(PublishedAcceptBudget.ContendedType(), "The thread's acceptances changed under every attempt; nothing was recorded", "thread=01JTHREAD"));

        var message = ForumTools.WriteRefused(refusal, ResourceKind.Answer, ActionKind.Accept).Message;

        var problems = new List<string>();
        foreach (var phrase in phrases)
            Need(problems, message.Contains(phrase, StringComparison.Ordinal), $"does not say \"{phrase}\"");
        foreach (var word in new[] { "Retrying will not change this", "trust tier", "posting budget", "acceptance budget", "flag budget" })
            Need(problems, !message.Contains(word, StringComparison.Ordinal), $"says \"{word}\"");

        Assert.True(problems.Count == 0, $"{string.Join("; ", problems)}. Actual: \"{message}\"");
    }
}
