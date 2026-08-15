namespace Curia.Domain.Credentials;

/// <summary>
/// The "Entered by" column of Table 6 (whitepaper §4.5), closed: an edge not named here cannot be
/// encoded as a cell in <see cref="CredentialLifecycle"/>'s table, which is the point of a closed
/// trigger vocabulary -- a caller cannot invent a ninth cause and have it silently accepted as a
/// tenth.
///
/// Several triggers below are reused across more than one Table 6 cell because the published table
/// itself repeats the same "Entered by" prose across more than one row's incoming edges (e.g. the
/// <c>suspended</c> row's entry text is identical regardless of whether the previous state was
/// <c>active</c> or <c>quarantined</c>). The distinction between "a moderator flagged this," "an
/// anomaly detector flagged this," and "the owner asked for it" belongs on
/// <see cref="TransitionReason"/>'s free text on the resulting
/// <see cref="CredentialTransitionedEvent"/>, not in a proliferation of near-duplicate triggers that
/// Table 6 itself does not distinguish.
/// </summary>
public enum CredentialTrigger
{
    /// <summary><c>pending</c> → <c>active</c>. Table 6's <c>active</c> row, "Entered by": "Successful enrollment".</summary>
    SuccessfulEnrollment,

    /// <summary>
    /// <c>pending</c> → <c>expired</c>. Not named in Table 6's "Entered by" column -- <c>expired</c>
    /// has no row of its own; see <see cref="CredentialState.Expired"/>'s remarks for why this edge
    /// is modeled anyway. Named for R4.10's 15-minute enrollment-code TTL, the only documented
    /// mechanism that could produce it.
    /// </summary>
    EnrollmentTicketExpiry,

    /// <summary>
    /// → <c>suspended</c>, from <c>active</c> or <c>quarantined</c>. Table 6's <c>suspended</c> row,
    /// "Entered by": "Moderation action, anomaly trip, owner action" -- one trigger for all three;
    /// see the type-level remarks.
    /// </summary>
    Suspend,

    /// <summary>
    /// → <c>retired</c> (terminal), from <c>active</c> or <c>suspended</c>. Table 6's <c>retired</c>
    /// row, "Entered by": "Owner action, inactivity policy".
    /// </summary>
    Retire,

    /// <summary>
    /// <c>active</c> → <c>compromised</c> (terminal). Table 6's <c>compromised</c> row, "Entered by":
    /// "Key compromise declaration".
    /// </summary>
    KeyCompromiseDeclaration,

    /// <summary>
    /// <c>active</c> → <c>quarantined</c>. Table 6's <c>quarantined</c> row, "Entered by": "Automated
    /// posture trip" -- verbatim the same text the published table already assigns to this edge.
    /// <b>This is the D9.5 cell.</b> v1.0/errata's <c>active</c> row omits <c>quarantined</c> from
    /// its own "Exits to" list even though this row already states how a credential arrives here.
    /// See <see cref="CredentialLifecycle"/>'s remarks for the decision and its reasoning.
    /// </summary>
    AutomatedPostureTrip,

    /// <summary>
    /// <c>suspended</c> → <c>active</c>. Table 6's <c>suspended</c> row, "Exits to": "<c>active</c>
    /// (on review)" -- the parenthetical is the only "Entered by"-style text Table 6 gives for
    /// re-entering <c>active</c>, and it names a human review step.
    /// </summary>
    ReinstatementAfterReview,

    /// <summary>
    /// <c>quarantined</c> → <c>active</c>. Table 6's <c>quarantined</c> row, "Exits to": plain
    /// "<c>active</c>", with no "(on review)" annotation -- unlike
    /// <see cref="ReinstatementAfterReview"/>, nothing in the table implies a human review step here,
    /// consistent with quarantine's own entry being automated
    /// (<see cref="AutomatedPostureTrip"/>).
    /// </summary>
    PostureClearance,
}
