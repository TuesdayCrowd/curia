using System.Diagnostics.CodeAnalysis;
using Curia.Application.Budget;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// BRB48 (errata G48, R7.27): the published numbers are the code's. This file is the one place a probe reads <see cref="AgentWriteGateBounds"/> and
/// <see cref="BudgetErrors"/>, and it reads them to compare them with the errata's text, not the reverse (BRB49 fences every other file). The statuses are the
/// routes' and are held by the Api suite's BRB25 and BRB27 against R7.27's code spans.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AgentWriteGatePinTests
{
    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>BRB48. The wait and the queue the code publishes are R7.27's two numbers, and the two problem types are its code spans.</summary>
    [Fact]
    public void R7_27_BRB48_TheCodesBoundsAndTypesAreTheErratas()
    {
        Said(
            $"wait {PublishedBudgetGate.Wait().TotalSeconds:0} s; waiters {PublishedBudgetGate.Waiters()}; {PublishedBudgetGate.InFlightType()}; {PublishedBudgetGate.UnavailableType()}",
            $"wait {AgentWriteGateBounds.Wait.TotalSeconds:0} s; waiters {AgentWriteGateBounds.Waiters}; {BudgetErrors.AgentInFlight().Type}; {BudgetErrors.GateUnavailable().Type}");
    }

    /// <summary>BRB54, the parsers' half that this assembly can see: R7.27 and R11.64 are in the errata with the numbers, types, statuses and tools the other facts parse.</summary>
    [Fact]
    public void R7_27_BRB54_TheErratasWordsParse()
    {
        var (posting, acceptance) = PublishedBudgetGate.BudgetReasons();

        Said(
            "wait 5 s; waiters 16; 409 curia/budget/agent-in-flight; 503 curia/budget/gate-unavailable; budget 403 table-11/rate-budget-exhausted | table-11/accept-budget-exhausted; tools curia_ask, curia_answer, curia_comment, curia_revise, curia_resolve",
            $"wait {PublishedBudgetGate.Wait().TotalSeconds:0} s; waiters {PublishedBudgetGate.Waiters()}; {PublishedBudgetGate.InFlightStatus()} {PublishedBudgetGate.InFlightType()}; {PublishedBudgetGate.UnavailableStatus()} {PublishedBudgetGate.UnavailableType()}; " +
            $"budget {PublishedBudgetGate.BudgetStatus()} {posting} | {acceptance}; tools {string.Join(", ", PublishedBudgetGate.ToolNames())}");
    }
}
