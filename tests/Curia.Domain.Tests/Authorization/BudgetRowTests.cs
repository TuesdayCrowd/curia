using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Credentials;
using Curia.Tests.Shared;
using Curia.Domain.Authorization;
using Xunit;

namespace Curia.Domain.Tests.Authorization;

/// <summary>
/// Errata G49, SEB8 to SEB10: Table 10's new row <c>budget</c> / <c>read</c> (own) and what the policy decides for it. <c>ResourceKind.Budget</c> is looked
/// up by name at run time, so the seam tree compiles and each fact is red by assertion ("Expected: ResourceKind.Budget Actual: absent") until Task 1 adds
/// the kind. The row, its cells and the denial count it moves are the errata's, parsed at test time; the limits are Table 11's, R7.26's and R7.22's.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetRowTests
{
    private static ResourceKind Budget()
    {
        Assert.True(Enum.TryParse<ResourceKind>("Budget", out var kind), "Expected: ResourceKind.Budget Actual: absent (Table 10 has no budget kind)");
        return kind;
    }

    private static (string Resource, string Action, string[] Cells) Quoted() => PublishedBudgetReadout.Table10Row();

    /// <summary>
    /// SEB8. The row, both ways: the white paper's Table 10 has exactly the row the errata quotes (cell for cell), the model holds the pair and agrees with
    /// each cell, and the published denials number what the errata says they move to (counted from the white paper).
    /// </summary>
    [Fact]
    public void R7_28_SEB8_TheRowIsInTheWhitePaperAndTheModelAndTheyAgree()
    {
        var (resource, action, cells) = Quoted();
        var problems = new List<string>();

        var rows = PublishedBudgetReadout.WhitePaperBudgetRows();
        string[] expected = [$"`{resource}`", $"`{action}` (own)", .. cells];
        if (rows.Length != 1) problems.Add($"Expected: one `{resource}` row in Table 10 Actual: {rows.Length}");
        else if (!rows[0].SequenceEqual(expected, StringComparer.Ordinal)) problems.Add($"Expected: [{string.Join(" | ", expected)}] Actual: [{string.Join(" | ", rows[0])}]");

        var denied = PublishedTable10.DeniedCells().Count();
        if (denied != PublishedBudgetReadout.DenialsAfter()) problems.Add($"Expected: {PublishedBudgetReadout.DenialsAfter()} published denials Actual: {denied}");

        if (!Enum.TryParse<ResourceKind>("Budget", out var budget)) problems.Add("Expected: ResourceKind.Budget Actual: absent");
        else
        {
            if (!ResourceActionModel.ModelledPairs.Contains((budget, ActionKind.Read))) problems.Add("Expected: the model holds (Budget, Read) Actual: not modelled");
            else
            {
                PrincipalTier[] tiers = [PrincipalTier.Anonymous, PrincipalTier.T0, PrincipalTier.T1, PrincipalTier.T2, PrincipalTier.T3];
                for (var i = 0; i < tiers.Length; i++)
                {
                    var want = cells[i] == "✗" ? Table10Cell.Denied : Table10Cell.Allowed;
                    var got = ResourceActionModel.CellFor(budget, ActionKind.Read, tiers[i]);
                    if (!got.TryGetValue(out var cell, out _) || cell != want) problems.Add($"{tiers[i]}: Expected: {want} Actual: {(got.TryGetValue(out var c2, out _) ? c2.ToString() : "failure")}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    private static AuthorizationDecision Decide(AuthorizationRequest request)
    {
        var decided = AccessPolicy.Decide(request);
        Assert.True(decided.TryGetValue(out var decision, out var error), $"Expected: a decision Actual: failure {error?.Type}");
        return decision!;
    }

    /// <summary>
    /// SEB9. The decision: T0 to T3 allow with the <c>(own)</c> qualifier, anonymous denies, and a quarantined credential is allowed because a read is within
    /// the tier's answer and is never more capable than the tier's (the monotonicity the quarantine intersection exists for).
    /// </summary>
    [Fact]
    public void R7_28_SEB9_TheDecisionAllowsOwnReadsFromT0AndDeniesAnonymous()
    {
        var budget = Budget();
        var problems = new List<string>();

        foreach (var tier in new[] { PrincipalTier.T0, PrincipalTier.T1, PrincipalTier.T2, PrincipalTier.T3 })
        {
            var d = Decide(new AuthorizationRequest(TierFixture.As(tier), CredentialState.Active, budget, ActionKind.Read));
            if ((d.Effect, d.Qualifier) != (DecisionEffect.Allow, GrantQualifier.OwnResourceOnly)) problems.Add($"{tier}: Expected: Allow OwnResourceOnly Actual: {d.Effect} {d.Qualifier}");

            var quarantined = Decide(new AuthorizationRequest(TierFixture.As(tier), CredentialState.Quarantined, budget, ActionKind.Read));
            if (quarantined.Effect != DecisionEffect.Allow) problems.Add($"{tier} quarantined: Expected: Allow Actual: {quarantined.Effect} {quarantined.Reason}");
        }

        var anonymous = Decide(new AuthorizationRequest(TierFixture.As(PrincipalTier.Anonymous), CredentialState.Active, budget, ActionKind.Read));
        if (anonymous.Effect != DecisionEffect.Deny) problems.Add($"Anonymous: Expected: Deny Actual: {anonymous.Effect}");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// SEB10. A spent budget does not refuse the read: a T0 request for the pair with every count at T0's published limit (Table 11, R7.26, R7.22) is allowed;
    /// the same counts refuse a post, an acceptance and a flag (the positive in the same fact).
    /// </summary>
    [Fact]
    public void R7_28_SEB10_ASpentBudgetDoesNotRefuseTheRead()
    {
        var posts = PublishedTable11.Rows["T0"].PostsPerDay!.Value;
        var limits = PublishedBudgetReadout.Limits([posts, 0, 0]);
        var (flags, accepts) = (limits["flag"][0], limits["acceptance"][0]);
        var t0 = TierFixture.As(PrincipalTier.T0);

        var problems = new List<string>();
        void Refused(string what, AuthorizationRequest request)
        {
            var d = Decide(request);
            if (d.Effect != DecisionEffect.Deny || !d.Reason.EndsWith("-budget-exhausted", StringComparison.Ordinal)) problems.Add($"{what}: Expected: Deny *-budget-exhausted Actual: {d.Effect} {d.Reason}");
        }

        Refused("post", new AuthorizationRequest(t0, CredentialState.Active, ResourceKind.Question, ActionKind.Create, posts, flags, accepts));
        Refused("flag", new AuthorizationRequest(t0, CredentialState.Active, ResourceKind.Flag, ActionKind.Raise, posts, flags, accepts));
        Refused("acceptance", new AuthorizationRequest(t0, CredentialState.Active, ResourceKind.Answer, ActionKind.Accept, posts, flags, accepts));

        var read = Decide(new AuthorizationRequest(t0, CredentialState.Active, Budget(), ActionKind.Read, posts, flags, accepts));
        if (read.Effect != DecisionEffect.Allow) problems.Add($"read: Expected: Allow Actual: {read.Effect} {read.Reason}");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
