using Curia.Domain.Credentials;
using Curia.Domain.Primitives;

namespace Curia.Domain.Authorization;

/// <summary>What the PDP answered.</summary>
public enum DecisionEffect
{
    /// <summary>Denied. R7.16: logged with the same fidelity as an allow.</summary>
    Deny,

    /// <summary>Permitted.</summary>
    Allow,
}

/// <summary>
/// A decision, with the rule that produced it. R7.16 requires denials to be logged at the same
/// fidelity as allows, which is only possible if the denial carries why -- a bare <c>false</c>
/// makes "denied by Table 10" and "denied because quarantined" indistinguishable in the audit
/// trail, and those two want very different operational responses.
/// </summary>
/// <param name="Effect">Allow or deny.</param>
/// <param name="Reason">The rule that decided it, as a stable slug.</param>
/// <param name="Qualifier">
/// On an allow, the obligation the caller still owes -- Table 10's "(own)", "(own thread)" and
/// "(delegated)" parentheticals, which cannot be evaluated without the concrete resource instance.
/// An allow carrying anything but <see cref="GrantQualifier.None"/> is not yet a permission to act.
/// </param>
public sealed record AuthorizationDecision(DecisionEffect Effect, string Reason, GrantQualifier Qualifier)
{
    public bool IsAllowed => Effect is DecisionEffect.Allow;
}

/// <summary>
/// The request a PDP answers: §7's inputs reduced to what a decision needs at this layer.
/// </summary>
/// <param name="Tier">
/// R7.7: "computed from live state at decision time, never read solely from a token claim."
/// Typed as <see cref="EvaluatedTier"/> rather than <see cref="PrincipalTier"/> so the requirement
/// is a construction rule instead of a convention -- an <see cref="EvaluatedTier"/> comes only out
/// of <see cref="TierPolicy.Evaluate"/> or <see cref="EvaluatedTier.Anonymous"/>, so a request
/// built from a JWT claim does not compile.
/// </param>
/// <param name="CredentialState">
/// Table 6's state. Present because Appendix F.1's quarantine rule keys on it and it overrides
/// tier entirely.
/// </param>
/// <param name="PostsToday">
/// How many posts the principal has made in the trailing budget window. R7.15 requires the PDP's
/// context to include "recent post rate", and this is it.
///
/// <para><b>A count rather than a boolean, deliberately.</b> The predecessor passed
/// <c>RateBudgetAvailable</c>, which made the caller decide whether the budget was exhausted —
/// so the caller had to know Table 11's numbers, and the policy could not be checked against the
/// published table. The observation belongs to the caller; the comparison belongs here.</para>
/// </param>
public sealed record AuthorizationRequest(
    EvaluatedTier Tier,
    CredentialState CredentialState,
    ResourceKind Resource,
    ActionKind Action,
    int PostsToday = 0);

/// <summary>
/// §7's decision, composed: Table 6's credential state first, then Table 10's cell. Pure, total
/// over its inputs, and dependent on nothing outside the BCL (R11.1) -- there is no clock here, no
/// store, and no policy engine. <c>Curia.Application</c>'s <c>IPolicyDecisionPoint</c> is the port
/// an engine plugs into; this is the model an engine has to agree with.
/// </summary>
public static class AccessPolicy
{
    /// <summary>
    /// Table 11's "Read only" capability for a quarantined credential, as the set of Table 10
    /// actions that are reads.
    ///
    /// <para><b>The one place Table 11 and Appendix F.1 disagree, and why Table 11 governs.</b>
    /// Appendix F.1 writes the quarantine rule as <c>forbid ... when { principal.state ==
    /// "quarantined" &amp;&amp; action != Action::"read" }</c> -- literally the single action
    /// <c>read</c>. Table 11's Quarantined row says "Read only" with a budget of "10 reads/min".
    /// Read literally, F.1 would deny a quarantined agent <c>board:list</c> and
    /// <c>thread:search</c>, both of which Table 10 grants to <b>Anonymous</b>.</para>
    ///
    /// <para>That reading makes quarantine strictly worse than presenting no credential at all, so
    /// an agent could <i>gain</i> capability by discarding its credential and coming back
    /// anonymous -- which would make quarantine not merely useless but an incentive to shed
    /// identity, the opposite of what a posture trip is for. Appendix F is titled "Policy
    /// examples" and is illustrative; Table 11 is the normative statement of what the state can
    /// do. Table 11 governs, and "read" in F.1 is read as the class of read actions.</para>
    ///
    /// <para>Recorded here rather than silently patched, in the same spirit as
    /// <see cref="CredentialLifecycle"/>'s D9.5 note -- the literal reading is rejected above, not
    /// merely unconsidered.</para>
    /// </summary>
    internal static bool IsRead(ActionKind action) => ActionKinds.IsRead(action);

    /// <summary>
    /// Whether an action spends the posting budget.
    ///
    /// <para>Everything that is not a read, minus <see cref="ActionKind.Enroll"/> -- which is decided
    /// by owner authentication rather than by tier, and which an agent performs before it has a tier
    /// whose budget could be spent. Defined as the complement of a read rather than as a list of
    /// writes, so an action added to Table 10 is budgeted by default: forgetting to add one to a
    /// list yields an unbounded action, while the complement's failure mode is an action that costs
    /// budget it should not -- which an author notices and reports.</para>
    /// </summary>
    private static bool IsWrite(ActionKind action) =>
        !ActionKinds.IsRead(action) && action is not ActionKind.Enroll;

    /// <summary>
    /// R7.6: anonymous read is an explicit <c>allow</c> decided by this function, never the absence
    /// of a check. There is no path through <see cref="Decide"/> that returns an allow without
    /// having consulted Table 10 -- the default is <see cref="DecisionEffect.Deny"/> and every
    /// allow names the cell that produced it.
    /// </summary>
    public static Result<AuthorizationDecision> Decide(AuthorizationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return ResourceActionModel.RowFor(request.Resource, request.Action)
            .Bind(row => Evaluate(request, row));
    }

    private static Result<AuthorizationDecision> Evaluate(AuthorizationRequest request, ResourceActionRow row)
    {
        var cell = row[request.Tier.Tier];

        // The `agent`/`enroll` row. Answered before anything else because it is not a
        // tier-indexed question at all, so neither the quarantine rule nor the Table 10 cell
        // below is the right thing to apply to it.
        if (cell is Table10Cell.OwnerAuthOnly)
            return Result<AuthorizationDecision>.Fail(
                AuthorizationErrors.OwnerAuthenticationRequired(request.Resource, request.Action));

        // Table 11's "Rate budget" column, applied to every write rather than only to Table 10's
        // one "rate-limited" cell.
        //
        // I read this the other way first, and the earlier reading was wrong in a way that matters.
        // Table 10 marks T0's `question`/`create` cell "rate-limited" and gives T1 and above a plain
        // tick, which I took to mean the budget was that cell's business alone. But Table 11 gives
        // *every* tier a posting budget -- 3/day at T0, 25 at T1, 100 at T2 -- and a budget nothing
        // consults is decoration. Under the old reading a T1 agent could post without limit forever,
        // which is one agent away from flooding the Forum.
        //
        // The two tables say different things and both hold: Table 11 caps how much a tier may post,
        // and Table 10's "rate-limited" cell marks the one place where the budget is the *deciding*
        // factor for whether the action is permitted at all rather than an upper bound on it.
        var tierDecision = FromCell(cell, row.Qualifier, rateBudgetAvailable: true);

        // Order matters, and getting it wrong produces a misleading refusal. The budget *bounds a
        // permission*; it cannot manufacture one, and it cannot explain the absence of one. So the
        // Table 10 cell decides first: an anonymous principal asking to post is refused because the
        // table never permitted it, not because it exhausted a budget of zero. R7.16 wants those two
        // distinguishable, and "you have hit your limit" told to someone who will never be allowed
        // is worse than unhelpful -- it implies waiting would work.
        //
        // Caught by a test asserting the anonymous denial's reason, which is the only reason this
        // is right rather than plausible.
        if (tierDecision.IsAllowed
            && IsWrite(request.Action)
            && request.PostsToday >= TierPolicy.PostsPerDay(request.Tier.Tier))
            return Result<AuthorizationDecision>.Ok(new AuthorizationDecision(
                DecisionEffect.Deny, "table-11/rate-budget-exhausted", GrantQualifier.None));

        // Appendix F.1: "Quarantine dominates everything." Applied as an intersection with the
        // tier's own answer, never as a grant in its own right: a quarantined principal gets the
        // reads its tier already had, and nothing else. Written as a restriction rather than as a
        // separate allow so that no future Table 10 edit can make quarantine the more capable
        // state -- the property is structural here, not something a test has to keep watching.
        if (request.CredentialState is CredentialState.Quarantined)
            return Result<AuthorizationDecision>.Ok(
                tierDecision.IsAllowed && IsRead(request.Action)
                    ? tierDecision with { Reason = "table-11/quarantined-read-only" }
                    : new AuthorizationDecision(
                        DecisionEffect.Deny, "table-11/quarantined-read-only", GrantQualifier.None));

        return Result<AuthorizationDecision>.Ok(tierDecision);
    }

    private static AuthorizationDecision FromCell(Table10Cell cell, GrantQualifier qualifier, bool rateBudgetAvailable) =>
        cell switch
        {
            Table10Cell.Allowed => new AuthorizationDecision(DecisionEffect.Allow, "table-10/permitted", qualifier),

            // Table 10's single "rate-limited" cell. A permit whose grant is conditional, so an
            // exhausted budget denies with its own reason rather than reading as a tier denial --
            // R7.16's fidelity requirement again: these two want different operational responses.
            Table10Cell.RateLimited => rateBudgetAvailable
                ? new AuthorizationDecision(DecisionEffect.Allow, "table-10/rate-limited", qualifier)
                : new AuthorizationDecision(
                    DecisionEffect.Deny, "table-11/rate-budget-exhausted", GrantQualifier.None),

            Table10Cell.Denied => new AuthorizationDecision(DecisionEffect.Deny, "table-10/denied", GrantQualifier.None),

            // Named explicitly rather than left to the discard arm: IDE0072 is escalated to a
            // build error, so a new Table10Cell member fails the build here instead of silently
            // reading as "denied". Reached only if Evaluate's guard is ever removed, and throwing
            // makes that a loud failure rather than an accidental owner-auth denial.
            Table10Cell.OwnerAuthOnly => throw new ArgumentOutOfRangeException(
                nameof(cell), cell, "owner-auth rows are decided before the cell switch"),

            // CS8524: a C# enum is not sealed to its named members, so even a switch listing all
            // four still needs an arm for a value with no name at all (e.g. an invalid cast).
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, "Not a Table 10 cell"),
        };
}
