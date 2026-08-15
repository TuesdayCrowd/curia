using Curia.Domain.Primitives;

namespace Curia.AuthN;

/// <summary>
/// RFC 9457 problem-type slugs for Stage C, mirroring <c>Curia.Canon.Jws.JwsErrors</c>' and
/// <c>Curia.Domain.DomainErrors</c>' one-factory-per-condition shape. R5.12 requires that the
/// response an untrusted caller actually sees collapse these to a coarse category ("401") without
/// naming which check failed; that collapsing is a serving-boundary (Api/Gateway) concern outside
/// Stage C. What lives here is the specific, internally-logged reason -- the thing R5.12 also
/// requires ("SHALL log the specific reason internally") and the thing this module's own test
/// suite asserts on to prove *which* phase rejected a given input.
/// </summary>
public static class AuthNErrors
{
    public static Error MissingAuthorization() => new(
        "curia/authn/missing-authorization", "No bearer or DPoP-scheme Authorization header present");

    public static Error Malformed(string detail) => new(
        "curia/authn/malformed", "Malformed token", detail);

    public static Error AlgNotAllowed(string alg) => new(
        "curia/authn/alg-not-allowed", "Algorithm not in the allow-list", alg);

    public static Error TypMismatch(string expected, string actual) => new(
        "curia/authn/typ-mismatch", $"Expected typ '{expected}'", actual);

    public static Error KidNotFound(string kid) => new(
        "curia/authn/kid-not-found", "kid not found in the configured JWKS", kid);

    public static Error SignatureInvalid() => new(
        "curia/authn/signature-invalid", "Signature does not verify");

    public static Error IssuerMismatch() => new(
        "curia/authn/issuer-mismatch", "iss does not match the configured issuer");

    public static Error AudienceMismatch() => new(
        "curia/authn/audience-mismatch", "aud does not contain the expected audience");

    public static Error ClientIdMismatch() => new(
        "curia/authn/client-id-mismatch", "client_id does not match sub (Table 8 consistency check)");

    public static Error SubjectMismatch() => new(
        "curia/authn/subject-mismatch", "sub does not match the agent the key resolver was scoped for");

    public static Error IssuerSubjectMismatch() => new(
        "curia/authn/issuer-subject-mismatch", "Client assertion iss must equal sub (RFC 7523 §3)");

    public static Error Expired() => new(
        "curia/authn/expired", "Token has expired");

    public static Error IssuedInFuture() => new(
        "curia/authn/issued-in-future", "iat is beyond the permitted clock skew");

    public static Error NotYetValid() => new(
        "curia/authn/not-yet-valid", "nbf is beyond the permitted clock skew");

    public static Error TtlExceeded() => new(
        "curia/authn/ttl-exceeded", "exp - iat exceeds the maximum permitted lifetime");

    public static Error MissingDpopProof() => new(
        "curia/authn/missing-dpop-proof", "No DPoP proof header present (R5.11: unbound tokens are never accepted)");

    public static Error MalformedJwk(string detail) => new(
        "curia/authn/malformed-jwk", "Malformed jwk header parameter", detail);

    public static Error BindingMismatch() => new(
        "curia/authn/binding-mismatch", "DPoP proof key thumbprint does not match cnf.jkt");

    public static Error MethodMismatch() => new(
        "curia/authn/method-mismatch", "DPoP proof htm does not match the request method");

    public static Error UrlMismatch() => new(
        "curia/authn/url-mismatch", "DPoP proof htu does not match the request URL");

    public static Error ProofWindowExceeded() => new(
        "curia/authn/proof-window-exceeded", "DPoP proof iat is outside the permitted freshness window");

    public static Error AthMismatch() => new(
        "curia/authn/ath-mismatch", "DPoP proof ath does not match the access token");

    public static Error NonceMissing() => new(
        "curia/authn/nonce-missing", "DPoP proof carries no nonce (R5.19 requires one on this path)");

    public static Error NonceStale() => new(
        "curia/authn/nonce-stale", "DPoP proof nonce is not the currently issued value");

    public static Error Replay() => new(
        "curia/authn/replay", "jti has already been used");
}
