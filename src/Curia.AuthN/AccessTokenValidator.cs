using Curia.AuthN.Dpop;
using Curia.AuthN.Jwt;
using Curia.AuthN.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

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
        // an unwritten one; header.Kid is the only field ever passed to it. R11.34 (errata G18): it is
        // passed as a Kid, and one that is no Kid is answered exactly as a kid the issuer does not
        // hold is answered, since no issuer key is named by it (spec section 4.7).
        if (!Kid.Parse(header.Kid).TryGetValue(out var kid, out _))
            return Result<ValidatedRequest>.Fail(AuthNErrors.KeyNotRegistered(AuthNConstants.IssuerKeyOwner, header.Kid));

        var keyResult = await context.IssuerKeyResolver.ResolveAsync(kid!, cancellationToken).ConfigureAwait(false);
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

        // R5.13: the proof's checks run in the one validator the token endpoint runs too, here with
        // the token's cnf.jkt and the access token its ath must name.
        var proofResult = DpopProofValidator.Validate(
            request.DpopProof,
            new DpopExpectation(request.HttpMethod, request.CanonicalUrl, token, claims.CnfJkt),
            context.VerifiersByAlg,
            now);
        if (!proofResult.TryGetValue(out var validatedProof, out var proofError))
            return Result<ValidatedRequest>.Fail(proofError!);

        // Errata B4/R5.19: required only on write paths, and only when this resource server has
        // a nonce store configured at all (a deployment may not have adopted the SHOULD yet).
        if (request.RequireDpopNonce && context.DpopNonceStore is { } nonceStore)
        {
            if (validatedProof!.Claims.Nonce is not { } nonce)
                return Result<ValidatedRequest>.Fail(AuthNErrors.NonceMissing());

            // R11.33 (errata G17): a nonce the Forum could not have issued is a stale one, answered
            // with a fresh nonce as any other is, and the store is never asked: Postgres text cannot
            // hold U+0000 (22021), and the store threw on every write route. R11.34 (errata G18): the
            // store is asked only about a DpopNonce.
            if (!DpopNonce.Parse(nonce).TryGetValue(out var typedNonce, out _))
                return Result<ValidatedRequest>.Fail(AuthNErrors.NonceStale());

            var currentResult = await nonceStore.IsCurrentAsync(typedNonce!, cancellationToken).ConfigureAwait(false);
            if (!currentResult.TryGetValue(out var isCurrent, out var currentError))
                return Result<ValidatedRequest>.Fail(currentError!);

            if (!isCurrent)
                return Result<ValidatedRequest>.Fail(AuthNErrors.NonceStale());
        }

        var consumed = await DpopProofValidator.ConsumeAsync(validatedProof!, context.ReplayCache, now, cancellationToken).ConfigureAwait(false);
        if (!consumed.TryGetValue(out _, out var consumeError))
            return Result<ValidatedRequest>.Fail(consumeError!);

        // R5.22: the token names the key its assertion was signed with, and every authenticated request
        // reads, uncached, that the key is live. A read that fails refuses the request with its own error
        // (it is never taken for Active), and a key the identity retired ends the token it issued.
        if (!Kid.Parse(claims.AssertionKid, "curia_akid").TryGetValue(out var akid, out _))
            return Result<ValidatedRequest>.Fail(AuthNErrors.TokenNamesNoKey());

        // A subject that is no AgentId has no key that is live.
        if (!AgentId.Parse(claims.Sub, "sub").TryGetValue(out var subject, out _))
            return Result<ValidatedRequest>.Fail(AuthNErrors.TokenKeyNotLive());

        var status = await context.KeyLiveness.StatusAsync(subject!, akid!, cancellationToken).ConfigureAwait(false);
        if (!status.TryGetValue(out var keyStatus, out var statusError)) return Result<ValidatedRequest>.Fail(statusError!);
        if (keyStatus != KeyStatus.Active) return Result<ValidatedRequest>.Fail(AuthNErrors.TokenKeyNotLive());

        // ---- Phases 5-6: live state and authorization --------------------------------------
        // Deliberately not implemented here -- see ValidatedRequest's remarks.
        return Result<ValidatedRequest>.Ok(new ValidatedRequest(claims, validatedProof!.Thumbprint));
    }
}
