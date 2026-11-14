using System.Buffers.Text;
using System.Security.Cryptography;
using Curia.Canon.Json;
using Curia.Canon.Jws;

namespace Curia.Application.Credentials;

/// <summary>
/// A key in R6.62's closed form (errata G68, R6.64, M15), read from a JWK object as the entry states it: an object carrying a string <c>kid</c> and the members of one of two keys, an
/// Ed25519 key (<c>alg</c> <c>EdDSA</c>, <c>kty</c> <c>OKP</c>, <c>crv</c> <c>Ed25519</c>, a coordinate <c>x</c>) or an ES256 key (<c>alg</c> <c>ES256</c>, <c>kty</c> <c>EC</c>,
/// <c>crv</c> <c>P-256</c>, coordinates <c>x</c> and <c>y</c>). A coordinate is the 43 characters of the base64url alphabet, the last one of <c>A E I M Q U Y c g k o s w 0 4 8</c>, that
/// encode 32 octets without padding. Whether a point lies on its curve is not asked here: it is asked of a key that signs, at the signature (<see cref="Material"/>), so a declaration
/// key that is off its curve is still a key the log published, and a log key that is.
///
/// <para>The members beside these (<c>use</c>, <c>valid_from</c>, anything else) are not part of the key.</para>
/// </summary>
internal sealed record ClosedJwk(string Alg, string Kty, string Crv, string X, string? Y)
{
    /// <summary>The key <paramref name="jwk"/> states under <paramref name="kid"/>, or null when it is not a key of the closed form or carries another <c>kid</c>.</summary>
    internal static ClosedJwk? Read(JsonValue.Object jwk, string kid)
    {
        ArgumentNullException.ThrowIfNull(jwk);
        ArgumentNullException.ThrowIfNull(kid);

        if (!string.Equals(Text(jwk, "kid"), kid, StringComparison.Ordinal)) return null;

        var alg = Text(jwk, "alg");
        var kty = Text(jwk, "kty");
        var crv = Text(jwk, "crv");
        var x = Text(jwk, "x");
        var y = Text(jwk, "y");

        return (alg, kty, crv) switch
        {
            ("EdDSA", "OKP", "Ed25519") when IsCoordinate(x) => new ClosedJwk(alg, kty, crv, x!, null),
            ("ES256", "EC", "P-256") when IsCoordinate(x) && IsCoordinate(y) => new ClosedJwk(alg, kty, crv, x!, y),
            _ => null,
        };
    }

    /// <summary>Whether this and <paramref name="other"/> are one public key: <c>kty</c>, <c>crv</c>, <c>x</c> and <c>y</c> as written (R6.62).</summary>
    internal bool SamePublicKey(ClosedJwk other) =>
        string.Equals(Kty, other.Kty, StringComparison.Ordinal)
        && string.Equals(Crv, other.Crv, StringComparison.Ordinal)
        && string.Equals(X, other.X, StringComparison.Ordinal)
        && string.Equals(Y, other.Y, StringComparison.Ordinal);

    /// <summary>The material a signature is verified under, or null when the coordinates are not a point on the curve the key names (an off-curve key verifies nothing).</summary>
    internal PublicKeyMaterial? Material(string kid)
    {
        ArgumentNullException.ThrowIfNull(kid);

        var x = Base64Url.DecodeFromChars(X);
        if (string.Equals(Alg, "EdDSA", StringComparison.Ordinal)) return new PublicKeyMaterial("EdDSA", kid, x);

        try
        {
            var parameters = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = new ECPoint { X = x, Y = Base64Url.DecodeFromChars(Y!) },
            };
            using var ecdsa = ECDsa.Create(parameters);
            return new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo());
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool IsCoordinate(string? coordinate)
    {
        if (coordinate is not { Length: 43 } || !Base64Url.IsValid(coordinate)) return false;

        // The 43rd character carries four bits of padding that must be zero, so the text is the one spelling of its 32 octets.
        var octets = Base64Url.DecodeFromChars(coordinate);
        return octets.Length == 32 && string.Equals(Base64Url.EncodeToString(octets), coordinate, StringComparison.Ordinal);
    }

    private static string? Text(JsonValue.Object o, string name)
    {
        foreach (var m in o.Members)
            if (string.Equals(m.Key, name, StringComparison.Ordinal))
                return m.Value is JsonValue.String s ? s.Value : null;
        return null;
    }
}
