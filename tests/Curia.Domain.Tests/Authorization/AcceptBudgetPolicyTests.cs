using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Domain.Tests.Authorization;

/// <summary>
/// R7.26 and R7.24 (rev.) (errata G46): an acceptance spends no posting budget and has one of its own. Probes TRW1 to TRW8, written in their final form and
/// seen red on a tree whose request carries <c>AcceptsToday</c> and <c>AcceptRecordsNothing</c> and does not read them. The reason, the three numbers and the
/// cause list are read from the errata's text (<see cref="PublishedAcceptBudget"/>), never from <see cref="TierPolicy"/> or <see cref="AccessPolicy"/>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptBudgetPolicyTests
{
    private static readonly PrincipalTier[] Credentialed = [PrincipalTier.T0, PrincipalTier.T1, PrincipalTier.T2];

    private static int Budget(PrincipalTier tier) => PublishedAcceptBudget.Numbers()[(int)tier - (int)PrincipalTier.T0];

    private static AuthorizationRequest Accept(
        PrincipalTier tier,
        int? acceptsToday = 0,
        int postsToday = 0,
        CredentialState state = CredentialState.Active,
        bool recordsNothing = false) =>
        new(TierFixture.As(tier), state, ResourceKind.Answer, ActionKind.Accept, postsToday, FlagsToday: 0, acceptsToday, recordsNothing);

    private static AuthorizationRequest Other(PrincipalTier tier, ResourceKind resource, ActionKind action, int postsToday = 0, int flagsToday = 0, int acceptsToday = 0) =>
        new(TierFixture.As(tier), CredentialState.Active, resource, action, postsToday, flagsToday, acceptsToday);

    private static AuthorizationDecision Decide(AuthorizationRequest request)
    {
        Assert.True(AccessPolicy.Decide(request).TryGetValue(out var decision, out var error), error?.Type);
        return decision!;
    }

    /// <summary>
    /// TRW1. Written against the members the request already had, so it is red by behaviour: the posting budget is not the acceptance's. T3's posting budget is
    /// <see cref="int.MaxValue"/> ("Negotiated"), and a count at it was refused too (M6).
    /// </summary>
    [Theory]
    [InlineData(PrincipalTier.T0)]
    [InlineData(PrincipalTier.T1)]
    [InlineData(PrincipalTier.T2)]
    [InlineData(PrincipalTier.T3)]
    public void R7_26_TRW1_AnAcceptanceIsNotRefusedForASpentPostingBudget(PrincipalTier tier)
    {
        var decision = Decide(Accept(tier, acceptsToday: 0, postsToday: TierPolicy.PostsPerDay(tier)));

        Assert.Equal(
            "Allow table-10/permitted OwnThreadOnly",
            $"{decision.Effect} {decision.Reason} {decision.Qualifier}");
    }

    /// <summary>TRW2. The edge: one under the published number is an allow, the number is a deny with the published reason; posts are zero throughout.</summary>
    [Theory]
    [InlineData(PrincipalTier.T0)]
    [InlineData(PrincipalTier.T1)]
    [InlineData(PrincipalTier.T2)]
    public void R7_26_TRW2_TheAcceptanceBudgetsEdgeIsThePublishedNumber(PrincipalTier tier)
    {
        var budget = Budget(tier);

        var under = Decide(Accept(tier, acceptsToday: budget - 1));
        var at = Decide(Accept(tier, acceptsToday: budget));

        Assert.Equal(
            $"Allow; Deny {PublishedAcceptBudget.Reason()}",
            $"{under.Effect}; {at.Effect} {at.Reason}");
    }

    /// <summary>TRW3. The numbers <see cref="TierPolicy.AcceptsPerDay"/> gives are the three R7.26 publishes; T3 is negotiated and Anonymous has none.</summary>
    [Fact]
    public void R7_26_TRW3_TheAcceptanceBudgetsAreThePublishedOnes()
    {
        Assert.Equal(
            [.. PublishedAcceptBudget.Numbers(), int.MaxValue, 0],
            [
                TierPolicy.AcceptsPerDay(PrincipalTier.T0),
                TierPolicy.AcceptsPerDay(PrincipalTier.T1),
                TierPolicy.AcceptsPerDay(PrincipalTier.T2),
                TierPolicy.AcceptsPerDay(PrincipalTier.T3),
                TierPolicy.AcceptsPerDay(PrincipalTier.Anonymous),
            ]);
    }

    /// <summary>TRW4. A missing count is a failure and never an allow or a deny, in each credential state that has a permitting cell.</summary>
    [Fact]
    public void R7_26_TRW4_AMissingAcceptanceCountIsAFailureInEveryCredentialState()
    {
        var actual = new List<string>();
        var expected = new List<string>();
        foreach (var tier in Credentialed.Append(PrincipalTier.T3))
        foreach (var state in new[] { CredentialState.Active, CredentialState.Quarantined })
        {
            var result = AccessPolicy.Decide(Accept(tier, acceptsToday: null, state: state));
            actual.Add($"{tier}/{state}: {(result.TryGetValue(out var decision, out var error) ? decision!.Effect + " " + decision.Reason : "fail " + error!.Type)}");
            expected.Add($"{tier}/{state}: fail curia/authz/accept-count-missing");
        }

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// TRW5. A replay is not refused for the budget and confers nothing. The first assertion is the contrast that makes the rest mean something (without the
    /// fact, the number is a deny); the others hold that the fact does not turn a tier, quarantine or ownership denial into an allow. The fact alone is green on
    /// a tree with no budget: it is the contrast that is red.
    /// </summary>
    [Fact]
    public void R7_26_TRW5_AReplayIsNotRefusedForTheBudgetAndConfersNothing()
    {
        var contrast = new List<string>();
        var replay = new List<string>();
        foreach (var tier in Credentialed)
        {
            contrast.Add($"{tier} {Decide(Accept(tier, acceptsToday: Budget(tier))).Effect}");
            replay.Add($"{tier} {Decide(Accept(tier, acceptsToday: Budget(tier), recordsNothing: true)).Effect}");
        }

        Assert.Equal("T0 Deny, T1 Deny, T2 Deny", string.Join(", ", contrast));
        Assert.Equal("T0 Allow, T1 Allow, T2 Allow", string.Join(", ", replay));

        var anonymous = Decide(Accept(PrincipalTier.Anonymous, acceptsToday: 0, recordsNothing: true));
        var quarantined = Decide(Accept(PrincipalTier.T1, acceptsToday: 0, state: CredentialState.Quarantined, recordsNothing: true));
        var notOwn = Decide(Accept(PrincipalTier.T1, acceptsToday: Budget(PrincipalTier.T1), recordsNothing: true)).Discharge(false);

        Assert.Equal(
            "Deny table-10/denied; Deny table-11/quarantined-read-only; Deny table-10/own-thread-only",
            $"{anonymous.Effect} {anonymous.Reason}; {quarantined.Effect} {quarantined.Reason}; {notOwn.Effect} {notOwn.Reason}");
    }

    /// <summary>
    /// TRW6 (Domain-level only: nothing in <c>src/</c> produces a quarantined credential for an agent, M6). A quarantined credential is told its quarantine, with
    /// the acceptance count over the budget, under it, and with the posting count over its budget.
    /// </summary>
    [Theory]
    [InlineData(PrincipalTier.T0)]
    [InlineData(PrincipalTier.T1)]
    [InlineData(PrincipalTier.T2)]
    public void R7_26_TRW6_AQuarantinedCredentialIsToldItsQuarantineAndNotABudget(PrincipalTier tier)
    {
        var over = Decide(Accept(tier, acceptsToday: Budget(tier) + 1, state: CredentialState.Quarantined));
        var under = Decide(Accept(tier, acceptsToday: 0, state: CredentialState.Quarantined));
        var postsOver = Decide(Accept(tier, acceptsToday: 0, postsToday: TierPolicy.PostsPerDay(tier), state: CredentialState.Quarantined));

        Assert.Equal(
            "Deny table-11/quarantined-read-only; Deny table-11/quarantined-read-only; Deny table-11/quarantined-read-only",
            $"{over.Effect} {over.Reason}; {under.Effect} {under.Reason}; {postsOver.Effect} {postsOver.Reason}");
    }

    /// <summary>TRW7. The two budgets are separate in both directions.</summary>
    [Fact]
    public void R7_26_TRW7_TheAcceptanceAndPostingBudgetsAreSeparateInBothDirections()
    {
        var acceptWithPostsSpent = Decide(Accept(PrincipalTier.T1, acceptsToday: 0, postsToday: int.MaxValue));

        var others = new[]
        {
            Decide(Other(PrincipalTier.T1, ResourceKind.Comment, ActionKind.Create, acceptsToday: int.MaxValue)),
            Decide(Other(PrincipalTier.T1, ResourceKind.Question, ActionKind.Create, acceptsToday: int.MaxValue)),
            Decide(Other(PrincipalTier.T1, ResourceKind.Flag, ActionKind.Raise, acceptsToday: int.MaxValue)),
            Decide(Other(PrincipalTier.T1, ResourceKind.Revision, ActionKind.Create, acceptsToday: int.MaxValue)),
        };
        var commentAtBudget = Decide(Other(PrincipalTier.T1, ResourceKind.Comment, ActionKind.Create, postsToday: TierPolicy.PostsPerDay(PrincipalTier.T1)));

        Assert.Equal(
            "Allow; Allow Allow Allow Allow; Deny table-11/rate-budget-exhausted",
            $"{acceptWithPostsSpent.Effect}; {string.Join(' ', others.Select(o => o.Effect))}; {commentAtBudget.Effect} {commentAtBudget.Reason}");
    }

    /// <summary>TRW8. The published reason is one the policy can give, and its title names its cause as R7.24 (rev.) says.</summary>
    [Fact]
    public void R7_24_TRW8_TheReasonIsOneThePolicyGivesAndItsTitleNamesTheAcceptanceBudget()
    {
        var reason = PublishedAcceptBudget.Reason();
        var title = AccessPolicy.TitleFor(reason);

        Assert.Contains(reason, AccessPolicy.DenialReasons);
        Assert.Contains("acceptance budget", PublishedAcceptBudget.CauseParagraph(), StringComparison.Ordinal);
        Assert.Contains("acceptance budget", title, StringComparison.Ordinal);
        Assert.DoesNotContain("trust tier", title, StringComparison.Ordinal);
        Assert.NotEqual(AccessPolicy.NeutralDenialTitle, title);
    }
}
