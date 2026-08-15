using System.Collections.Frozen;
using Curia.Domain.Primitives;

namespace Curia.Domain.Credentials;

/// <summary>
/// CS-12: Table 6 (whitepaper §4.5, "Credential lifecycle states") as data, not scattered
/// <c>if</c>s. <see cref="Table"/> is meant to be read cell by cell against the published table:
///
/// <code>
/// | State       | Entered by                                    | Exits to                                       |
/// |-------------|------------------------------------------------|--------------------------------------------------|
/// | pending     | Enrollment ticket created                       | active, expired                                  |
/// | active      | Successful enrollment                           | suspended, retired, compromised, quarantined (*) |
/// | suspended   | Moderation action, anomaly trip, owner action   | active (on review), retired                      |
/// | quarantined | Automated posture trip                          | active, suspended                                |
/// | retired     | Owner action, inactivity policy                 | terminal                                         |
/// | compromised | Key compromise declaration                      | terminal                                         |
/// | expired (+) | Enrollment ticket TTL lapsed unused             | terminal                                         |
/// </code>
///
/// (*) <b>D9.5.</b> The published table's <c>active</c> row exits to <c>suspended, retired,
/// compromised</c> only -- it omits <c>quarantined</c>, even though the <c>quarantined</c> row's
/// own "Entered by" cell ("Automated posture trip") already describes exactly this edge, and
/// nothing else in either document names any other predecessor <c>quarantined</c> could have.
/// Errata D9.5 records the inconsistency but reaches no fix for it ("Corrections carrying no
/// requirement change ... none affects an implementation of §6" -- credentials are §4, so D9.5
/// leaves the actual resolution to whoever implements §4).
///
/// <b>Decision:</b> the <c>active</c> row's "Exits to" list is the defect, not the
/// <c>quarantined</c> row's "Entered by" cell -- add <c>(active, AutomatedPostureTrip) →
/// quarantined</c> as a real cell in the table below. The alternative reading -- that
/// <c>quarantined</c> is simply unreachable and its own "Entered by"/"Exits to" cells are
/// aspirational text nobody is meant to implement -- would leave a published, non-terminal state
/// with no way to ever be entered by anything. That is a strictly worse defect than a one-cell
/// omission, and not a credible reading of a table whose every other row is internally consistent
/// (every other row's "Exits to" list is exactly the set of rows whose "Entered by" cell names it
/// as a source). Recorded here, in the table itself, rather than silently patched, per the Stage A
/// brief -- the alternative reading is rejected above, not merely unconsidered.
///
/// (+) <see cref="CredentialState.Expired"/>'s remarks cover why this row exists at all despite
/// not appearing in the published table.
///
/// <see cref="CredentialState.Retired"/>, <see cref="CredentialState.Compromised"/>, and
/// <see cref="CredentialState.Expired"/> are absorbing: no cell in <see cref="Table"/> has any of
/// the three as its "from" state, for any trigger.
/// </summary>
public static class CredentialLifecycle
{
    /// <summary>
    /// Table 6 itself: every legal (state, trigger) → state cell, and only those. A lookup miss is
    /// Table 6 saying "no such exit," which <see cref="Transition"/> turns into a
    /// <see cref="Result{T}"/> failure (CS-10) rather than an exception or a silent no-op.
    /// </summary>
    private static readonly FrozenDictionary<(CredentialState From, CredentialTrigger Trigger), CredentialState> Table =
        new Dictionary<(CredentialState, CredentialTrigger), CredentialState>
        {
            // pending -- "Entered by: Enrollment ticket created"; exits to: active, expired.
            [(CredentialState.Pending, CredentialTrigger.SuccessfulEnrollment)] = CredentialState.Active,
            [(CredentialState.Pending, CredentialTrigger.EnrollmentTicketExpiry)] = CredentialState.Expired,

            // active -- "Entered by: Successful enrollment";
            // exits to: suspended, retired, compromised, quarantined (D9.5, see type remarks).
            [(CredentialState.Active, CredentialTrigger.Suspend)] = CredentialState.Suspended,
            [(CredentialState.Active, CredentialTrigger.Retire)] = CredentialState.Retired,
            [(CredentialState.Active, CredentialTrigger.KeyCompromiseDeclaration)] = CredentialState.Compromised,
            [(CredentialState.Active, CredentialTrigger.AutomatedPostureTrip)] = CredentialState.Quarantined,

            // suspended -- "Entered by: Moderation action, anomaly trip, owner action";
            // exits to: active (on review), retired.
            [(CredentialState.Suspended, CredentialTrigger.ReinstatementAfterReview)] = CredentialState.Active,
            [(CredentialState.Suspended, CredentialTrigger.Retire)] = CredentialState.Retired,

            // quarantined -- "Entered by: Automated posture trip"; exits to: active, suspended.
            [(CredentialState.Quarantined, CredentialTrigger.PostureClearance)] = CredentialState.Active,
            [(CredentialState.Quarantined, CredentialTrigger.Suspend)] = CredentialState.Suspended,

            // retired, compromised, expired -- terminal: no cells.
        }.ToFrozenDictionary();

    /// <summary>The single pure function the table exists to define. CS-12's shape, verbatim.</summary>
    public static Result<CredentialState> Transition(CredentialState from, CredentialTrigger trigger) =>
        Table.TryGetValue((from, trigger), out var to)
            ? Result<CredentialState>.Ok(to)
            : Result<CredentialState>.Fail(CredentialErrors.IllegalTransition(from, trigger));

    /// <summary>
    /// R4.21: "the current state is a projection." Folds a credential's full event history
    /// through <see cref="Transition"/>, starting from <see cref="CredentialState.Pending"/> (see
    /// its remarks for why that is the correct fold seed), and fails on the first event that is
    /// not a legal exit of the state the fold has reached so far -- an illegal transition that
    /// somehow made it into the log is a <see cref="Result{T}"/> failure here too, never a thrown
    /// exception and never a value silently produced by skipping the bad event.
    ///
    /// There is deliberately no cached "current state" this method could return instead of
    /// recomputing: every call re-derives the answer from <paramref name="history"/> alone. That
    /// is what lets R4.22's "next request, not next token expiry" hold with no extra machinery in
    /// this layer -- a caller that re-projects on every request always sees whatever the log says
    /// right now, because there is nothing else here that could go stale.
    /// </summary>
    public static Result<CredentialState> Project(IReadOnlyList<CredentialTransitionedEvent> history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var state = Result<CredentialState>.Ok(CredentialState.Pending);
        foreach (var @event in history)
            state = state.Bind(current => Transition(current, @event.Trigger));

        return state;
    }
}
