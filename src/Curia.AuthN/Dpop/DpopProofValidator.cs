using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.AuthN.Jwt;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Dpop;

/// <summary>What a DPoP proof must name to be accepted: the request's method and URL, the access
/// token its <c>ath</c> binds it to (none on a token request), and the thumbprint the token's
/// <c>cnf.jkt</c> binds it to (none on a token request, where the proof's key is what the token will
/// be bound to).</summary>
[SuppressMessage(
    "Design",
    "CA1054:URI parameters should not be strings",
    Justification = "Url is compared byte-for-byte against the proof's htu claim (RFC 9449 §4.2), " +
        "which is wire text, as IncomingRequest.CanonicalUrl is; round-tripping through Uri's own " +
        "normalization would risk accepting a URL that differs from what the proof signed.")]
[SuppressMessage(
    "Design",
    "CA1056:URI properties should not be strings",
    Justification = "See the CA1054 justification just above; the same reasoning applies to the property.")]
public sealed record DpopExpectation(string HttpMethod, string Url, string? AccessToken, string? ExpectedThumbprint);

/// <summary>A proof that passed <see cref="DpopProofValidator.Validate"/>: its embedded key, that key's
/// RFC 7638 thumbprint, and its claims.</summary>
public sealed record ValidatedProof(Jwk Key, string Thumbprint, DpopProofClaims Claims);

/// <summary>The value of an operation that succeeds with nothing to return.</summary>
public readonly record struct Unit
{
    public static Unit Value => default;
}

/// <summary>
/// R5.13: a DPoP proof is validated once, in one module, for every host. The resource server's Phase
/// 4 (<see cref="AccessTokenValidator"/>) and the token endpoint both call <see cref="Validate"/>, then
/// <see cref="ConsumeAsync"/>, so the token request's proof is held to every check a resource request's
/// is (register D29). Before D29 closed, the token endpoint read a proof only for its key.
/// </summary>
public static class DpopProofValidator
{
    /// <summary>The proof checks of §5.5's Phase 4, in its printed order, up to and including <c>ath</c>
    /// (checked only when <see cref="DpopExpectation.AccessToken"/> is non-null). The nonce and the
    /// replay insert are the caller's: the nonce is a write path's (R5.19), and the insert is
    /// <see cref="ConsumeAsync"/>.</summary>
    public static Result<ValidatedProof> Validate(
        string proof,
        DpopExpectation expected,
        IReadOnlyDictionary<string, IContentVerifier> verifiers,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(verifiers);

        if (!CompactJws.Split(proof).TryGetValue(out var proofParts, out var proofSplitError))
            return Result<ValidatedProof>.Fail(proofSplitError!);

        if (!CompactJws.ParseHeader(proofParts, DpopProofHeader.Parse).TryGetValue(out var proofHeader, out var proofHeaderError))
            return Result<ValidatedProof>.Fail(proofHeaderError!);

        // Errata A17's first addition: "require proof.header.typ == 'dpop+jwt'" -- placed here,
        // as early in Phase 4 as R5.9 places the access token's own type/algorithm pins in Phase
        // 1, and before any of the embedded key is even parsed.
        if (proofHeader.Typ != "dpop+jwt")
            return Result<ValidatedProof>.Fail(AuthNErrors.TypMismatch("dpop+jwt", proofHeader.Typ));

        // Not an errata item -- Stage C's own hardening, mirroring R5.9's reasoning for the DPoP
        // proof's own header: a proof carrying alg:"none" or an HMAC alg must never reach
        // verify_signature. See the Stage C report.
        if (!AuthNConstants.AllowedAlgorithms.Contains(proofHeader.Alg))
            return Result<ValidatedProof>.Fail(AuthNErrors.AlgNotAllowed(proofHeader.Alg));

        if (!CompactJws.ParseHeader(proofParts, DpopProofHeader.ParseJwk).TryGetValue(out var jwk, out var jwkError))
            return Result<ValidatedProof>.Fail(jwkError!);

        var thumbprint = JwkThumbprint.Compute(jwk);
        if (expected.ExpectedThumbprint is { } jkt && !FixedTimeEquals(thumbprint, jkt))
            return Result<ValidatedProof>.Fail(AuthNErrors.BindingMismatch());

        // R5.21 (errata G16): the proof's alg names the embedded key's algorithm, which its jwk's type
        // fixes, before any key material is built or any verifier chosen by the header.
        var keyAlg = jwk!.Match(okpEd25519: _ => "EdDSA", ecP256: _ => "ES256");
        if (!string.Equals(proofHeader.Alg, keyAlg, StringComparison.Ordinal))
            return Result<ValidatedProof>.Fail(AuthNErrors.AlgKeyMismatch(proofHeader.Alg, keyAlg));

        // R11.33 (errata G17): a jwk whose coordinates are no point on the curve is a malformed
        // proof, answered as one. Until D29 closed, the token endpoint bound a token to a key it never
        // built (register D29), and this refusal remains for any token so bound before then; it threw.
        if (!jwk.ToPublicKeyMaterial(kid: "").TryGetValue(out var proofKey, out var proofKeyError))
            return Result<ValidatedProof>.Fail(proofKeyError!);

        if (!verifiers.TryGetValue(proofHeader.Alg, out var proofVerifier))
            return Result<ValidatedProof>.Fail(AuthNErrors.AlgNotAllowed(proofHeader.Alg));

        if (!CompactJws.VerifySignature(proofParts, proofVerifier, proofKey))
            return Result<ValidatedProof>.Fail(AuthNErrors.SignatureInvalid());

        if (!CompactJws.ParsePayload(proofParts, DpopProofClaims.Parse).TryGetValue(out var proofClaims, out var proofClaimsError))
            return Result<ValidatedProof>.Fail(proofClaimsError!);

        if (proofClaims.Htm != expected.HttpMethod)
            return Result<ValidatedProof>.Fail(AuthNErrors.MethodMismatch());

        if (proofClaims.Htu != expected.Url)
            return Result<ValidatedProof>.Fail(AuthNErrors.UrlMismatch());

        var skew = proofClaims.Iat > now ? proofClaims.Iat - now : now - proofClaims.Iat;
        if (skew > AuthNConstants.MaxSkew)
            return Result<ValidatedProof>.Fail(AuthNErrors.ProofWindowExceeded());

        if (expected.AccessToken is { } accessToken && proofClaims.Ath != ComputeAth(accessToken))
            return Result<ValidatedProof>.Fail(AuthNErrors.AthMismatch());

        return Result<ValidatedProof>.Ok(new ValidatedProof(jwk, thumbprint, proofClaims!));
    }

    /// <summary>R5.14/R5.17: records the proof's <c>jti</c> in the shared replay cache, atomically; a
    /// <c>jti</c> already recorded is a replay.</summary>
    public static async Task<Result<Unit>> ConsumeAsync(
        ValidatedProof proof,
        IReplayCache replay,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(replay);

        // R5.14: a proof is acceptable until iat + MaxSkew (Validate's skew test is |iat - now| <= MaxSkew), so its jti is held at least that long. The second is because PostgresReplayCache readmits a jti whose expires_at <= now, and at exactly iat + MaxSkew Validate still accepts.
        var proofExpiry = (proof.Claims.Iat > now ? proof.Claims.Iat : now) + AuthNConstants.MaxSkew + TimeSpan.FromSeconds(1);
        var insertResult = await replay
            .TryInsertAsync(proof.Claims.Jti, proofExpiry, ct)
            .ConfigureAwait(false);
        if (!insertResult.TryGetValue(out var inserted, out var insertError))
            return Result<Unit>.Fail(insertError!);

        if (!inserted)
            return Result<Unit>.Fail(AuthNErrors.Replay());

        return Result<Unit>.Ok(Unit.Value);
    }

    /// <summary>RFC 9449 §4.2's <c>ath</c>: <c>base64url(sha256(access_token))</c>, where
    /// <paramref name="accessToken"/> is the compact access token string Phase 1 extracted (not
    /// the DPoP proof itself).</summary>
    private static string ComputeAth(string accessToken) =>
        Base64Url.EncodeToString(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(accessToken)));

    /// <summary>Both operands are already-computed digests/thumbprints (fixed-length, base64url
    /// text), not secrets an attacker recovers bit-by-bit through a comparison, but comparing
    /// them in constant time costs nothing here and removes the question.</summary>
    private static bool FixedTimeEquals(string a, string b) =>
        a.Length == b.Length && CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));
}
