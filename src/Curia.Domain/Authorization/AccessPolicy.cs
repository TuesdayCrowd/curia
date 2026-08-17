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
/// R7.7: computed from live state at decision time, never read solely from a token claim. This
/// type cannot enforce that -- it receives whatever it is handed -- so Stage 2's posture
/// projection is what makes the requirement true; here it is only stated.
/// </param>
/// <param name="CredentialState">
/// Table 6's state. Present because Appendix F.1's quarantine rule keys on it and it overrides
/// tier entirely.
/// </param>
/// <param name="RateBudgetAvailable">
/// Whether the principal is within Table 11's rate budget, which is R7.15's
/// <c>context.posts_today</c> reduced to the one bit Table 10's "rate-limited" cell consumes.
/// Irrelevant to every other cell.
/// </param>
public sealed record AuthorizationRequest(
    PrincipalTier Tier,
    CredentialState CredentialState,
    ResourceKind Resource,
    ActionKind Action,
    bool RateBudgetAvailable = true);

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
    private static bool IsRead(ActionKind action) =>
        action is ActionKind.List or ActionKind.Read or ActionKind.Search;

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
        var cell = row[request.Tier];

        // The `agent`/`enroll` row. Answered before anything else because it is not a
        // tier-indexed question at all, so neither the quarantine rule nor the Table 10 cell
        // below is the right thing to apply to it.
        if (cell is Table10Cell.OwnerAuthOnly)
            return Result<AuthorizationDecision>.Fail(
                AuthorizationErrors.OwnerAuthenticationRequired(request.Resource, request.Action));

        var tierDecision = FromCell(cell, row.Qualifier, request.RateBudgetAvailable);

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
