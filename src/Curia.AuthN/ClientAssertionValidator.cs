using Curia.AuthN.Jwt;
using Curia.Domain.Primitives;

namespace Curia.AuthN;

/// <summary>
/// The client-assertion half of R5.13's "one module": RFC 7523 private_key_jwt validation for
/// the token endpoint (Figure 5, step 3: "resolve agent JWKS by iss, verify sig, aud, exp,
/// jti"), built from the same <see cref="CompactJws"/> core <see cref="AccessTokenValidator"/>
/// uses -- pin the algorithm before any signature work, resolve the key without ever fetching a
/// URL from the token, verify, then validate claims -- so a bug fixed in one artifact type's
/// parsing or algorithm-pinning is fixed in both, which is the entire point of R5.13 rather than
/// an accident of shared file layout.
///
/// One deliberate divergence from that shared shape: resolving the agent's key here is not just
/// "look up this kid" the way <see cref="AccessTokenValidator"/>'s issuer-JWKS resolve is --
/// errata A12/R6.31 requires that an agent key's validity be evaluated at <c>server_ts</c>, so
/// <see cref="Ports.IAgentKeyResolver.ResolveAsync"/> takes that instant as a parameter, not an
/// afterthought. That is why the clock is read here, ahead of resolution -- earlier than
/// <see cref="AccessTokenValidator"/> reads it for its own (timestamp-independent) resolve --
/// rather than later alongside the other claim-freshness checks. It does not change R5.9's
/// ordering: the algorithm is still pinned, and <c>typ</c> still checked, before either the clock
/// is read or any signature work happens.
///
/// Deliberately excludes Phase 4 (DPoP proof of possession) and Phases 5-6: a client assertion
/// authenticates the token *request*, not a resource-server call, so it carries no DPoP proof
/// and there is no live agent/owner state or PDP decision at the token endpoint's assertion-
/// verification step (issuance-time PDP consultation, R5.5, is a separate Issuer concern, not
/// part of this artifact's own validity).
/// </summary>
public static class ClientAssertionValidator
{
    public static async Task<Result<ClientAssertionClaims>> ValidateAsync(
        string? assertion,
        ClientAssertionValidationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!CompactJws.Split(assertion).TryGetValue(out var parts, out var splitError))
            return Result<ClientAssertionClaims>.Fail(splitError!);

        if (!CompactJws.DecodeHeader(parts).TryGetValue(out var header, out var headerError))
            return Result<ClientAssertionClaims>.Fail(headerError!);

        // R5.9's principle applied to this artifact type too: pin alg before any signature work,
        // and before resolving a key or reading typ.
        if (!AuthNConstants.AllowedAlgorithms.Contains(header.Alg))
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.AlgNotAllowed(header.Alg));

        // Appendix C.1's header example: {"typ": "JWT"} -- RFC 7523 client assertions use the
        // ordinary JWT type, not a Forum-specific one the way the access token's "at+jwt" is.
        if (header.Typ != "JWT")
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.TypMismatch("JWT", header.Typ));

        // R6.31/A12: an agent key's validity is evaluated at server_ts, never at submission time
        // and never at the assertion's own iat -- so the governing instant has to exist before
        // resolution can even be attempted. Reading it here, immediately after the alg/typ pins
        // and before resolving anything, is what makes IAgentKeyResolver's signature satisfiable:
        // there is no later point in this method where "resolve, then separately check validity"
        // could be reintroduced, because resolution itself already requires the instant.
        var serverTimestamp = ServerTimestamp.At(context.Clock.GetUtcNow());
        var now = serverTimestamp.Value;

        // R5.10's principle applied here: AgentKeyResolver is already scoped by the caller to one
        // agent's Forum-served keys (see ClientAssertionValidationContext's remarks); this call
        // only ever passes the kid string and server_ts, never a URL.
        var keyResult = await context.AgentKeyResolver.ResolveAsync(header.Kid, serverTimestamp, cancellationToken).ConfigureAwait(false);
        if (!keyResult.TryGetValue(out var key, out var keyError))
            return Result<ClientAssertionClaims>.Fail(keyError!);

        if (!context.VerifiersByAlg.TryGetValue(header.Alg, out var verifier))
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.AlgNotAllowed(header.Alg));

        if (!CompactJws.VerifySignature(parts, verifier, key))
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.SignatureInvalid());

        if (!CompactJws.ParsePayload(parts, ClientAssertionClaims.Parse).TryGetValue(out var claims, out var claimsError))
            return Result<ClientAssertionClaims>.Fail(claimsError!);

        // RFC 7523 §3: both iss and sub MUST carry the client's own identifier for this profile.
        if (claims.Iss != claims.Sub)
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.IssuerSubjectMismatch());

        if (claims.Sub != context.ExpectedSubject)
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.SubjectMismatch());

        // R5.1: "The issuer SHALL reject assertions whose aud does not exactly match its own
        // token endpoint URL."
        if (claims.Aud != context.TokenEndpoint)
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.AudienceMismatch());

        // now/serverTimestamp were already read above, ahead of key resolution -- reused here
        // rather than re-read, so every check in this method judges the request against the
        // exact same instant.
        if (now >= claims.Exp)
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.Expired());

        if (claims.Iat > now + AuthNConstants.MaxSkew)
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.IssuedInFuture());

        // R5.1: "The client assertion SHALL have a lifetime <= 60 seconds."
        if (claims.Exp - claims.Iat > AuthNConstants.MaxClientAssertionTtl)
            return Result<ClientAssertionClaims>.Fail(AuthNErrors.TtlExceeded());

        // R5.14: the jti replay cache is shared with DPoP proofs (same IReplayCache instance,
        // wired by the caller) -- this is that requirement's other half.
        var expiresAt = claims.Exp + AuthNConstants.MaxSkew;
        var insertResult = await context.ReplayCache
            .TryInsertAsync(claims.Jti, expiresAt, cancellationToken)
            .ConfigureAwait(false);
        if (!insertResult.TryGetValue(out var inserted, out var insertError))
            return Result<ClientAssertionClaims>.Fail(insertError!);

        return inserted
            ? Result<ClientAssertionClaims>.Ok(claims)
            : Result<ClientAssertionClaims>.Fail(AuthNErrors.Replay());
    }
}
