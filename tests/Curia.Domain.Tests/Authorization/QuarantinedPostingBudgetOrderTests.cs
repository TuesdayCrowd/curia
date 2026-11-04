using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Xunit;

namespace Curia.Domain.Tests.Authorization;

/// <summary>
/// ASA50 (errata G58, plan ruling 42): the premise the route's R7.32 <c>Active</c> clause rests on, held in the policy itself, with no log and no event. The route lets the posting budget's denial give way to
/// R8.78 for a credential that is not set aside, and that clause is needed only because <see cref="AccessPolicy"/> decides the posting budget before the quarantine intersection: a quarantined credential at a spent
/// posting budget is told the budget's reason and not the quarantine's. The clause itself has no probe until an event can quarantine a credential (the quarantine leg of R7.32 is implemented and not discharged). The
/// expected reasons are the errata's literals (R7.24, R7.26), written here and not read from the policy's constants.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class QuarantinedPostingBudgetOrderTests
{
    private const string PostingBudgetReason = "table-11/rate-budget-exhausted";
    private const string QuarantineReason = "table-11/quarantined-read-only";

    private static AuthorizationDecision Decide(AuthorizationRequest request)
    {
        Assert.True(AccessPolicy.Decide(request).TryGetValue(out var decision, out var error), error?.Type);
        return decision!;
    }

    private static AuthorizationRequest Question(PrincipalTier tier, int postsToday) =>
        new(TierFixture.As(tier), CredentialState.Quarantined, ResourceKind.Question, ActionKind.Create, postsToday);

    /// <summary>
    /// ASA50. For T0 and T1 (each at its own spent count): a quarantined request at a spent posting budget is denied with the posting budget's reason (the premise); one under the budget is denied the quarantine's
    /// (quarantine still dominates, so a policy that stopped consulting the state does not satisfy the first leg); a quarantined acceptance at a spent acceptance budget is the quarantine's (the contrast R7.26 states).
    /// Predicted red under FASA42: the quarantine intersection moved before the posting budget (first leg), or a quarantine that skips a spent budget (second).
    /// </summary>
    [Theory]
    [InlineData(PrincipalTier.T0)]
    [InlineData(PrincipalTier.T1)]
    public void R7_32_ASA50_AQuarantinedCredentialAtASpentPostingBudgetIsToldTheBudget(PrincipalTier tier)
    {
        var spent = TierPolicy.PostsPerDay(tier);
        var atBudget = Decide(Question(tier, spent));
        var underBudget = Decide(Question(tier, spent - 1));
        var accepts = TierPolicy.AcceptsPerDay(tier);
        var acceptance = Decide(new AuthorizationRequest(TierFixture.As(tier), CredentialState.Quarantined, ResourceKind.Answer, ActionKind.Accept, 0, FlagsToday: 0, accepts));

        Assert.Equal(
            $"Deny {PostingBudgetReason}; Deny {QuarantineReason}; Deny {QuarantineReason}",
            $"{atBudget.Effect} {atBudget.Reason}; {underBudget.Effect} {underBudget.Reason}; {acceptance.Effect} {acceptance.Reason}");
    }
}
