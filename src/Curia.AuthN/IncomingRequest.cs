using System.Diagnostics.CodeAnalysis;

namespace Curia.AuthN;

/// <summary>
/// The wire material §5.5's <c>validate_request</c> reads, lifted out of any specific host's
/// request type (Gateway's YARP transform, Api's minimal-API <c>HttpRequest</c>) so the one
/// module R5.13 requires can be unit-tested with no ASP.NET Core host running at all, and so
/// neither host needs to duplicate the mapping the other already wrote.
/// </summary>
/// <param name="Authorization">The raw <c>Authorization</c> header value, e.g.
/// <c>"DPoP eyJhbGci..."</c>. <see langword="null"/> if the header was absent.</param>
/// <param name="DpopProof">The raw <c>DPoP</c> request header value (the proof JWS compact
/// serialization). <see langword="null"/> if the header was absent.</param>
/// <param name="HttpMethod">The request method, compared against the DPoP proof's <c>htm</c>.</param>
/// <param name="CanonicalUrl">The request URL, already canonicalized by the caller (scheme,
/// host, path -- no query or fragment, per RFC 9449 §4.2) and compared against the DPoP proof's
/// <c>htu</c>. Canonicalization is a host-specific concern (Gateway sees a different URL shape
/// than Api behind it) and deliberately stays out of this module for exactly that reason.</param>
/// <param name="RequireDpopNonce">Errata B4/R5.19: <see langword="true"/> on write paths, where a
/// current server-issued DPoP nonce is required in addition to everything else Phase 4 checks.
/// <see langword="false"/> leaves the nonce unchecked even if present.</param>
[SuppressMessage(
    "Design",
    "CA1054:URI parameters should not be strings",
    Justification = "CanonicalUrl is compared byte-for-byte against a DPoP proof's htu claim " +
        "(RFC 9449 §4.2), which is itself wire text, not a System.Uri -- round-tripping through " +
        "Uri's own normalization would risk silently accepting a URL that differs from what the " +
        "proof actually signed. Canonicalizing to this exact string is each host's job (Gateway " +
        "and Api see different URL shapes), deliberately kept out of this module.")]
[SuppressMessage(
    "Design",
    "CA1056:URI properties should not be strings",
    Justification = "See the CA1054 justification just above; the same reasoning applies to the property.")]
public sealed record IncomingRequest(
    string? Authorization,
    string? DpopProof,
    string HttpMethod,
    string CanonicalUrl,
    bool RequireDpopNonce = false)
{
    private const string BearerScheme = "Bearer ";
    private const string DpopScheme = "DPoP ";

    /// <summary>Phase 1's <c>extract_bearer_or_dpop</c>: pulls the token out of an
    /// <c>Authorization</c> header carrying either scheme keyword, case-insensitively (RFC 7235
    /// §2.1 defines the auth-scheme token as case-insensitive). Phase 4 still unconditionally
    /// requires a DPoP proof afterward regardless of which scheme word prefixed this header --
    /// the printed algorithm has no branch that skips proof-of-possession for either spelling.</summary>
    internal string? ExtractToken()
    {
        if (Authorization is null)
            return null;

        if (Authorization.StartsWith(DpopScheme, StringComparison.OrdinalIgnoreCase))
            return Authorization[DpopScheme.Length..];

        if (Authorization.StartsWith(BearerScheme, StringComparison.OrdinalIgnoreCase))
            return Authorization[BearerScheme.Length..];

        return null;
    }
}
