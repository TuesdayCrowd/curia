using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Xunit;

namespace Curia.Domain.Tests.Authorization;

/// <summary>Posture as a projection (R11.9), and R7.7's "from live state, never from a claim".</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PostureProjectorTests
{
    private static readonly DateTimeOffset Enrolled = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The reason is free text (<see cref="TransitionReason"/> validates only that it is
    /// non-empty), and nothing in the posture fold reads it -- so these tests use one constant
    /// rather than varying it, which would suggest it mattered.
    /// </summary>
    private static readonly TransitionReason AnyReason = Reason("test");

    private static TransitionReason Reason(string value)
    {
        Assert.True(TransitionReason.Create(value).TryGetValue(out var reason, out _));
        return reason;
    }

    private static CredentialTransitionedEvent Transition(CredentialTrigger trigger, DateTimeOffset at) =>
        new(trigger, null, AnyReason, at);

    private static PostureFacts Empty => new(CredentialState.Pending);

    private static PostureFacts Fold(IReadOnlyList<CredentialTransitionedEvent> history, PostureFacts? counted = null)
    {
        Assert.True(PostureProjector.Fold(history, counted ?? Empty).TryGetValue(out var facts, out var error), error?.Type);
        return facts!;
    }

    [Fact]
    public void Enrollment_sets_the_state_and_the_enrollment_instant()
    {
        var facts = Fold([Transition(CredentialTrigger.SuccessfulEnrollment, Enrolled)]);

        Assert.Equal(CredentialState.Active, facts.CredentialState);
        Assert.Equal(Enrolled, facts.EnrolledAt);
    }

    [Fact]
    public void An_empty_history_has_no_enrollment_instant()
    {
        var facts = Fold([]);

        Assert.Equal(CredentialState.Pending, facts.CredentialState);
        Assert.Null(facts.EnrolledAt);
    }

    /// <summary>
    /// A suspension and reinstatement does not restart the tenure clock. Table 11 counts "≥ 7
    /// days" from enrollment, and resetting on reinstatement would turn every suspension into a
    /// silent demotion the published table never describes.
    /// </summary>
    [Fact]
    public void Reinstatement_does_not_restart_the_tenure_clock()
    {
        var facts = Fold(
        [
            Transition(CredentialTrigger.SuccessfulEnrollment, Enrolled),
            Transition(CredentialTrigger.Suspend, Enrolled.AddDays(3)),
            Transition(CredentialTrigger.ReinstatementAfterReview, Enrolled.AddDays(5)),
        ]);

        Assert.Equal(CredentialState.Active, facts.CredentialState);
        Assert.Equal(Enrolled, facts.EnrolledAt);
    }

    /// <summary>
    /// The projected state must equal what <see cref="CredentialLifecycle.Project"/> says, always.
    /// Two disagreeing answers to "what state is this credential in" is the kind of divergence that
    /// hides behind a seam until something security-relevant reads the wrong one.
    /// </summary>
    [Fact]
    public void The_projected_state_never_disagrees_with_the_lifecycle()
    {
        CredentialTransitionedEvent[][] histories =
        [
            [],
            [Transition(CredentialTrigger.SuccessfulEnrollment, Enrolled)],
            [Transition(CredentialTrigger.EnrollmentTicketExpiry, Enrolled)],
            [
                Transition(CredentialTrigger.SuccessfulEnrollment, Enrolled),
                Transition(CredentialTrigger.AutomatedPostureTrip, Enrolled.AddDays(1)),
            ],
            [
                Transition(CredentialTrigger.SuccessfulEnrollment, Enrolled),
                Transition(CredentialTrigger.KeyCompromiseDeclaration, Enrolled.AddDays(2)),
            ],
        ];

        foreach (var history in histories)
        {
            var viaLifecycle = CredentialLifecycle.Project(history);
            Assert.True(viaLifecycle.TryGetValue(out var expected, out _));
            Assert.Equal(expected, Fold(history).CredentialState);
        }
    }

    /// <summary>An illegal history is a failure, exactly as the lifecycle reports it -- never a default posture.</summary>
    [Fact]
    public void An_illegal_history_is_a_failure()
    {
        var result = PostureProjector.Fold(
            [Transition(CredentialTrigger.Suspend, Enrolled)], Empty);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/domain/credential/illegal-transition", error!.Type);
    }

    /// <summary>
    /// R7.7: a caller cannot inject the credential state or the enrollment instant. Those are the
    /// fold's output; accepting them from the caller would be the "tier from somewhere other than
    /// the log" path the requirement forbids, wearing a projection's clothes.
    /// </summary>
    [Fact]
    public void R7_7_CallerSuppliedStateAndEnrollmentAreIgnored()
    {
        var lying = new PostureFacts(
            CredentialState.Active,
            EnrolledAt: Enrolled.AddYears(-5),
            OwnerVerified: true,
            QuestionsWithoutUpheldFlags: 99);

        var facts = Fold([Transition(CredentialTrigger.EnrollmentTicketExpiry, Enrolled)], lying);

        Assert.Equal(CredentialState.Expired, facts.CredentialState);
        Assert.Null(facts.EnrolledAt);

        // The counted facts the log genuinely cannot supply are kept.
        Assert.True(facts.OwnerVerified);
        Assert.Equal(99, facts.QuestionsWithoutUpheldFlags);
    }

    /// <summary>
    /// R11.9: folding the same history twice gives the identical result. Trivially true only
    /// because nothing here reads a clock -- which is the property worth pinning, since a single
    /// <c>DateTimeOffset.UtcNow</c> added later would break this and nothing else.
    /// </summary>
    [Fact]
    public void R11_9_TheFoldIsDeterministic()
    {
        CredentialTransitionedEvent[] history =
        [
            Transition(CredentialTrigger.SuccessfulEnrollment, Enrolled),
            Transition(CredentialTrigger.AutomatedPostureTrip, Enrolled.AddDays(1)),
            Transition(CredentialTrigger.PostureClearance, Enrolled.AddDays(2)),
        ];

        Assert.Equal(Fold(history), Fold(history));
    }

    /// <summary>
    /// End to end: a history plus counts plus an instant gives a tier, with no token anywhere in
    /// the chain. This is R7.7's sentence, executed.
    /// </summary>
    [Fact]
    public void R7_7_AtierIsReachableFromEventsAlone()
    {
        var counted = Empty with { OwnerVerified = true, QuestionsWithoutUpheldFlags = 3 };
        var facts = Fold([Transition(CredentialTrigger.SuccessfulEnrollment, Enrolled)], counted);

        var tier = TierPolicy.Evaluate(facts, Enrolled.AddDays(TierPolicy.T1MinimumDays));

        Assert.Equal(PrincipalTier.T1, tier.Tier);
    }
}
