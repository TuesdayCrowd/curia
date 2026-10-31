using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Domain.Tests.Authorization;

/// <summary>
/// PR 3 (errata G50, R7.30), Task 0's probes RLW1 and RLW2 at the PDP. <b>CONSISTENCY CHECK (RLW42): this file reads <c>AccessPolicy.</c> on purpose.</b>
/// It is the pure unit file that holds the decision and its titles; the reason, the title text and the count of reasons are read from the errata
/// (<see cref="PublishedReadersLastWords"/>), so a constant the code changes cannot move its own expectation.
///
/// <para>On the seam tree <c>Discharge</c> takes <c>resourceKnownToExist</c> and reads it nowhere, so a denial is still <c>table-10/own-resource-only</c>
/// and the ninth reason has no title: both facts are red by assertion.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OwnResourceFoundTests
{
    private static AuthorizationRequest Request(PrincipalTier tier, ResourceKind resource, ActionKind action, CredentialState state = CredentialState.Active) =>
        new(TierFixture.As(tier), state, resource, action, 0, 0, 0);

    /// <summary>Every permitted decision the PDP gives whose permission is qualified by <c>(own)</c>: each undischarged, it is the place a route chooses its reason.</summary>
    private static List<AuthorizationDecision> OwnResourceDecisions()
    {
        var found = new List<AuthorizationDecision>();
        foreach (var (resource, action) in ResourceActionModel.ModelledPairs)
        foreach (var tier in Enum.GetValues<PrincipalTier>())
        {
            if (!AccessPolicy.Decide(Request(tier, resource, action)).TryGetValue(out var decision, out _)) continue;
            if (decision!.IsPermitted && decision.Qualifier is GrantQualifier.OwnResourceOnly) found.Add(decision);
        }

        return found;
    }

    /// <summary>
    /// RLW1. <c>Discharge(false)</c> is the shared reason for every <c>(own)</c> pair; <c>Discharge(false, resourceKnownToExist: true)</c> is R7.30's new
    /// reason; <c>Discharge(true, ...)</c> allows whatever the flag; and the PDP's own <c>Decide</c> never gives the new reason for any pair, tier or
    /// credential state (a route issues it, a table does not).
    /// </summary>
    [Fact]
    public void R7_30_RLW1_TheReasonIsChosenByWhetherTheRouteKnowsTheResourceExists()
    {
        var found = PublishedReadersLastWords.OwnResourceFound();
        var only = PublishedReadersLastWords.OwnResourceOnly();
        var decisions = OwnResourceDecisions();
        Assert.NotEmpty(decisions);

        var problems = new List<string>();
        foreach (var decision in decisions)
        {
            var unknown = decision.Discharge(false);
            var known = decision.Discharge(false, resourceKnownToExist: true);
            if (unknown.Reason != only) problems.Add($"Discharge(false): Expected: {only} Actual: {unknown.Reason}");
            if (known.Reason != found) problems.Add($"Discharge(false, resourceKnownToExist: true): Expected: {found} Actual: {known.Reason}");
            if (!decision.Discharge(true, resourceKnownToExist: true).IsAllowed || !decision.Discharge(true).IsAllowed)
                problems.Add("Discharge(true) did not allow");
            if (known.Effect is not DecisionEffect.Deny) problems.Add("a denial that knows the resource exists is not a denial");
        }

        foreach (var (resource, action) in ResourceActionModel.ModelledPairs)
        foreach (var tier in Enum.GetValues<PrincipalTier>())
        foreach (var state in Enum.GetValues<CredentialState>())
        {
            if (AccessPolicy.Decide(Request(tier, resource, action, state)).TryGetValue(out var decision, out _) && decision!.Reason == found)
                problems.Add($"Decide gave the route's reason for {resource}/{action} at {tier}/{state}");
        }

        Assert.True(!problems.Distinct().Any(), string.Join("; ", problems.Distinct().Take(6)));
    }

    /// <summary>
    /// RLW2. The reasons a denial can carry are the PDP's observed eight and R7.30's, and no other, both ways; every title is distinct and is
    /// <c>TitleFor(reason)</c> alone; the new title is the entry's and says the resource exists without "does not exist", "may not exist" or the tier;
    /// the shared title keeps "does not exist".
    /// </summary>
    [Fact]
    public void R7_30_RLW2_TheNinthReasonHasItsOwnTitleAndTheSharedOneKeepsItsClause()
    {
        var found = PublishedReadersLastWords.OwnResourceFound();
        var only = PublishedReadersLastWords.OwnResourceOnly();
        var pdp = PublishedAcceptBudget.Causes().Length;
        var reasons = AccessPolicy.DenialReasons.ToArray();

        var problems = new List<string>();
        if (reasons.Length != pdp + 1) problems.Add($"Expected: {pdp + 1} reasons (the PDP's {pdp} and R7.30's) Actual: {reasons.Length}");
        if (!reasons.Contains(found, StringComparer.Ordinal)) problems.Add($"Expected: the reason {found} in DenialReasons Actual: {string.Join(", ", reasons)}");

        var titles = reasons.Select(AccessPolicy.TitleFor).ToArray();
        if (titles.Distinct(StringComparer.Ordinal).Count() != titles.Length) problems.Add("two reasons share a title");
        if (titles.Contains(AccessPolicy.NeutralDenialTitle, StringComparer.Ordinal)) problems.Add("a reason has only the neutral title");

        var title = AccessPolicy.TitleFor(found);
        if (title != PublishedReadersLastWords.FoundTitle()) problems.Add($"Expected: the title \"{PublishedReadersLastWords.FoundTitle()}\" Actual: \"{title}\"");
        if (title.Contains("does not exist", StringComparison.OrdinalIgnoreCase) || title.Contains("may not exist", StringComparison.OrdinalIgnoreCase)) problems.Add("the new title hedges");
        if (title.Contains("trust tier", StringComparison.OrdinalIgnoreCase)) problems.Add("the new title names the tier");
        if (!title.Contains("exists", StringComparison.Ordinal)) problems.Add("the new title does not say the resource exists");
        if (!AccessPolicy.TitleFor(only).Contains("does not exist", StringComparison.Ordinal)) problems.Add("the shared title lost \"does not exist\"");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
