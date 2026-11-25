using System.Security.Cryptography;
using System.Text.Json;
using Curia.AuthN.Dpop;
using Curia.AuthN.Jwt;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.AuthN;

/// <summary>What an enrollment request carries that R4.46's proof is judged against: the request's members, the key's stored form (R4.28) and the proof as sent (null when the member was absent).</summary>
public sealed record EnrollmentProofRequest(string AgentId, string Alg, string Kid, ReadOnlyMemory<byte> PublicKey, string? Proof);

/// <summary>
/// R4.46 (errata G79): the proof that the submitter of an enrollment holds the private half of the key being enrolled. The seven steps are the errata's, in its order, and each is marked where it
/// stands in <see cref="ValidateAsync"/>. Steps 1 to 5 read and write no store (they are pure); step 6 asks the nonce store and comes before the <c>jti</c> is consumed; step 7 consumes the <c>jti</c>
/// in the cache R5.14 shares with client assertions and DPoP proofs, and comes before the caller reads the event log, so a request whose proof does not verify reads and writes nothing, and a fault of
/// the log after step 7 spends the proof (R5.23's rule, as <c>ClientAssertionValidator</c> keeps G72's). The signature is checked before the payload is read, so no claim is trusted before it is
/// signed, and under the request's own key, never a key taken from the proof. The <c>iat</c> check is pure and comes before any store is asked: it is what bounds a proof's reuse after the replay cache
/// has forgotten its <c>jti</c> (<see cref="DpopProofValidator.RetainedUntil"/>), so moving it after the cache would reopen replay.
/// </summary>
public sealed class EnrollmentProofValidator
{
    /// <summary>R4.46 step 2: the longest proof, in characters, that is read at all.</summary>
    public const int MaxProofLength = 8_192;

    /// <summary>R4.46: the proof's header <c>typ</c>, which separates it from a DPoP proof, a client assertion and a post.</summary>
    public const string ProofTyp = "curia-enroll-pop+jwt";

    private readonly IReadOnlyDictionary<string, IContentVerifier> _verifiers;
    private readonly IDpopNonceStore _nonces;
    private readonly IReplayCache _replay;

    public EnrollmentProofValidator(IReadOnlyDictionary<string, IContentVerifier> verifiers, IDpopNonceStore nonces, IReplayCache replay)
    {
        ArgumentNullException.ThrowIfNull(verifiers);
        ArgumentNullException.ThrowIfNull(nonces);
        ArgumentNullException.ThrowIfNull(replay);
        _verifiers = verifiers;
        _nonces = nonces;
        _replay = replay;
    }

    /// <summary>
    /// R4.46's seven steps. <paramref name="audience"/> is the absolute URL of the enrollment route as the Forum received it; <paramref name="now"/> is the Forum's clock. A refusal of the proof is one
    /// of <see cref="EnrollmentProofErrors"/>'s seven; a fault of the nonce store or the replay cache is that store's own error, passed through unchanged.
    /// </summary>
    public async Task<Result<Unit>> ValidateAsync(EnrollmentProofRequest request, string audience, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(audience);

        // Step 1: no proof, or an empty one.
        if (string.IsNullOrEmpty(request.Proof))
            return Fail(EnrollmentProofErrors.Required());

        // Step 2: a proof over the cap (decided before any decoding), not three unpadded base64url segments, a header that does not read as an object, a typ that is not this proof's, an alg
        // outside R4.15's allow-list, or no kid.
        // (Base64Url.IsValid admits '=' padding, which a compact JWS never carries, so it is refused here.)
        if (request.Proof.Length > MaxProofLength || request.Proof.Contains('=', StringComparison.Ordinal) || !CompactJws.Split(request.Proof).TryGetValue(out var parts, out _))
            return Fail(EnrollmentProofErrors.Malformed("proof"));

        if (!CompactJws.DecodeHeader(parts!).TryGetValue(out var header, out _))
            return Fail(EnrollmentProofErrors.Malformed("header"));

        if (!string.Equals(header!.Typ, ProofTyp, StringComparison.Ordinal))
            return Fail(EnrollmentProofErrors.Malformed("typ"));

        if (!AuthNConstants.AllowedAlgorithms.Contains(header.Alg))
            return Fail(EnrollmentProofErrors.Malformed("alg"));

        if (header.Kid.Length == 0)
            return Fail(EnrollmentProofErrors.Malformed("kid"));

        // Step 3: a header alg or kid that is not the request's.
        if (!string.Equals(header.Alg, request.Alg, StringComparison.Ordinal))
            return Fail(EnrollmentProofErrors.Mismatch("alg"));

        if (!string.Equals(header.Kid, request.Kid, StringComparison.Ordinal))
            return Fail(EnrollmentProofErrors.Mismatch("kid"));

        // Step 4: the signature, under the request's key and the verifier the request's alg chooses; nothing from the proof picks either.
        if (!_verifiers.TryGetValue(request.Alg, out var verifier)
            || !CompactJws.VerifySignature(parts!, verifier, new PublicKeyMaterial(request.Alg, request.Kid, request.PublicKey)))
            return Fail(EnrollmentProofErrors.NotPossessed());

        // Step 5: the payload, read now that it is signed. Malformed first (for every claim), then the three that must be the request's, in order, then the window.
        if (!CompactJws.ParsePayload(parts!, ReadClaims).TryGetValue(out var claims, out _))
            return Fail(EnrollmentProofErrors.Malformed("payload"));

        if (claims!.MalformedMember is { } malformed)
            return Fail(EnrollmentProofErrors.Malformed(malformed));

        if (!string.Equals(claims.AgentId, request.AgentId, StringComparison.Ordinal))
            return Fail(EnrollmentProofErrors.Mismatch("agent_id"));

        if (ThumbprintOf(request.Alg, request.PublicKey.Span) is not { } jkt)
            return Fail(EnrollmentProofErrors.Malformed("public_key"));

        if (!string.Equals(claims.Jkt, jkt, StringComparison.Ordinal))
            return Fail(EnrollmentProofErrors.Mismatch("jkt"));

        if (!string.Equals(claims.Aud, audience, StringComparison.Ordinal))
            return Fail(EnrollmentProofErrors.Mismatch("aud"));

        var skew = claims.Iat > now ? claims.Iat - now : now - claims.Iat;
        if (skew > AuthNConstants.MaxSkew)
            return Fail(EnrollmentProofErrors.Window());

        // Step 6: a nonce that is absent, is not a nonce value, or is not one the store reports current. Before the jti is consumed, so a stale nonce spends nothing.
        if (claims.Nonce is null || !DpopNonce.Parse(claims.Nonce).TryGetValue(out var nonce, out _))
            return Fail(EnrollmentProofErrors.NonceStale());

        var current = await _nonces.IsCurrentAsync(nonce!, cancellationToken).ConfigureAwait(false);
        if (!current.TryGetValue(out var isCurrent, out var nonceFault))
            return Result<Unit>.Fail(nonceFault!);

        if (!isCurrent)
            return Fail(EnrollmentProofErrors.NonceStale());

        // Step 7: the jti, consumed in the shared cache and retained as a DPoP proof's is. The caller reads the event log only after this returns, so a fault of the log spends the proof.
        var inserted = await _replay
            .TryInsertAsync(claims.Jti!, DpopProofValidator.RetainedUntil(claims.Iat, now), cancellationToken)
            .ConfigureAwait(false);
        if (!inserted.TryGetValue(out var first, out var replayFault))
            return Result<Unit>.Fail(replayFault!);

        return first ? Result<Unit>.Ok(Unit.Value) : Fail(EnrollmentProofErrors.Replayed());
    }

    private static Result<Unit> Fail(Error error) => Result<Unit>.Fail(error);

    /// <summary>The payload's claims, or the name of the first required claim that is absent or of the wrong type (agent_id, jkt, aud, iat, jti, in that order). The nonce is not among them: it is step 6's.</summary>
    private sealed record Claims(string? MalformedMember, string AgentId, string Jkt, string Aud, DateTimeOffset Iat, Jti? Jti, string? Nonce);

    private static Result<Claims> ReadClaims(JsonElement root)
    {
        string? malformed = null;
        string Text(string name)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString()!;
            malformed ??= name;
            return string.Empty;
        }

        var agentId = Text("agent_id");
        var jkt = Text("jkt");
        var aud = Text("aud");

        var iat = default(DateTimeOffset);
        if (NumericDate.ReadRequired(root, "iat").TryGetValue(out var readIat, out _)) iat = readIat;
        else malformed ??= "iat";

        Jti? jti = null;
        if (Jti.Parse(root.TryGetProperty("jti", out var jtiValue) && jtiValue.ValueKind == JsonValueKind.String ? jtiValue.GetString() : null).TryGetValue(out var parsedJti, out _)) jti = parsedJti;
        else malformed ??= "jti";

        var nonce = root.TryGetProperty("nonce", out var nonceValue) && nonceValue.ValueKind == JsonValueKind.String ? nonceValue.GetString() : null;
        return Result<Claims>.Ok(new Claims(malformed, agentId, jkt, aud, iat, jti, nonce));
    }

    /// <summary>
    /// The RFC 7638 thumbprint of the key as R4.34 renders it (the log binds it, the key set serves it), or null for material that is not a key of its algorithm: <c>EdDSA</c> is 32 raw bytes,
    /// <c>ES256</c> a whole SubjectPublicKeyInfo on P-256. The route has judged the key before the proof is read, so null is not reached through it.
    /// </summary>
    private static string? ThumbprintOf(string alg, ReadOnlySpan<byte> stored)
    {
        if (string.Equals(alg, "EdDSA", StringComparison.Ordinal))
            return stored.Length == 32 ? JwkThumbprint.Compute(new Jwk.OkpEd25519(stored.ToArray())) : null;

        if (!string.Equals(alg, "ES256", StringComparison.Ordinal)) return null;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(stored, out var read);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            if (read != stored.Length || parameters.Curve is not { IsNamed: true } curve || curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value) return null;
            return JwkThumbprint.Compute(new Jwk.EcP256(parameters.Q.X!, parameters.Q.Y!));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
