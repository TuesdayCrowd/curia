using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Xunit;

namespace Curia.Domain.Tests.Authorization;

/// <summary>§7's decision function: the rules Table 10 alone does not express.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AccessPolicyTests
{
    private static AuthorizationRequest Request(
        PrincipalTier tier,
        ResourceKind resource,
        ActionKind action,
        CredentialState state = CredentialState.Active,
        int postsToday = 0) =>
        new(TierFixture.As(tier), state, resource, action, postsToday);

    private static AuthorizationDecision Decide(AuthorizationRequest request)
    {
        Assert.True(AccessPolicy.Decide(request).TryGetValue(out var decision, out var error), error?.Type);
        return decision!;
    }

    /// <summary>
    /// R7.6: "Anonymous read access SHALL be an explicit allow decision from the PDP, not the
    /// absence of a check." Asserted as an allow that names the cell it came from -- a decision
    /// reached by default would carry no reason, so the reason is what distinguishes a rule from a
    /// fallthrough.
    /// </summary>
    [Theory]
    [InlineData(ResourceKind.Board, ActionKind.List)]
    [InlineData(ResourceKind.Board, ActionKind.Read)]
    [InlineData(ResourceKind.Thread, ActionKind.Read)]
    [InlineData(ResourceKind.Thread, ActionKind.Search)]
    public void R7_6_AnonymousReadIsAnExplicitAllow(ResourceKind resource, ActionKind action)
    {
        var decision = Decide(Request(PrincipalTier.Anonymous, resource, action));

        Assert.Equal(DecisionEffect.Allow, decision.Effect);
        Assert.Equal("table-10/permitted", decision.Reason);
    }

    /// <summary>
    /// Appendix F.1: "Quarantine dominates everything." A quarantined T3 -- the most capable tier
    /// there is -- keeps only its reads.
    /// </summary>
    [Theory]
    [InlineData(ResourceKind.Board, ActionKind.List, true)]
    [InlineData(ResourceKind.Thread, ActionKind.Search, true)]
    [InlineData(ResourceKind.Thread, ActionKind.Read, true)]
    [InlineData(ResourceKind.Question, ActionKind.Create, false)]
    [InlineData(ResourceKind.Answer, ActionKind.Create, false)]
    [InlineData(ResourceKind.Vote, ActionKind.Cast, false)]
    [InlineData(ResourceKind.Moderation, ActionKind.Apply, false)]
    [InlineData(ResourceKind.Flag, ActionKind.Raise, false)]
    public void Quarantine_leaves_reads_and_removes_everything_else(
        ResourceKind resource, ActionKind action, bool expectedAllow)
    {
        var decision = Decide(Request(PrincipalTier.T3, resource, action, CredentialState.Quarantined));

        Assert.Equal(expectedAllow ? DecisionEffect.Allow : DecisionEffect.Deny, decision.Effect);
        Assert.Equal("table-11/quarantined-read-only", decision.Reason);
    }

    /// <summary>
    /// The structural property behind the Table 11 / Appendix F.1 reading recorded in
    /// <see cref="AccessPolicy"/>: quarantine must never leave a principal able to do something an
    /// anonymous caller could not. If it did, an agent could gain capability by discarding its
    /// credential, which would make a posture trip an incentive to shed identity.
    ///
    /// <para>Checked over every modelled pair and tier rather than the handful above, because the
    /// claim is about the whole table, not about the rows someone thought to list.</para>
    /// </summary>
    [Fact]
    public void Quarantine_never_grants_more_than_the_tier_would()
    {
        foreach (var pair in ResourceActionModel.ModelledPairs)
        {
            if (pair is { Resource: ResourceKind.Agent, Action: ActionKind.Enroll }) continue;

            foreach (var tier in Enum.GetValues<PrincipalTier>())
            {
                var active = Decide(Request(tier, pair.Resource, pair.Action));
                var quarantined = Decide(Request(tier, pair.Resource, pair.Action, CredentialState.Quarantined));

                if (quarantined.IsAllowed)
                    Assert.True(
                        active.IsAllowed,
                        $"quarantine granted {PublishedTable10.Describe(pair, tier)} that the tier itself denies");
            }
        }
    }

    /// <summary>
    /// Table 10's single "rate-limited" cell. An exhausted budget is a denial with its own reason,
    /// not a tier denial -- R7.16 wants the audit trail to tell those apart, since one means
    /// "wait" and the other means "you will never be allowed this".
    /// </summary>
    [Fact]
    public void Rate_limited_cell_allows_within_budget_and_denies_outside_it()
    {
        var within = Decide(Request(PrincipalTier.T0, ResourceKind.Question, ActionKind.Create));
        Assert.Equal(DecisionEffect.Allow, within.Effect);
        Assert.Equal("table-10/rate-limited", within.Reason);

        var exhausted = Decide(Request(
            PrincipalTier.T0, ResourceKind.Question, ActionKind.Create,
            postsToday: TierPolicy.PostsPerDay(PrincipalTier.T0)));

        Assert.Equal(DecisionEffect.Deny, exhausted.Effect);
        Assert.Equal("table-11/rate-budget-exhausted", exhausted.Reason);

        // A tier whose cell is ✗ stays denied for the tier's reason, not the budget's -- the two
        // denials mean different things and R7.16 wants them distinguishable.
        Assert.Equal(
            "table-10/denied",
            Decide(Request(PrincipalTier.Anonymous, ResourceKind.Question, ActionKind.Create)).Reason);
    }

    /// <summary>
    /// <b>Table 11's budget binds every tier, not only Table 10's "rate-limited" cell.</b>
    ///
    /// <para>This test replaces one that asserted the opposite, and the correction matters. Table 10
    /// marks T0's <c>question</c>/<c>create</c> cell "rate-limited" and gives T1 and above a plain
    /// tick, which reads as though the budget were that cell's business alone. But Table 11 gives
    /// every tier a posting budget -- 3/day at T0, 25 at T1, 100 at T2 -- and a budget nothing
    /// consults is decoration. Under the earlier reading a T1 agent could post without limit
    /// forever, which is one agent away from flooding the Forum.</para>
    ///
    /// <para>Both tables hold and say different things: Table 11 caps how much a tier may post;
    /// Table 10's "rate-limited" cell marks the one place where the budget decides whether the
    /// action is permitted at all rather than bounding how often.</para>
    /// </summary>
    [Theory]
    [InlineData(PrincipalTier.T1, ResourceKind.Answer)]
    [InlineData(PrincipalTier.T2, ResourceKind.Finding)]
    [InlineData(PrincipalTier.T3, ResourceKind.Comment)]
    public void R7_15_TheTable11BudgetBindsEveryTier(PrincipalTier tier, ResourceKind resource)
    {
        var budget = TierPolicy.PostsPerDay(tier);

        var underBudget = Decide(Request(tier, resource, ActionKind.Create, postsToday: budget - 1));
        Assert.Equal(DecisionEffect.Allow, underBudget.Effect);

        var atBudget = Decide(Request(tier, resource, ActionKind.Create, postsToday: budget));
        Assert.Equal(DecisionEffect.Deny, atBudget.Effect);
        Assert.Equal("table-11/rate-budget-exhausted", atBudget.Reason);
    }

    /// <summary>
    /// T3's budget is "Negotiated" -- not a number -- so it is uncapped here rather than given an
    /// invented figure. Asserted so that inventing one later is a deliberate act.
    /// </summary>
    [Fact]
    public void T3s_negotiated_budget_does_not_exhaust()
    {
        var decision = Decide(Request(
            PrincipalTier.T3, ResourceKind.Comment, ActionKind.Create, postsToday: 1_000_000));

        Assert.Equal(DecisionEffect.Allow, decision.Effect);
    }

    /// <summary>
    /// Reads do not spend the posting budget. Table 11 gives reads their own column, in a different
    /// unit (per minute, not per day), so counting a read against the posting cap would enforce a
    /// limit the table does not state.
    /// </summary>
    [Theory]
    [InlineData(ResourceKind.Board, ActionKind.List)]
    [InlineData(ResourceKind.Thread, ActionKind.Read)]
    [InlineData(ResourceKind.Thread, ActionKind.Search)]
    public void Reads_do_not_spend_the_posting_budget(ResourceKind resource, ActionKind action)
    {
        var decision = Decide(Request(PrincipalTier.T0, resource, action, postsToday: 1_000_000));

        Assert.Equal(DecisionEffect.Allow, decision.Effect);
    }

    /// <summary>
    /// The <c>agent</c>/<c>enroll</c> row is decided by owner authentication (§4.3), so a
    /// tier-indexed query about it is a category error rather than a denial. Reported as a failure
    /// so the caller routes to enrollment instead of logging an authorization denial that would
    /// misdescribe what happened.
    /// </summary>
    [Theory]
    [InlineData(PrincipalTier.Anonymous)]
    [InlineData(PrincipalTier.T0)]
    [InlineData(PrincipalTier.T3)]
    public void Enrollment_is_not_a_tier_decision(PrincipalTier tier)
    {
        var result = AccessPolicy.Decide(Request(tier, ResourceKind.Agent, ActionKind.Enroll));

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authz/owner-authentication-required", error!.Type);
    }

    /// <summary>
    /// A pair Table 10 does not model is a gap in §7.2, reported as a failure rather than as a
    /// denial. A missing row must not be able to masquerade as a deliberate one.
    /// </summary>
    [Fact]
    public void An_unmodelled_pair_is_a_failure_not_a_denial()
    {
        var result = AccessPolicy.Decide(Request(PrincipalTier.T3, ResourceKind.Board, ActionKind.Create));

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authz/unmodelled-resource-action", error!.Type);
    }

    /// <summary>
    /// Table 10's parentheticals survive the decision. An allow carrying a qualifier is not yet a
    /// permission to act, and dropping it here would silently convert "may edit own revision" into
    /// "may edit any revision".
    /// </summary>
    [Theory]
    [InlineData(ResourceKind.Revision, ActionKind.Create, GrantQualifier.OwnResourceOnly)]
    [InlineData(ResourceKind.Answer, ActionKind.Accept, GrantQualifier.OwnThreadOnly)]
    [InlineData(ResourceKind.Moderation, ActionKind.Apply, GrantQualifier.Delegated)]
    [InlineData(ResourceKind.Comment, ActionKind.Create, GrantQualifier.None)]
    public void An_allow_carries_the_published_qualifier(
        ResourceKind resource, ActionKind action, GrantQualifier expected)
    {
        var decision = Decide(Request(PrincipalTier.T3, resource, action));

        Assert.Equal(DecisionEffect.Allow, decision.Effect);
        Assert.Equal(expected, decision.Qualifier);
    }

    /// <summary>
    /// A denial never carries a qualifier: there is no obligation attached to "no".
    ///
    /// <para>Keyed on <see cref="DecisionEffect.Deny"/> rather than on <c>!IsAllowed</c>, which was
    /// the same test until a qualified allow stopped being directly actionable. The claim here is
    /// about the effect — a refusal attaches no obligation — and <c>!IsAllowed</c> now also matches
    /// a permitted-but-qualified grant, which carries a qualifier precisely because it is one.</para>
    /// </summary>
    [Fact]
    public void A_denial_carries_no_qualifier()
    {
        foreach (var pair in ResourceActionModel.ModelledPairs)
        {
            if (pair is { Resource: ResourceKind.Agent, Action: ActionKind.Enroll }) continue;

            foreach (var tier in Enum.GetValues<PrincipalTier>())
            {
                var decision = Decide(Request(tier, pair.Resource, pair.Action));
                if (decision.Effect is DecisionEffect.Deny)
                    Assert.Equal(GrantQualifier.None, decision.Qualifier);
            }
        }
    }

    // ---- Table 10's parentheticals ------------------------------------------------------

    /// <summary>
    /// An unqualified allow is actionable as it stands — the overwhelmingly common case, and the
    /// behaviour every existing call site depends on.
    /// </summary>
    [Fact]
    public void AnUnqualifiedAllowIsActionable()
    {
        var decision = Decide(Request(PrincipalTier.T1, ResourceKind.Question, ActionKind.Create));

        Assert.Equal(GrantQualifier.None, decision.Qualifier);
        Assert.True(decision.IsPermitted);
        Assert.True(decision.IsAllowed);
    }

    /// <summary>
    /// <b>A qualified allow is not a permission to act, and now cannot be mistaken for one.</b>
    ///
    /// <para>Table 10 writes <c>revision</c>/<c>create</c> as "(own)" and <c>answer</c>/<c>accept</c>
    /// as "(own thread)". <see cref="AuthorizationDecision"/> has always carried that parenthetical
    /// and always documented that such a decision "is not yet a permission to act" — and every
    /// caller in the solution read <c>IsAllowed</c> and proceeded, because nothing stopped them.
    /// <c>IsAllowed</c> is now false while a qualifier is outstanding, so the only way past it is
    /// to answer the question the table asked.</para>
    /// </summary>
    [Theory]
    [InlineData(ResourceKind.Revision, ActionKind.Create, GrantQualifier.OwnResourceOnly)]
    [InlineData(ResourceKind.Answer, ActionKind.Accept, GrantQualifier.OwnThreadOnly)]
    public void AQualifiedAllowIsNotActionableUntilDischarged(
        ResourceKind resource, ActionKind action, GrantQualifier expected)
    {
        var decision = Decide(Request(PrincipalTier.T1, resource, action));

        Assert.Equal(expected, decision.Qualifier);
        Assert.True(decision.IsPermitted);
        Assert.False(decision.IsAllowed);
    }

    /// <summary>Discharging a satisfied qualifier yields an actionable decision.</summary>
    [Fact]
    public void DischargingASatisfiedQualifierYieldsAnActionableAllow()
    {
        var discharged = Decide(Request(PrincipalTier.T1, ResourceKind.Answer, ActionKind.Accept))
            .Discharge(satisfied: true);

        Assert.True(discharged.IsAllowed);
        Assert.Equal(GrantQualifier.None, discharged.Qualifier);
    }

    /// <summary>
    /// An unsatisfied qualifier denies, and the reason names the parenthetical rather than reporting
    /// a bare refusal — R7.16 logs denials at the fidelity of allows, and "denied" alone cannot tell
    /// an operator that the caller was the wrong agent rather than the wrong tier.
    /// </summary>
    [Fact]
    public void AnUnsatisfiedQualifierDeniesAndSaysWhich()
    {
        var denied = Decide(Request(PrincipalTier.T1, ResourceKind.Answer, ActionKind.Accept))
            .Discharge(satisfied: false);

        Assert.False(denied.IsAllowed);
        Assert.False(denied.IsPermitted);
        Assert.Contains("own-thread", denied.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Discharging a denial cannot produce an allow. A qualifier is a further restriction on a
    /// permission, never a way to acquire one — the caller establishing ownership of a resource it
    /// was never permitted to touch must still be refused.
    /// </summary>
    [Fact]
    public void DischargingADenialCannotManufactureAnAllow()
    {
        var denied = Decide(Request(PrincipalTier.Anonymous, ResourceKind.Answer, ActionKind.Accept));

        Assert.False(denied.IsPermitted);
        Assert.False(denied.Discharge(satisfied: true).IsAllowed);
    }

    /// <summary>
    /// <b>A qualified write still spends the posting budget.</b>
    ///
    /// <para>The regression this guards is subtle and was live while the change was being made:
    /// <see cref="AccessPolicy"/> tests the tier decision internally to decide whether a budget
    /// applies, and if that internal test had been left reading <c>IsAllowed</c> — now false for a
    /// qualified row — a revision would have become the one write with no rate limit. "The table
    /// permitted this" and "the caller may act on this" are two questions, and this is the one place
    /// that has to ask the first.</para>
    /// </summary>
    [Fact]
    public void AQualifiedWriteStillSpendsTheRateBudget()
    {
        var overBudget = Decide(Request(
            PrincipalTier.T1,
            ResourceKind.Revision,
            ActionKind.Create,
            postsToday: TierPolicy.PostsPerDay(PrincipalTier.T1)));

        Assert.False(overBudget.IsPermitted);
        Assert.Equal("table-11/rate-budget-exhausted", overBudget.Reason);
    }
}
