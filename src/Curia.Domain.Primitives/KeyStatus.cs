namespace Curia.Domain.Primitives;

/// <summary>
/// What the log says of one key of one identity at a position (spec section 4.8, errata G21). It lives
/// in <c>Curia.Domain.Primitives</c> and not <c>Curia.Domain.Keys</c> because <c>Curia.AuthN</c>
/// references Canon and Domain.Primitives only, and R5.22 reads it there.
/// </summary>
public enum KeyStatus
{
    /// <summary>No entry binds the key to the identity.</summary>
    Unbound,

    /// <summary>Bound, and neither retired nor compromised.</summary>
    Active,

    /// <summary>The identity retired it (R4.39).</summary>
    Retired,

    /// <summary>A declaration names it (R6.57, PR 2).</summary>
    Compromised,
}
