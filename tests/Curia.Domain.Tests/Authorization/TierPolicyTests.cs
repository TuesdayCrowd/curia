using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Xunit;

namespace Curia.Domain.Tests.Authorization;

/// <summary>Table 11's entry criteria and rate budgets, and R7.7/R7.8's properties.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class TierPolicyTests
{
    private static readonly DateTimeOffset Enrolled = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Facts that satisfy T1 exactly: 7 days, 3 clean questions, owner verified.</summary>
    private static PostureFacts MeetingT1() => new(
        CredentialState.Active,
        EnrolledAt: Enrolled,
        OwnerVerified: true,
        QuestionsWithoutUpheldFlags: 3);

    /// <summary>Facts that satisfy T2 exactly: T1 plus 30 days at T1 plus 5 accepted answers.</summary>
    private static PostureFacts MeetingT2() => MeetingT1() with
    {
        ReachedT1At = Enrolled.AddDays(7),
        AcceptedAnswers = 5,
    };

    private static PrincipalTier Tier(PostureFacts facts, DateTimeOffset at) =>
        TierPolicy.Evaluate(facts, at).Tier;

    // ---- Table 11's numbers, against the published table -------------------------------------

    /// <summary>
    /// The thresholds are transcribed from Table 11's criteria column, so they are compared with
    /// it. T1's row publishes "≥ 7 days, ≥ 3 questions..."; T2's publishes "≥ 30 days at T1, ≥ 5
    /// accepted answers or ≥ 1 verified finding".
    /// </summary>
    [Fact]
    public void Published_thresholds_match_the_constants()
    {
        Assert.Equal(
            new[] { TierPolicy.T1MinimumDays, TierPolicy.T1MinimumCleanQuestions },
            PublishedTable11.Rows["T1"].Thresholds);

        Assert.Equal(
            new[]
            {
                TierPolicy.T2MinimumDaysAtT1,
                TierPolicy.T2MinimumAcceptedAnswers,
                TierPolicy.T2MinimumVerifiedFindings,
            },
            PublishedTable11.Rows["T2"].Thresholds);

        // T0 and T3 publish no numeric threshold -- "Enrollment" and "Manual grant". Asserted so
        // that a future edit adding one is a failure here rather than a criterion nothing reads.
        Assert.Empty(PublishedTable11.Rows["T0"].Thresholds);
        Assert.Empty(PublishedTable11.Rows["T3"].Thresholds);
    }

    [Theory]
    [InlineData("T0", PrincipalTier.T0)]
    [InlineData("T1", PrincipalTier.T1)]
    [InlineData("T2", PrincipalTier.T2)]
    public void Published_rate_budgets_match_the_constants(string row, PrincipalTier tier)
    {
        Assert.Equal(PublishedTable11.Rows[row].PostsPerDay, TierPolicy.PostsPerDay(tier));
        Assert.Equal(PublishedTable11.Rows[row].ReadsPerMinute, TierPolicy.ReadsPerMinute(tier));
    }

    /// <summary>
    /// T3's budget cell reads "Negotiated" -- not a number. The code must not have invented one,
    /// because inventing a number here would be inventing policy.
    /// </summary>
    [Fact]
    public void T3s_budget_is_negotiated_and_therefore_uncapped_here()
    {
        Assert.Null(PublishedTable11.Rows["T3"].PostsPerDay);
        Assert.Null(PublishedTable11.Rows["T3"].ReadsPerMinute);
        Assert.Equal(int.MaxValue, TierPolicy.PostsPerDay(PrincipalTier.T3));
    }

    /// <summary>Table 11's Quarantined row publishes "10 reads/min" and no posting budget.</summary>
    [Fact]
    public void Quarantined_budget_matches_the_published_row()
    {
        Assert.Equal(PublishedTable11.Rows["—"].ReadsPerMinute, TierPolicy.QuarantinedReadsPerMinute);
        Assert.Null(PublishedTable11.Rows["—"].PostsPerDay);
    }

    // ---- Table 11's criteria, structurally ---------------------------------------------------

    [Fact]
    public void Enrollment_alone_is_T0()
    {
        var facts = new PostureFacts(CredentialState.Active, EnrolledAt: Enrolled);
        Assert.Equal(PrincipalTier.T0, Tier(facts, Enrolled));
    }

    /// <summary>
    /// Before the credential is active there is no tier: Table 11's lowest row is entered by
    /// enrollment. Anonymous rather than T0, since T0 already confers posting rights Table 10
    /// denies the anonymous column.
    /// </summary>
    [Theory]
    [InlineData(CredentialState.Pending)]
    [InlineData(CredentialState.Suspended)]
    [InlineData(CredentialState.Retired)]
    [InlineData(CredentialState.Compromised)]
    public void A_credential_that_is_not_active_confers_no_tier(CredentialState state)
    {
        var facts = MeetingT2() with { CredentialState = state };
        Assert.Equal(PrincipalTier.Anonymous, Tier(facts, Enrolled.AddYears(1)));
    }

    /// <summary>
    /// "≥ 7 days, ≥ 3 questions with no upheld flags, owner verified" -- a conjunction, so each
    /// clause alone must be enough to withhold the promotion.
    /// </summary>
    [Fact]
    public void T1_requires_every_clause_of_its_criterion()
    {
        var at = Enrolled.AddDays(TierPolicy.T1MinimumDays);

        Assert.Equal(PrincipalTier.T1, Tier(MeetingT1(), at));

        Assert.Equal(PrincipalTier.T0, Tier(MeetingT1(), at.AddSeconds(-1)));
        Assert.Equal(PrincipalTier.T0, Tier(MeetingT1() with { QuestionsWithoutUpheldFlags = 2 }, at));
        Assert.Equal(PrincipalTier.T0, Tier(MeetingT1() with { OwnerVerified = false }, at));
    }

    /// <summary>
    /// "≥ 30 days at T1, ≥ 5 accepted answers <b>or</b> ≥ 1 verified finding, clean record". The
    /// disjunction is the part a threshold comparison cannot check, so it is checked here: either
    /// arm suffices, and neither is required.
    /// </summary>
    [Fact]
    public void T2_accepts_either_arm_of_its_disjunction()
    {
        var at = Enrolled.AddDays(7 + TierPolicy.T2MinimumDaysAtT1);

        Assert.Equal(PrincipalTier.T2, Tier(MeetingT2(), at));
        Assert.Equal(
            PrincipalTier.T2,
            Tier(MeetingT2() with { AcceptedAnswers = 0, VerifiedFindings = 1 }, at));

        // Neither arm: falls back to T1, not to T0 -- a failed promotion is not a demotion.
        Assert.Equal(
            PrincipalTier.T1,
            Tier(MeetingT2() with { AcceptedAnswers = 4, VerifiedFindings = 0 }, at));
    }

    /// <summary>
    /// "≥ 30 days <b>at T1</b>" counts from reaching T1, not from enrollment. An agent 30 days
    /// enrolled but only 1 day at T1 is not T2.
    /// </summary>
    [Fact]
    public void T2s_clock_runs_from_T1_not_from_enrollment()
    {
        var reachedT1 = Enrolled.AddDays(60);
        var facts = MeetingT2() with { ReachedT1At = reachedT1 };

        Assert.Equal(PrincipalTier.T1, Tier(facts, reachedT1.AddDays(TierPolicy.T2MinimumDaysAtT1 - 1)));
        Assert.Equal(PrincipalTier.T2, Tier(facts, reachedT1.AddDays(TierPolicy.T2MinimumDaysAtT1)));
    }

    [Fact]
    public void T3_is_a_manual_grant_and_needs_no_counts()
    {
        var facts = new PostureFacts(CredentialState.Active, EnrolledAt: Enrolled, ManuallyGrantedT3: true);
        Assert.Equal(PrincipalTier.T3, Tier(facts, Enrolled));
    }

    // ---- R7.8: demotion --------------------------------------------------------------------

    /// <summary>
    /// R7.8: "Tier SHALL be able to decrease automatically on posture degradation ... demotion
    /// SHOULD be immediate." One upheld flag breaks T2's "clean record" at the very next
    /// evaluation, with nothing to invalidate -- the tier was never stored.
    /// </summary>
    [Fact]
    public void R7_8_AnUpheldFlagDemotesImmediately()
    {
        var at = Enrolled.AddDays(7 + TierPolicy.T2MinimumDaysAtT1);

        Assert.Equal(PrincipalTier.T2, Tier(MeetingT2(), at));
        Assert.Equal(PrincipalTier.T1, Tier(MeetingT2() with { UpheldFlags = 1 }, at));
    }

    /// <summary>
    /// A manual T3 grant is not immune. R7.8 requires demotion "without human intervention", and a
    /// grant that outranked posture would be a hole in exactly that mechanism.
    /// </summary>
    [Fact]
    public void R7_8_AManualGrantDoesNotSurvivePostureDegradation()
    {
        var granted = new PostureFacts(CredentialState.Active, EnrolledAt: Enrolled, ManuallyGrantedT3: true);

        Assert.Equal(PrincipalTier.T3, Tier(granted, Enrolled));
        Assert.Equal(PrincipalTier.T0, Tier(granted with { UpheldFlags = 1 }, Enrolled));
    }

    /// <summary>
    /// R7.7's structural half: evaluation is a pure function of facts and instant, so the same
    /// inputs always give the same answer and nothing is remembered between calls. This is what
    /// makes "computed at decision time" true by construction rather than by discipline.
    /// </summary>
    [Fact]
    public void R7_7_EvaluationIsPureAndCarriesTheInstant()
    {
        var at = Enrolled.AddDays(10);
        var first = TierPolicy.Evaluate(MeetingT1(), at);
        var second = TierPolicy.Evaluate(MeetingT1(), at);

        Assert.Equal(first, second);
        Assert.Equal(at, first.EvaluatedAt);

        // And it really is a function of the instant, not of when the test runs.
        Assert.NotEqual(first.Tier, TierPolicy.Evaluate(MeetingT1(), Enrolled).Tier);
    }

    // ---- FirstSatisfiedT1At: the T2 clock, derived rather than stamped -----------------------

    /// <summary>
    /// The later of the two conditions wins, and the answer is a fact about the history rather
    /// than about when it was asked for.
    /// </summary>
    [Fact]
    public void FirstSatisfiedT1At_is_the_later_of_tenure_and_the_countable_criteria()
    {
        // Tenure binds: the questions and the verification were in on day one.
        Assert.Equal(
            Enrolled.AddDays(TierPolicy.T1MinimumDays),
            TierPolicy.FirstSatisfiedT1At(Enrolled, Enrolled.AddDays(1)));

        // The countable criteria bind: they were not all met until day twenty.
        Assert.Equal(
            Enrolled.AddDays(20),
            TierPolicy.FirstSatisfiedT1At(Enrolled, Enrolled.AddDays(20)));
    }

    /// <summary>T1 that has never been reached has no instant, in either direction.</summary>
    [Fact]
    public void FirstSatisfiedT1At_is_null_until_every_criterion_has_held()
    {
        Assert.Null(TierPolicy.FirstSatisfiedT1At(enrolledAt: null, Enrolled));
        Assert.Null(TierPolicy.FirstSatisfiedT1At(Enrolled, countableCriteriaMetAt: null));
        Assert.Null(TierPolicy.FirstSatisfiedT1At(enrolledAt: null, countableCriteriaMetAt: null));
    }

    /// <summary>
    /// The instant it returns is exactly the boundary <see cref="TierPolicy.Evaluate"/> promotes
    /// at. The two are separate code paths over the same published row, so they are asserted to
    /// agree rather than assumed to: a disagreement would present as an agent that is T1 for
    /// authorization while its T2 clock says it is not, which is the sort of bug that surfaces
    /// thirty days later.
    /// </summary>
    [Fact]
    public void FirstSatisfiedT1At_agrees_with_Evaluate_about_the_boundary()
    {
        var facts = MeetingT1();
        var reached = TierPolicy.FirstSatisfiedT1At(Enrolled, Enrolled)!.Value;

        Assert.Equal(PrincipalTier.T1, Tier(facts, reached));
        Assert.Equal(PrincipalTier.T0, Tier(facts, reached.AddTicks(-1)));
    }

    [Fact]
    public void Anonymous_is_available_without_facts()
    {
        var anonymous = EvaluatedTier.Anonymous(Enrolled);

        Assert.Equal(PrincipalTier.Anonymous, anonymous.Tier);
        Assert.Equal(Enrolled, anonymous.EvaluatedAt);
    }
}
