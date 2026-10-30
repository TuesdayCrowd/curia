using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Client;
using Curia.Client.Cli;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R11.64 (errata G48): <c>curia</c> words the agent write gate's two refusals by their cause, and states the bound in <c>curia help</c> (probes BRB42, BRB44's help
/// half, BRB46). The clauses are R11.64's own, each checked to be in the errata's paragraph before it is asked of the client
/// (<see cref="PublishedBudgetGate.Clause"/>), the wait and the queue are R7.27's numbers parsed from the errata, and the problem types and statuses are R7.27's.
/// Each fact reports the phrases that are missing, so a red names what the reader does not say. <c>curia-mcp</c> shares <see cref="Refusal.Summary"/>; its half is
/// in <c>Curia.Mcp.Tests</c> (BRB43, BRB44, BRB45).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("console")]
public sealed class BudgetRefusalWordsTests
{
    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string Missing(string text, params string[] phrases) =>
        string.Join("; ", phrases.Where(p => !text.Contains(p, StringComparison.OrdinalIgnoreCase)).Select(p => $"missing \"{p}\""));

    private static Refusal InFlight() =>
        new(RefusalKind.Conflict, PublishedBudgetGate.InFlightStatus(), new Error(PublishedBudgetGate.InFlightType(), "Another write of this agent is being recorded", "nothing was recorded, counted or spent; send it again"));

    private static Refusal Unavailable() =>
        new(RefusalKind.ServerFault, PublishedBudgetGate.UnavailableStatus(), new Error(PublishedBudgetGate.UnavailableType(), "The write gate could not be reached; nothing was recorded"));

    private static string NotATierOrBudget(Refusal refusal) =>
        refusal.Kind is RefusalKind.Authorization or RefusalKind.RateBudget
            || refusal.Summary.Contains("trust tier", StringComparison.OrdinalIgnoreCase)
            || refusal.Summary.Contains("Today's", StringComparison.Ordinal)
            ? "worded as a tier or budget denial"
            : string.Empty;

    /// <summary>BRB42. An agent-in-flight refusal is a retry of an act that was not recorded, neither a tier denial nor a spent budget, exit 3, and the exit-3 entry of the help names the type among the retryable conflicts.</summary>
    [Fact]
    public void R11_64_BRB42_AnAgentInFlightRefusalIsARetryOfAnActThatWasNotRecorded()
    {
        var refusal = InFlight();

        var problems = string.Join(
            "; ",
            new[]
            {
                Missing(refusal.Summary, PublishedBudgetGate.Clause("was not recorded"), PublishedBudgetGate.Clause("neither a tier denial nor a spent budget"), PublishedBudgetGate.Clause("retry")),
                NotATierOrBudget(refusal),
                ExitCode.For(refusal) == 3 ? string.Empty : $"exit {ExitCode.For(refusal)}",
                Missing(Help.Usage, PublishedBudgetGate.InFlightType()).Replace("missing", "the help's exit 3 entry is missing", StringComparison.Ordinal),
            }.Where(p => p.Length > 0));

        Said(string.Empty, problems);
    }

    /// <summary>BRB46. A gate-unavailable refusal is a fault of the Forum with nothing recorded and the act able to be sent again, not a tier denial or a budget; the CLI exits with the Forum-fault code.</summary>
    [Fact]
    public void R11_64_BRB46_AGateUnavailableRefusalIsAFaultOfTheForumWithNothingRecorded()
    {
        var refusal = Unavailable();
        var plainFault = new Refusal(RefusalKind.ServerFault, 500, new Error("curia/x/y", "A fault"));

        var problems = string.Join(
            "; ",
            new[]
            {
                Missing(refusal.Summary, PublishedBudgetGate.Clause("a fault of the Forum"), PublishedBudgetGate.Clause("nothing recorded"), PublishedBudgetGate.Clause("sent again")),
                NotATierOrBudget(refusal),
                ExitCode.For(refusal) == ExitCode.For(plainFault) ? string.Empty : $"exit {ExitCode.For(refusal)}, not the Forum-fault code {ExitCode.For(plainFault)}",
            }.Where(p => p.Length > 0));

        Said(string.Empty, problems);
    }

    /// <summary>BRB44, the help half. <c>curia help</c> states the wait and the queue R7.27 publishes, that one agent's simultaneous writes are processed one at a time in the order they reach the Forum, and that a parallel batch must not assume an order.</summary>
    [Fact]
    public void R11_64_BRB44_TheHelpStatesTheBound()
    {
        var seconds = PublishedBudgetGate.Wait().TotalSeconds.ToString("0", CultureInfo.InvariantCulture);
        var waiters = PublishedBudgetGate.Waiters().ToString(CultureInfo.InvariantCulture);

        var help = Help.Usage;
        var problems = Missing(
            help,
            seconds + " seconds",
            waiters,
            PublishedBudgetGate.Clause("one at a time in the order they reach the Forum"),
            PublishedBudgetGate.Clause("must not assume an order"));

        Said(string.Empty, problems);
    }
}
