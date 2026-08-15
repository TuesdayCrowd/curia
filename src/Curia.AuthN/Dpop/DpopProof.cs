using System.Text.Json;
using Curia.AuthN.Jwt;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Dpop;

/// <summary>The DPoP proof's header fields Phase 4 pins before touching the embedded key or the
/// signature (errata A17's <c>typ</c> addition, and Stage C's own alg pin -- see the Stage C
/// report). Deliberately not <see cref="Curia.AuthN.Jwt.CompactJwsHeader"/>: that type has no
/// <c>jwk</c> member, which a DPoP proof header always carries and an access token or client
/// assertion header never does.</summary>
public sealed record DpopProofHeader(string Alg, string Typ)
{
    internal static Result<DpopProofHeader> Parse(JsonElement root) =>
        Result<DpopProofHeader>.Ok(new DpopProofHeader(
            Alg: CompactJws.ReadString(root, "alg"),
            Typ: CompactJws.ReadString(root, "typ")));

    /// <summary>Reads just the embedded public key, separately from <see cref="Parse"/> so Phase 4
    /// can pin <c>typ</c>/<c>alg</c> first and only then pay for parsing and validating the key
    /// material -- the same "cheap, header-only checks before anything expensive" ordering the
    /// printed algorithm itself uses (kid resolution and signature verification both come after
    /// the header pins in Phase 1).</summary>
    internal static Result<Jwk> ParseJwk(JsonElement root)
    {
        if (!root.TryGetProperty("jwk", out var jwk) || jwk.ValueKind != JsonValueKind.Object)
            return Result<Jwk>.Fail(AuthNErrors.MalformedJwk("missing jwk header parameter"));

        return JwkParser.Parse(jwk);
    }
}

/// <summary>RFC 9449 §4.2's proof claims. <see cref="Ath"/> is only present (and only checked)
/// when the proof accompanies a protected-resource request bound to an access token -- exactly
/// <see cref="AccessTokenValidator"/>'s Phase 4 use, the only place this type is constructed.
/// <see cref="Nonce"/> is optional on the wire; whether its absence is itself a failure is a
/// caller decision (R5.19 is a SHOULD, scoped to write paths) -- see
/// <see cref="AccessTokenValidator"/>'s nonce check.</summary>
public sealed record DpopProofClaims(
    string Htm,
    string Htu,
    DateTimeOffset Iat,
    string Jti,
    string? Ath,
    string? Nonce)
{
    internal static Result<DpopProofClaims> Parse(JsonElement root)
    {
        if (!NumericDate.ReadRequired(root, "iat").TryGetValue(out var iat, out var iatError))
            return Result<DpopProofClaims>.Fail(iatError!);

        return Result<DpopProofClaims>.Ok(new DpopProofClaims(
            Htm: CompactJws.ReadString(root, "htm"),
            Htu: CompactJws.ReadString(root, "htu"),
            Iat: iat,
            Jti: CompactJws.ReadString(root, "jti"),
            Ath: root.TryGetProperty("ath", out var ath) && ath.ValueKind == JsonValueKind.String ? ath.GetString() : null,
            Nonce: root.TryGetProperty("nonce", out var nonce) && nonce.ValueKind == JsonValueKind.String ? nonce.GetString() : null));
    }
}
