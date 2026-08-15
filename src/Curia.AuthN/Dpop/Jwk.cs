using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Dpop;

/// <summary>
/// Errata R4.28's two permitted public-key JWK shapes -- RFC 8037 octet key pairs for Ed25519,
/// RFC 7518 <c>EC</c> for P-256 -- and nothing else. CS-11: closed to this assembly by the
/// <see langword="private protected"/> constructor, with <see cref="Match{T}"/> as the
/// exhaustiveness guarantee; a third key shape (RSA, a different curve) has no representable
/// value here rather than silently falling through a switch's default arm.
///
/// The only place Stage C parses a JWK from the wire at all: a DPoP proof's <c>jwk</c> header
/// member (RFC 9449 §4.2) is self-describing key material the sender embeds directly in the
/// proof, unlike a registered signing key resolved by <c>kid</c> through
/// <see cref="Curia.AuthN.Ports.IJwsKeyResolver"/> (R5.10), which never touches JWK wire format
/// at this layer at all -- the resolver already returns <c>PublicKeyMaterial</c>.
/// </summary>
public abstract record Jwk
{
    private protected Jwk() { }

    /// <summary><c>kty:"OKP"</c>, <c>crv:"Ed25519"</c>. <see cref="X"/> is the raw 32-byte public
    /// key -- exactly the byte layout NSec's <c>KeyBlobFormat.RawPublicKey</c> expects, so no
    /// further transformation is needed to build a <c>PublicKeyMaterial</c> from it.</summary>
    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "CS-11's closed-hierarchy idiom nests every variant inside its abstract base " +
            "(mirrors Curia.Domain.AgentPublicKey's identical Ed25519Key/P256Key nesting for the same " +
            "R4.28 shapes) so the hierarchy reads as one closed set at the call site.")]
    public sealed record OkpEd25519(ReadOnlyMemory<byte> X) : Jwk;

    /// <summary><c>kty:"EC"</c>, <c>crv:"P-256"</c>. <see cref="X"/>/<see cref="Y"/> are the raw
    /// 32-byte curve coordinates RFC 7518 §6.2.1 specifies -- not the SPKI DER
    /// <see cref="Curia.Canon.Jws.PublicKeyMaterial.Public"/> expects for ES256; see
    /// <c>JwkPublicKey.ToPublicKeyMaterial</c> for that conversion.</summary>
    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "See OkpEd25519's identical justification just above.")]
    public sealed record EcP256(ReadOnlyMemory<byte> X, ReadOnlyMemory<byte> Y) : Jwk;

    public T Match<T>(Func<OkpEd25519, T> okpEd25519, Func<EcP256, T> ecP256)
    {
        ArgumentNullException.ThrowIfNull(okpEd25519);
        ArgumentNullException.ThrowIfNull(ecP256);

        return this switch
        {
            OkpEd25519 o => okpEd25519(o),
            EcP256 e => ecP256(e),
            _ => throw new InvalidOperationException($"Unreachable: {nameof(Jwk)} is closed to this assembly (CS-11)."),
        };
    }
}

/// <summary>Parses the two R4.28 JWK shapes from a DPoP proof's embedded <c>jwk</c> header
/// member. Attacker-controlled input at this point (the proof is not yet signature-verified when
/// the algorithm reads <c>proof.header.jwk</c>) -- every branch below is a <see cref="Result{T}"/>
/// failure, never a thrown exception or a length-mismatched key silently truncated/padded.</summary>
public static class JwkParser
{
    private const int Ed25519KeyLength = 32;
    private const int P256CoordinateLength = 32;

    public static Result<Jwk> Parse(JsonElement jwk)
    {
        if (jwk.ValueKind != JsonValueKind.Object)
            return Result<Jwk>.Fail(AuthNErrors.MalformedJwk("jwk must be a JSON object"));

        var kty = Jwt.CompactJws.ReadString(jwk, "kty");
        return kty switch
        {
            "OKP" => ParseOkp(jwk),
            "EC" => ParseEc(jwk),
            _ => Result<Jwk>.Fail(AuthNErrors.MalformedJwk($"unsupported kty '{kty}' (only OKP and EC are permitted, R4.28)")),
        };
    }

    private static Result<Jwk> ParseOkp(JsonElement jwk)
    {
        var crv = Jwt.CompactJws.ReadString(jwk, "crv");
        if (crv != "Ed25519")
            return Result<Jwk>.Fail(AuthNErrors.MalformedJwk($"unsupported crv '{crv}' for kty=OKP (only Ed25519 is permitted)"));

        if (!TryDecode(jwk, "x", Ed25519KeyLength, out var x, out var error))
            return Result<Jwk>.Fail(error!);

        return Result<Jwk>.Ok(new Jwk.OkpEd25519(x));
    }

    private static Result<Jwk> ParseEc(JsonElement jwk)
    {
        var crv = Jwt.CompactJws.ReadString(jwk, "crv");
        if (crv != "P-256")
            return Result<Jwk>.Fail(AuthNErrors.MalformedJwk($"unsupported crv '{crv}' for kty=EC (only P-256 is permitted)"));

        if (!TryDecode(jwk, "x", P256CoordinateLength, out var x, out var xError))
            return Result<Jwk>.Fail(xError!);

        if (!TryDecode(jwk, "y", P256CoordinateLength, out var y, out var yError))
            return Result<Jwk>.Fail(yError!);

        return Result<Jwk>.Ok(new Jwk.EcP256(x, y));
    }

    private static bool TryDecode(JsonElement jwk, string member, int expectedLength, out byte[] value, out Error? error)
    {
        var encoded = Jwt.CompactJws.ReadString(jwk, member);
        if (encoded.Length == 0 || !Base64Url.IsValid(encoded))
        {
            value = [];
            error = AuthNErrors.MalformedJwk($"'{member}' is missing or not base64url");
            return false;
        }

        var decoded = Base64Url.DecodeFromChars(encoded);
        if (decoded.Length != expectedLength)
        {
            value = [];
            error = AuthNErrors.MalformedJwk($"'{member}' must decode to exactly {expectedLength} bytes, got {decoded.Length}");
            return false;
        }

        value = decoded;
        error = null;
        return true;
    }
}
