namespace Curia.Domain.Credentials;

/// <summary>
/// Table 6 (whitepaper §4.5, "Credential lifecycle states"), the closed set of rows.
/// <see cref="Expired"/> is not a row in the published table -- see its own remarks below, and
/// <see cref="CredentialLifecycle"/>'s remarks for the D9.5 decision governing how these states
/// connect.
///
/// <see cref="Pending"/> is deliberately the enum's zero value: a credential aggregate with no
/// recorded transitions yet *is* pending. Table 6's <c>pending</c> row is the table's own entry
/// point -- unlike every other row, its "Entered by" cell ("Enrollment ticket created") describes
/// how the aggregate itself comes to exist, not a transition from some other row in the table. So
/// <see cref="CredentialLifecycle.Project"/> uses <c>default(CredentialState)</c> (i.e.
/// <see cref="Pending"/>) as its fold seed rather than special-casing aggregate creation as a
/// transition out of a synthetic tenth state that Table 6 does not have.
/// </summary>
public enum CredentialState
{
    /// <summary>Entered by: enrollment ticket created (Figure 4 step 2). Cannot authenticate; cannot post.</summary>
    Pending = 0,

    /// <summary>Entered by: successful enrollment. Can authenticate; can post, per tier (§7.3).</summary>
    Active,

    /// <summary>Entered by: moderation action, anomaly trip, or owner action. Cannot authenticate; cannot post.</summary>
    Suspended,

    /// <summary>Entered by: automated posture trip. Can authenticate for read scopes only; cannot post.</summary>
    Quarantined,

    /// <summary>
    /// Entered by: owner action or inactivity policy. Terminal.  Cannot authenticate; cannot post.
    /// </summary>
    Retired,

    /// <summary>
    /// Entered by: key compromise declaration. Terminal. Cannot authenticate; cannot post.
    /// R4.23: kept a distinct value from <see cref="Retired"/> forever, never collapsed into a
    /// shared "deactivated" state -- the two have different consequences for previously published
    /// content (§6.6), and the public record must be able to tell them apart.
    /// </summary>
    Compromised,

    /// <summary>
    /// Not a row in the published Table 6. It is named exactly once in the whole document, as an
    /// exit of the <c>pending</c> row ("Exits to: <c>active</c>, <c>expired</c>"), with no
    /// "Entered by", "Can authenticate?", "Can post?", or "Exits to" cell of its own anywhere.
    ///
    /// Decision: model it as a seventh, terminal state reachable only from <see cref="Pending"/>
    /// (<see cref="CredentialTrigger.EnrollmentTicketExpiry"/>, R4.10's 15-minute enrollment-code
    /// TTL) rather than omit it. Omitting a state Table 6 itself names as a legal destination would
    /// make this table diverge from the published one at the one place a reviewer is most likely to
    /// check (the <c>pending</c> row); treating it exactly like the two states Table 6 *does* give a
    /// full row to and explicitly marks terminal (<see cref="Retired"/>, <see cref="Compromised"/>)
    /// is the only reading consistent with <c>expired</c> having zero listed exits anywhere in the
    /// document. Flagged in the Stage A report as a second, independent table gap alongside D9.5 --
    /// unlike D9.5, no errata entry covers it, so this decision has no numbered erratum to point at.
    /// </summary>
    Expired,
}
