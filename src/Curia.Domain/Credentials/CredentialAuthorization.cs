namespace Curia.Domain.Credentials;

/// <summary>
/// Table 6 column 3, "Can authenticate?", as a type. <see cref="ReadOnly"/> exists solely for
/// <see cref="CredentialState.Quarantined"/>'s "Yes (read scopes only)" cell -- every other
/// state's cell in that column is a plain Yes/No.
/// </summary>
public enum CredentialAuthenticationScope
{
    Denied,
    ReadOnly,
    Unrestricted,
}

/// <summary>
/// Table 6 column 4, "Can post?". <see cref="PerTier"/> is <see cref="CredentialState.Active"/>'s
/// "Per tier" cell: the actual accept/deny for a given request is a function of the agent's trust
/// tier (§7.3), which this layer has no data for and no opinion on. This layer only records that
/// <c>active</c> is the *eligible* state -- never a blanket grant -- leaving the tier check to
/// whatever consults trust tiers.
/// </summary>
public enum CredentialPostingEligibility
{
    Denied,
    PerTier,
}

/// <summary>
/// Table 6 columns 3 and 4, one static function per column, each an explicit arm per named
/// <see cref="CredentialState"/> value (<c>.editorconfig</c> escalates IDE0072 "populate switch" to
/// a build error, so a state added without an entry here fails the build rather than silently
/// reading as "denied" by falling into a catch-all). The trailing discard arm exists only to
/// satisfy CS8524 -- C# enums are not sealed to their named members, so even a switch that lists
/// every one of the seven still needs an arm for a value with no name at all (e.g. an invalid cast)
/// -- and throws rather than returning a value, so a genuinely impossible input fails loudly
/// instead of silently reading as "denied."
///
/// R4.22 ("suspended and quarantined take effect on the next request, not the next token expiry,"
/// which "requires the PDP to be consulted per request") and R4.23 ("the distinction between
/// retired and compromised SHALL be preserved") are both HTTP-/PDP-layer requirements in their
/// enforcement, but each constrains this layer's types: R4.22 by there being no cached
/// authorization value anywhere in this module for a per-request PDP to have to invalidate --
/// see <see cref="CredentialTransitionedEvent"/>'s remarks -- and R4.23 by
/// <see cref="CredentialState.Retired"/> and <see cref="CredentialState.Compromised"/> never
/// sharing a switch arm below, or anywhere else in this module, despite currently producing the
/// identical (<see cref="CredentialAuthenticationScope.Denied"/>,
/// <see cref="CredentialPostingEligibility.Denied"/>) pair. A future caller distinguishing them
/// (e.g. to decide whether previously published content stays up) switches on
/// <see cref="CredentialState"/> itself, which still has two separate values to switch on --
/// nothing here ever collapses them into one.
/// </summary>
public static class CredentialAuthorization
{
    /// <summary>Table 6 column 3, verbatim, one cell per state.</summary>
    public static CredentialAuthenticationScope AuthenticationScope(this CredentialState state) => state switch
    {
        CredentialState.Pending => CredentialAuthenticationScope.Denied,
        CredentialState.Active => CredentialAuthenticationScope.Unrestricted,
        CredentialState.Suspended => CredentialAuthenticationScope.Denied,
        CredentialState.Quarantined => CredentialAuthenticationScope.ReadOnly,
        CredentialState.Retired => CredentialAuthenticationScope.Denied,
        CredentialState.Compromised => CredentialAuthenticationScope.Denied,
        CredentialState.Expired => CredentialAuthenticationScope.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, $"Unrecognized {nameof(CredentialState)} value"),
    };

    /// <summary>Table 6 column 4, verbatim, one cell per state.</summary>
    public static CredentialPostingEligibility PostingEligibility(this CredentialState state) => state switch
    {
        CredentialState.Pending => CredentialPostingEligibility.Denied,
        CredentialState.Active => CredentialPostingEligibility.PerTier,
        CredentialState.Suspended => CredentialPostingEligibility.Denied,
        CredentialState.Quarantined => CredentialPostingEligibility.Denied,
        CredentialState.Retired => CredentialPostingEligibility.Denied,
        CredentialState.Compromised => CredentialPostingEligibility.Denied,
        CredentialState.Expired => CredentialPostingEligibility.Denied,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, $"Unrecognized {nameof(CredentialState)} value"),
    };
}
