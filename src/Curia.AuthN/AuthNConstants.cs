namespace Curia.AuthN;

/// <summary>
/// The numeric ceilings §5.5/§5.6 pin. Centralized here (rather than a literal in each phase
/// method) because every one of them is independently a requirement number, and a reviewer
/// checking "is R5.16 actually 30 seconds in the code" should find one place to look.
/// </summary>
public static class AuthNConstants
{
    /// <summary>
    /// R5.16: "Permitted clock skew SHALL be &lt;= 30 seconds." R5.16 sits in §5.6 ("Replay
    /// defense") rather than being scoped to one specific claim, and the white paper never
    /// gives Phase 4's <c>PROOF_WINDOW</c> a separate numeric value anywhere else -- Table 7's
    /// "Replay window: Bounded by <c>iat</c> freshness plus a server-side <c>jti</c> cache" is
    /// the only other place proof freshness is discussed, and it does not name a second
    /// constant either. Stage C therefore reads R5.16 as the one permitted-skew figure the
    /// whole algorithm uses -- access token <c>iat</c>/<c>nbf</c>, client assertion <c>iat</c>,
    /// and DPoP proof <c>iat</c> (<c>PROOF_WINDOW</c>) alike -- rather than inventing an
    /// unstated second number for DPoP alone. See the Stage C report for this call spelled out.
    /// </summary>
    public static readonly TimeSpan MaxSkew = TimeSpan.FromSeconds(30);

    /// <summary>R5.1: client assertion lifetime SHALL be &lt;= 60 seconds.</summary>
    public static readonly TimeSpan MaxClientAssertionTtl = TimeSpan.FromSeconds(60);

    /// <summary>R5.2: access token lifetime SHALL be &lt;= 300 seconds.</summary>
    public static readonly TimeSpan MaxAccessTokenTtl = TimeSpan.FromSeconds(300);

    /// <summary>Errata B4/R5.19: DPoP server nonce rotation intervals SHALL be &lt;= 5 minutes.</summary>
    public static readonly TimeSpan MaxDpopNonceRotationInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Table 8's access-token allow-list ("alg (header) SHALL EdDSA or ES256 -- Against a pinned
    /// allow-list"), R4.15's key-algorithm restriction applied at the token layer. Every alg
    /// pin in this module -- access token (R5.9), client assertion, and DPoP proof (Stage C's
    /// own hardening beyond errata A17's two named additions; see the report) -- checks against
    /// this same set, so HS*/RS*/"none" are excluded everywhere a signature is ever verified,
    /// not just at the one call site the white paper's pseudocode happens to show.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedAlgorithms =
        new HashSet<string>(StringComparer.Ordinal) { "EdDSA", "ES256" };
}
