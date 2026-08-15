using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Credentials;
using Xunit;

namespace Curia.Domain.Tests.Credentials;

/// <summary>
/// Cell-by-cell coverage of Table 6 (whitepaper §4.5) as encoded in
/// <see cref="CredentialLifecycle"/>. <see cref="LegalCells"/> below is the single source of truth
/// both the "every legal transition succeeds" and "every other combination is rejected" theories
/// are generated from, so the two enumerations can never drift out of sync with each other -- only
/// with Table 6 itself, which a reviewer checks by diffing this list against the published table
/// and <see cref="CredentialLifecycle"/>'s own remarks (including the D9.5 decision).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "One test name below carries the errata identifier (D9.5) it pins verbatim, " +
        "mirroring LayeringTests'/CanonProperties' precedent of R-number/CS-number test names.")]
public sealed class CredentialLifecycleTests
{
    private static readonly (CredentialState From, CredentialTrigger Trigger, CredentialState To)[] LegalCells =
    [
        // pending -- "Entered by: Enrollment ticket created"; exits to: active, expired.
        (CredentialState.Pending, CredentialTrigger.SuccessfulEnrollment, CredentialState.Active),
        (CredentialState.Pending, CredentialTrigger.EnrollmentTicketExpiry, CredentialState.Expired),

        // active -- exits to: suspended, retired, compromised, quarantined (D9.5).
        (CredentialState.Active, CredentialTrigger.Suspend, CredentialState.Suspended),
        (CredentialState.Active, CredentialTrigger.Retire, CredentialState.Retired),
        (CredentialState.Active, CredentialTrigger.KeyCompromiseDeclaration, CredentialState.Compromised),
        (CredentialState.Active, CredentialTrigger.AutomatedPostureTrip, CredentialState.Quarantined),

        // suspended -- exits to: active (on review), retired.
        (CredentialState.Suspended, CredentialTrigger.ReinstatementAfterReview, CredentialState.Active),
        (CredentialState.Suspended, CredentialTrigger.Retire, CredentialState.Retired),

        // quarantined -- exits to: active, suspended.
        (CredentialState.Quarantined, CredentialTrigger.PostureClearance, CredentialState.Active),
        (CredentialState.Quarantined, CredentialTrigger.Suspend, CredentialState.Suspended),

        // retired, compromised, expired -- terminal: no cells.
    ];

    public static TheoryData<CredentialState, CredentialTrigger, CredentialState> LegalTransitionCases()
    {
        var data = new TheoryData<CredentialState, CredentialTrigger, CredentialState>();
        foreach (var (from, trigger, to) in LegalCells)
            data.Add(from, trigger, to);
        return data;
    }

    public static TheoryData<CredentialState, CredentialTrigger> IllegalTransitionCases()
    {
        var legalPairs = LegalCells.Select(c => (c.From, c.Trigger)).ToHashSet();
        var data = new TheoryData<CredentialState, CredentialTrigger>();
        foreach (var from in Enum.GetValues<CredentialState>())
            foreach (var trigger in Enum.GetValues<CredentialTrigger>())
                if (!legalPairs.Contains((from, trigger)))
                    data.Add(from, trigger);
        return data;
    }

    [Fact]
    public void TheTableHasExactlyTenCellsWithNoDuplicates()
    {
        Assert.Equal(10, LegalCells.Length);
        Assert.Equal(LegalCells.Length, LegalCells.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(LegalTransitionCases))]
    public void EveryLegalTransitionSucceeds(CredentialState from, CredentialTrigger trigger, CredentialState expectedTo) =>
        Assert.Equal(expectedTo, TestSupport.Require(CredentialLifecycle.Transition(from, trigger)));

    [Theory]
    [MemberData(nameof(IllegalTransitionCases))]
    public void EveryOtherStateTriggerCombinationIsRejected(CredentialState from, CredentialTrigger trigger)
    {
        var result = CredentialLifecycle.Transition(from, trigger);
        Assert.False(result.IsOk);
        result.Match(_ => throw new InvalidOperationException("expected failure"), e =>
        {
            Assert.Equal("curia/domain/credential/illegal-transition", e.Type);
            return 0;
        });
    }

    [Theory]
    [InlineData(CredentialState.Retired)]
    [InlineData(CredentialState.Compromised)]
    [InlineData(CredentialState.Expired)]
    public void TerminalStatesAcceptNoTrigger(CredentialState terminal)
    {
        foreach (var trigger in Enum.GetValues<CredentialTrigger>())
            Assert.False(CredentialLifecycle.Transition(terminal, trigger).IsOk);
    }

    /// <summary>
    /// D9.5: the published table's <c>active</c> row omits <c>quarantined</c> from its "Exits to"
    /// list even though the <c>quarantined</c> row's own "Entered by" cell already names this edge
    /// ("Automated posture trip"). Pinned as its own named test, not just a row in
    /// <see cref="LegalCells"/>, because this is the one cell in the whole table that is not a
    /// direct transcription of the published document -- see <see cref="CredentialLifecycle"/>'s
    /// remarks for the decision and why the alternative reading (quarantined is simply unreachable)
    /// was rejected rather than merely unconsidered.
    /// </summary>
    [Fact]
    public void D9_5_ActiveExitsToQuarantinedOnAutomatedPostureTrip() =>
        Assert.Equal(
            CredentialState.Quarantined,
            TestSupport.Require(CredentialLifecycle.Transition(CredentialState.Active, CredentialTrigger.AutomatedPostureTrip)));
}
