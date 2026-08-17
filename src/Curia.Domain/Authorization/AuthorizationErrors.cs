using Curia.Domain.Primitives;

namespace Curia.Domain.Authorization;

/// <summary>
/// RFC 9457 problem-type slugs for §7, mirroring <see cref="Curia.Domain.DomainErrors"/>'
/// one-factory-per-condition shape so every rejection names the rule it enforces.
///
/// <para>Note that a <i>denial</i> is not one of these. A denial is a well-formed answer to a
/// well-formed question and travels as an <see cref="AuthorizationDecision"/>; these are the cases
/// where the question itself cannot be answered from the published model. Conflating the two would
/// let a specification gap arrive at the caller wearing a deliberate denial's clothes.</para>
/// </summary>
public static class AuthorizationErrors
{
    /// <summary>
    /// Table 10 has no row for this (resource, action) pair. Deliberately not a denial: the model
    /// is silent, and silence is a gap to be closed in §7.2, not a decision.
    /// </summary>
    public static Error UnmodelledResourceAction(ResourceKind resource, ActionKind action) => new(
        "curia/authz/unmodelled-resource-action",
        "Table 10 defines no row for this resource and action",
        $"resource={ResourceActionNames.Wire(resource)} action={ResourceActionNames.Wire(action)}");

    /// <summary>
    /// The <c>agent</c>/<c>enroll</c> row's "owner-auth only" cell. §4.3 decides enrollment through
    /// owner authentication, so a tier-indexed PDP query about it is a category error rather than
    /// something to answer allow or deny. Reported so the caller routes to the enrollment path
    /// instead of silently receiving a denial it would probably log as an authorization failure.
    /// </summary>
    public static Error OwnerAuthenticationRequired(ResourceKind resource, ActionKind action) => new(
        "curia/authz/owner-authentication-required",
        "Table 10 decides this action by owner authentication, not by trust tier",
        $"resource={ResourceActionNames.Wire(resource)} action={ResourceActionNames.Wire(action)}");
}
