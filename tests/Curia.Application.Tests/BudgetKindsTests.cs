using System.Diagnostics.CodeAnalysis;
using Curia.Application.Budget;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// Errata G49, SEB6 and SEB52: the three budgets as data and the credential word as a total function. The names and their order are R7.28's and R9.50's,
/// parsed from the errata; the reasons are the policy's own denial enumeration filtered to <c>table-11/*-budget-exhausted</c>, so a fourth reason with
/// no budget (or a budget with no reason) is red.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetKindsTests
{
    private static string[] BudgetReasons() =>
        [.. AccessPolicy.DenialReasons.Where(r => r.StartsWith("table-11/", StringComparison.Ordinal) && r.EndsWith("-budget-exhausted", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    /// <summary>
    /// SEB6. The budgets are the policy's, data-driven: the names equal R9.50's list in R9.50's order, and the reasons the kinds carry are exactly the
    /// policy's <c>table-11/*-budget-exhausted</c> reasons, one to one. (A guard against the control: the policy does have three such reasons.)
    /// </summary>
    [Fact]
    public void R7_28_SEB6_TheBudgetsAreThePolicysDenialReasonsOneToOne()
    {
        var reasons = BudgetReasons();
        Assert.Equal(3, reasons.Length);

        var kinds = BudgetKinds.All;
        Assert.True(
            kinds.Select(k => k.Name).SequenceEqual(PublishedBudgetReadout.Names(), StringComparer.Ordinal),
            $"Expected: {{{string.Join(", ", PublishedBudgetReadout.Names())}}} Actual: {{{string.Join(", ", kinds.Select(k => k.Name))}}}");
        Assert.Equal(reasons, kinds.Select(k => k.DenialReason).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// SEB52 (ruling 17). The credential word is a total function of <see cref="CredentialState"/>, enumerated over every member (a new member is red until
    /// it is decided): <c>Active</c> and <c>Quarantined</c> have R9.50's words in R9.50's order, and every other state yields no readout, never
    /// <c>active</c> by default.
    /// </summary>
    [Fact]
    public void R9_50_SEB52_TheCredentialWordIsATotalFunctionOfTheState()
    {
        var words = PublishedBudgetReadout.CredentialWords();
        var expected = new Dictionary<CredentialState, string?>
        {
            [CredentialState.Active] = words[0],
            [CredentialState.Quarantined] = words[1],
        };

        var problems = new List<string>();
        foreach (var state in Enum.GetValues<CredentialState>())
        {
            var result = BudgetKinds.CredentialWord(state);
            var word = result.TryGetValue(out var w, out _) ? w : null;
            var want = expected.GetValueOrDefault(state);
            if (word != want) problems.Add($"{state}: Expected: {want ?? "no readout"} Actual: {word ?? "no readout"}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
