using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Curia.AuthN.Dpop;

/// <summary>
/// RFC 7638 JWK thumbprint: SHA-256 over the JWK's *required* members only, serialized with
/// lexicographically sorted keys and no insignificant whitespace, base64url-encoded. This is
/// what Phase 4 (§5.5 line "require sha256_thumbprint(pk) == claims.cnf.jkt") compares against
/// the access token's <c>cnf.jkt</c> -- the proof-of-possession binding at the center of R5.6's
/// sender-constraining requirement.
///
/// Hand-built rather than round-tripped through <c>Curia.Canon.Canonical.CanonicalJson</c>
/// deliberately: RFC 7638's canonical form is its own fixed, narrower rule (exactly the required
/// members, in a specific member order per key type) rather than JCS's general "sort every
/// member" rule over an arbitrary document, and the member set here is small and fully known at
/// compile time, so hand-writing it is both correct per RFC 7638 and does not reach for Canon's
/// JCS machinery for a shape JCS was never asked to canonicalize.
/// </summary>
public static class JwkThumbprint
{
    public static string Compute(Jwk jwk)
    {
        ArgumentNullException.ThrowIfNull(jwk);

        return jwk.Match(
            okpEd25519: okp => ComputeFromCanonicalJson(BuildOkpCanonicalJson(okp)),
            ecP256: ec => ComputeFromCanonicalJson(BuildEcCanonicalJson(ec)));
    }

    /// <summary>RFC 8037 §3.2: OKP's required members, sorted, are exactly <c>crv</c>, <c>kty</c>, <c>x</c>.</summary>
    private static string BuildOkpCanonicalJson(Jwk.OkpEd25519 okp) =>
        $$"""{"crv":"Ed25519","kty":"OKP","x":"{{Base64Url.EncodeToString(okp.X.Span)}}"}""";

    /// <summary>RFC 7518 §6.2.1 / RFC 7638 §3.2: EC's required members, sorted, are
    /// <c>crv</c>, <c>kty</c>, <c>x</c>, <c>y</c>.</summary>
    private static string BuildEcCanonicalJson(Jwk.EcP256 ec) =>
        $$"""{"crv":"P-256","kty":"EC","x":"{{Base64Url.EncodeToString(ec.X.Span)}}","y":"{{Base64Url.EncodeToString(ec.Y.Span)}}"}""";

    private static string ComputeFromCanonicalJson(string json)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Base64Url.EncodeToString(hash);
    }
}
