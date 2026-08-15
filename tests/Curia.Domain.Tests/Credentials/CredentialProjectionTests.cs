using Curia.Domain.Credentials;
using Xunit;

namespace Curia.Domain.Tests.Credentials;

/// <summary>
/// R4.21: "the current state is a projection." These tests exercise
/// <see cref="CredentialLifecycle.Project"/> directly -- there is no stored-state field anywhere
/// in this module for a test to instead read off an aggregate.
/// </summary>
public sealed class CredentialProjectionTests
{
    [Fact]
    public void EmptyHistoryProjectsToPending() =>
        Assert.Equal(CredentialState.Pending, TestSupport.Require(CredentialLifecycle.Project([])));

    [Fact]
    public void LegalHistoryReplaysSequentiallyToItsFinalState()
    {
        var history = new[]
        {
            TestSupport.Event(CredentialTrigger.SuccessfulEnrollment),     // pending -> active
            TestSupport.Event(CredentialTrigger.Suspend),                  // active -> suspended
            TestSupport.Event(CredentialTrigger.ReinstatementAfterReview), // suspended -> active
        };

        Assert.Equal(CredentialState.Active, TestSupport.Require(CredentialLifecycle.Project(history)));
    }

    [Fact]
    public void ProjectionFailsOnTheFirstIllegalEventInHistory()
    {
        // active has no ReinstatementAfterReview exit -- only suspended does (Table 6).
        var history = new[]
        {
            TestSupport.Event(CredentialTrigger.SuccessfulEnrollment),     // pending -> active (legal)
            TestSupport.Event(CredentialTrigger.ReinstatementAfterReview), // active -> ??? (illegal)
        };

        var result = CredentialLifecycle.Project(history);
        Assert.False(result.IsOk);
        result.Match(_ => throw new InvalidOperationException("expected failure"), e =>
        {
            Assert.Equal("curia/domain/credential/illegal-transition", e.Type);
            return 0;
        });
    }

    /// <summary>
    /// "Two different event orders that should converge do" (Stage A brief). Read here as two
    /// different, structurally unrelated *routes* through Table 6 landing on the same state --
    /// literally permuting one fixed multiset of triggers is not a meaningful operation on a
    /// directed state machine the way it would be for commuting field updates on an event-sourced
    /// aggregate, so the interesting invariant instead is that <c>active</c> is reachable again
    /// from two different recovery paths: the moderation route (suspend, then reinstate on review)
    /// and the automated route (posture trip, then automated clearance) -- both legal, both
    /// converging on the same projected state.
    /// </summary>
    [Fact]
    public void DifferentRecoveryPathsConvergeOnTheSameState()
    {
        var viaSuspension = CredentialLifecycle.Project(
        [
            TestSupport.Event(CredentialTrigger.SuccessfulEnrollment),
            TestSupport.Event(CredentialTrigger.Suspend),
            TestSupport.Event(CredentialTrigger.ReinstatementAfterReview),
        ]);

        var viaQuarantine = CredentialLifecycle.Project(
        [
            TestSupport.Event(CredentialTrigger.SuccessfulEnrollment),
            TestSupport.Event(CredentialTrigger.AutomatedPostureTrip),
            TestSupport.Event(CredentialTrigger.PostureClearance),
        ]);

        Assert.Equal(CredentialState.Active, TestSupport.Require(viaSuspension));
        Assert.Equal(CredentialState.Active, TestSupport.Require(viaQuarantine));
    }

    [Fact]
    public void RetiredAndCompromisedAreReachableAndStayDistinct()
    {
        var retired = CredentialLifecycle.Project(
        [
            TestSupport.Event(CredentialTrigger.SuccessfulEnrollment),
            TestSupport.Event(CredentialTrigger.Retire),
        ]);

        var compromised = CredentialLifecycle.Project(
        [
            TestSupport.Event(CredentialTrigger.SuccessfulEnrollment),
            TestSupport.Event(CredentialTrigger.KeyCompromiseDeclaration),
        ]);

        var expired = CredentialLifecycle.Project([TestSupport.Event(CredentialTrigger.EnrollmentTicketExpiry)]);

        // R4.23: retired and compromised must never collapse into the same value.
        Assert.Equal(CredentialState.Retired, TestSupport.Require(retired));
        Assert.Equal(CredentialState.Compromised, TestSupport.Require(compromised));
        Assert.Equal(CredentialState.Expired, TestSupport.Require(expired));
        Assert.NotEqual(TestSupport.Require(retired), TestSupport.Require(compromised));
    }
}
