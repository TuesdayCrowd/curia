using System.Buffers.Text;
using System.Security.Cryptography;
using Curia.AuthN.Dpop;
using Curia.AuthN.Jwt;
using Curia.Domain.Primitives;

namespace Curia.AuthN;

/// <summary>The result of a successful Phase 1-4 validation: the access token's claims plus proof
/// that the caller currently holds the private key matching <c>cnf.jkt</c>. Phases 5 (live agent
/// and owner state) and 6 (PDP authorization) are deliberately not this type's concern -- they
/// need an agent store and a PDP client that do not exist in this stage (Increment 4's Stage
/// A/B and a later increment, respectively). A caller chains those from here; this type is the
/// documented handoff point rather than a silent scope boundary. See the Stage C report.</summary>
public sealed record ValidatedRequest(AccessTokenClaims Claims, string DpopKeyThumbprint);

/// <summary>
/// §5.5's <c>validate_request</c>, Phases 1-4, as one static method sequence in the printed
/// order -- R5.13: implemented once, here, for every host (Gateway/PEP-1, Api/PEP-2) to call.
/// Each phase is a separate internal method so ordering tests can pin one check's position
/// without driving the whole algorithm, and so this class reads top-to-bottom against §5.5 the
/// same way the white paper's own pseudocode does. See the Stage C report for the full
/// phase-by-phase mapping, including where errata A17's two additions and Stage C's own
/// hardening land and why.
/// </summary>
public static class AccessTokenValidator
{
    public static async Task<Result<ValidatedRequest>> ValidateRequestAsync(
        IncomingRequest request,
        AccessTokenValidationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        // ---- Phase 1: parse without trusting ----------------------------------------------
        var token = request.ExtractToken();
        if (token is null)
            return Result<ValidatedRequest>.Fail(AuthNErrors.MissingAuthorization());

        if (!CompactJws.Split(token).TryGetValue(out var parts, out var splitError))
            return Result<ValidatedRequest>.Fail(splitError!);

        if (!CompactJws.DecodeHeader(parts).TryGetValue(out var header, out var headerError))
            return Result<ValidatedRequest>.Fail(headerError!);

        // R5.9: pin the algorithm BEFORE any signature work, and before typ/kid too -- exactly
        // the printed order (lines "if header.alg not in ALLOWED_ALGS" then "if header.typ !=").
        // Never read header.alg to pick a verification routine; it only ever gates membership in
        // AuthNConstants.AllowedAlgorithms, and the routine that runs is chosen by *this check*
        // having already passed, not by branching on the string afterward.
        if (!AuthNConstants.AllowedAlgorithms.Contains(header.Alg))
            return Result<ValidatedRequest>.Fail(AuthNErrors.AlgNotAllowed(header.Alg));

        if (header.Typ != "at+jwt")
            return Result<ValidatedRequest>.Fail(AuthNErrors.TypMismatch("at+jwt", header.Typ));

        // R5.10: resolve kid ONLY within the configured issuer JWKS -- IJwsKeyResolver's shape
        // makes "fetch a key from a URL found inside the token" a non-expressible call, not just
        // an unwritten one; header.Kid is the only field ever passed to it.
        var keyResult = await context.IssuerKeyResolver.ResolveAsync(header.Kid, cancellationToken).ConfigureAwait(false);
        if (!keyResult.TryGetValue(out var key, out var keyError))
            return Result<ValidatedRequest>.Fail(keyError!);

        // ---- Phase 2: cryptographic verification -------------------------------------------
        if (!context.VerifiersByAlg.TryGetValue(header.Alg, out var verifier))
            return Result<ValidatedRequest>.Fail(AuthNErrors.AlgNotAllowed(header.Alg));

        if (!CompactJws.VerifySignature(parts, verifier, key))
            return Result<ValidatedRequest>.Fail(AuthNErrors.SignatureInvalid());

        if (!CompactJws.ParsePayload(parts, AccessTokenClaims.Parse).TryGetValue(out var claims, out var claimsError))
            return Result<ValidatedRequest>.Fail(claimsError!);

        // ---- Phase 3: claim validation (all mandatory, no skipping) ------------------------
        var now = context.Clock.GetUtcNow();

        if (claims.Iss != context.ConfiguredIssuer)
            return Result<ValidatedRequest>.Fail(AuthNErrors.IssuerMismatch());

        if (!claims.Aud.Contains(context.ResourceServer, StringComparer.Ordinal))
            return Result<ValidatedRequest>.Fail(AuthNErrors.AudienceMismatch());

        // Table 8's client_id/sub consistency check: present in Table 8 ("client_id SHALL Same
        // as sub for this profile -- Consistency check") but absent from the printed §5.5
        // pseudocode -- the same shape of gap A17 already names twice elsewhere in this phase.
        // Not an errata item; see the Stage C report for why Stage C closes it anyway.
        if (claims.ClientId != claims.Sub)
            return Result<ValidatedRequest>.Fail(AuthNErrors.ClientIdMismatch());

        if (now >= claims.Exp)
            return Result<ValidatedRequest>.Fail(AuthNErrors.Expired());

        if (claims.Iat > now + AuthNConstants.MaxSkew)
            return Result<ValidatedRequest>.Fail(AuthNErrors.IssuedInFuture());

        // Errata A17: "a nbf check to Phase 3 to match Table 8's SHOULD." Grouped with the other
        // now()-relative freshness checks (exp, iat), immediately after iat and before the
        // exp-iat ttl-ceiling check below, which is pure claim arithmetic with no now() in it.
        if (claims.Nbf is { } nbf && nbf > now + AuthNConstants.MaxSkew)
            return Result<ValidatedRequest>.Fail(AuthNErrors.NotYetValid());

        if (claims.Exp - claims.Iat > AuthNConstants.MaxAccessTokenTtl)
            return Result<ValidatedRequest>.Fail(AuthNErrors.TtlExceeded());

        // ---- Phase 4: proof of possession ---------------------------------------------------
        // R5.11: an unbound token is never accepted, even with a perfect signature -- the proof
        // header is mandatory, unconditionally, exactly as the printed algorithm has it (no
        // branch that skips this for a "read-only" request; only the nonce sub-check below is
        // scoped to write paths per R5.19).
        if (request.DpopProof is null)
            return Result<ValidatedRequest>.Fail(AuthNErrors.MissingDpopProof());

        if (!CompactJws.Split(request.DpopProof).TryGetValue(out var proofParts, out var proofSplitError))
            return Result<ValidatedRequest>.Fail(proofSplitError!);

        if (!CompactJws.ParseHeader(proofParts, DpopProofHeader.Parse).TryGetValue(out var proofHeader, out var proofHeaderError))
            return Result<ValidatedRequest>.Fail(proofHeaderError!);

        // Errata A17's first addition: "require proof.header.typ == 'dpop+jwt'" -- placed here,
        // as early in Phase 4 as R5.9 places the access token's own type/algorithm pins in Phase
        // 1, and before any of the embedded key is even parsed.
        if (proofHeader.Typ != "dpop+jwt")
            return Result<ValidatedRequest>.Fail(AuthNErrors.TypMismatch("dpop+jwt", proofHeader.Typ));

        // Not an errata item -- Stage C's own hardening, mirroring R5.9's reasoning for the DPoP
        // proof's own header: a proof carrying alg:"none" or an HMAC alg must never reach
        // verify_signature. See the Stage C report.
        if (!AuthNConstants.AllowedAlgorithms.Contains(proofHeader.Alg))
            return Result<ValidatedRequest>.Fail(AuthNErrors.AlgNotAllowed(proofHeader.Alg));

        if (!CompactJws.ParseHeader(proofParts, DpopProofHeader.ParseJwk).TryGetValue(out var jwk, out var jwkError))
            return Result<ValidatedRequest>.Fail(jwkError!);

        var thumbprint = JwkThumbprint.Compute(jwk);
        if (!FixedTimeEquals(thumbprint, claims.CnfJkt))
            return Result<ValidatedRequest>.Fail(AuthNErrors.BindingMismatch());

        var proofKey = jwk.ToPublicKeyMaterial(kid: "");
        if (!context.VerifiersByAlg.TryGetValue(proofHeader.Alg, out var proofVerifier))
            return Result<ValidatedRequest>.Fail(AuthNErrors.AlgNotAllowed(proofHeader.Alg));

        if (!CompactJws.VerifySignature(proofParts, proofVerifier, proofKey))
            return Result<ValidatedRequest>.Fail(AuthNErrors.SignatureInvalid());

        if (!CompactJws.ParsePayload(proofParts, DpopProofClaims.Parse).TryGetValue(out var proofClaims, out var proofClaimsError))
            return Result<ValidatedRequest>.Fail(proofClaimsError!);

        if (proofClaims.Htm != request.HttpMethod)
            return Result<ValidatedRequest>.Fail(AuthNErrors.MethodMismatch());

        if (proofClaims.Htu != request.CanonicalUrl)
            return Result<ValidatedRequest>.Fail(AuthNErrors.UrlMismatch());

        var skew = proofClaims.Iat > now ? proofClaims.Iat - now : now - proofClaims.Iat;
        if (skew > AuthNConstants.MaxSkew)
            return Result<ValidatedRequest>.Fail(AuthNErrors.ProofWindowExceeded());

        if (proofClaims.Ath != ComputeAth(token))
            return Result<ValidatedRequest>.Fail(AuthNErrors.AthMismatch());

        // Errata B4/R5.19: required only on write paths, and only when this resource server has
        // a nonce store configured at all (a deployment may not have adopted the SHOULD yet).
        if (request.RequireDpopNonce && context.DpopNonceStore is { } nonceStore)
        {
            if (proofClaims.Nonce is not { } nonce)
                return Result<ValidatedRequest>.Fail(AuthNErrors.NonceMissing());

            var currentResult = await nonceStore.IsCurrentAsync(nonce, cancellationToken).ConfigureAwait(false);
            if (!currentResult.TryGetValue(out var isCurrent, out var currentError))
                return Result<ValidatedRequest>.Fail(currentError!);

            if (!isCurrent)
                return Result<ValidatedRequest>.Fail(AuthNErrors.NonceStale());
        }

        var proofExpiry = now + AuthNConstants.MaxSkew;
        var insertResult = await context.ReplayCache
            .TryInsertAsync(proofClaims.Jti, proofExpiry, cancellationToken)
            .ConfigureAwait(false);
        if (!insertResult.TryGetValue(out var inserted, out var insertError))
            return Result<ValidatedRequest>.Fail(insertError!);

        if (!inserted)
            return Result<ValidatedRequest>.Fail(AuthNErrors.Replay());

        // ---- Phases 5-6: live state and authorization --------------------------------------
        // Deliberately not implemented here -- see ValidatedRequest's remarks.
        return Result<ValidatedRequest>.Ok(new ValidatedRequest(claims, thumbprint));
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
