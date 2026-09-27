using System.Buffers.Text;
using System.Security.Cryptography;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Canon.Jws;

/// <summary>
/// A registered key as the public JWK the Forum publishes (R4.28): the one rendering that the key
/// set serves and that a key-binding entry carries into the log (R4.34, errata G16), so "published"
/// and "bound in the log" are one computation and cannot come apart.
///
/// <para><b>Members, in the order the key set has always served them:</b> <c>kty</c>, <c>crv</c>,
/// <c>alg</c>, <c>kid</c>, <c>x</c>, and for <c>ES256</c> <c>y</c>. RFC 8037 §2 gives Ed25519 the
/// octet-key-pair form with one coordinate; RFC 7518 §6.2.1 gives P-256 the <c>EC</c> form with two.
/// Order carries no meaning once a leaf is canonicalized (R6.46). The key set's served bytes were
/// compared with 05f56f4's bytes by a review probe; no test pins them.</para>
///
/// <para><b>What this does not decide.</b> Whether material is a key of its algorithm is the rule
/// the adapter that verifies with it owns (<c>Es256Adapter.IsPublicKey</c>,
/// <c>Ed25519Adapter.IsPublicKey</c>), and every caller that publishes or binds a key asks that rule
/// first. This method refuses what it cannot render -- anything but 32 bytes under <c>EdDSA</c>,
/// and anything but a whole DER SubjectPublicKeyInfo on the curve named P-256 under <c>ES256</c> --
/// and <c>PublicJwkTests</c> holds the two to the same answer on every material
/// <c>KeyMaterials</c> names.</para>
/// </summary>
public static class PublicJwk
{
    /// <summary>The slug of every refusal here.</summary>
    public const string NotRenderableType = "curia/canon/key-not-renderable";

    /// <summary>
    /// <paramref name="key"/> as the public JWK the key set publishes, or a refusal naming the
    /// algorithm. Never throws for material: a stored row that is not a key is answered, not failed.
    /// </summary>
    public static Result<JsonValue.Object> Of(PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(key.Kid);

        return key.Alg switch
        {
            "EdDSA" => key.Public.Length == 32
                ? Result<JsonValue.Object>.Ok(Render("OKP", "Ed25519", key, key.Public.Span, null))
                : Result<JsonValue.Object>.Fail(NotRenderable(key.Alg)),
            "ES256" => Es256(key),
            _ => Result<JsonValue.Object>.Fail(NotRenderable(key.Alg)),
        };
    }

    /// <summary>
    /// Whether two public JWKs are the same key under the same name: their canonical forms (RFC 8785)
    /// are byte-identical. Both sides of every comparison in this solution come from <see cref="Of"/>
    /// or from a log entry <see cref="Of"/> wrote, so no member a publisher might add is in either.
    /// </summary>
    public static bool SameKey(JsonValue.Object left, JsonValue.Object right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return CanonicalJson.Canonicalize(left).TryGetValue(out var l, out _)
            && CanonicalJson.Canonicalize(right).TryGetValue(out var r, out _)
            && l.Span.SequenceEqual(r.Span);
    }

    /// <summary>
    /// The coordinates are recovered by importing the SubjectPublicKeyInfo rather than by slicing the
    /// DER by offset, which works until an encoder emits a legal variation and then silently yields a
    /// wrong key. The curve is judged by its OID, as the verifier judges it: brainpoolP256r1 and
    /// secp256k1 have 32-byte coordinates too.
    /// </summary>
    private static Result<JsonValue.Object> Es256(PublicKeyMaterial key)
    {
        using var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(key.Public.Span, out var read);
            var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
            if (read != key.Public.Length
                || parameters.Curve is not { IsNamed: true } curve
                || curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value)
                return Result<JsonValue.Object>.Fail(NotRenderable(key.Alg));

            return Result<JsonValue.Object>.Ok(Render("EC", "P-256", key, parameters.Q.X!, parameters.Q.Y!));
        }
        catch (CryptographicException)
        {
            return Result<JsonValue.Object>.Fail(NotRenderable(key.Alg));
        }
        catch (PlatformNotSupportedException)
        {
            return Result<JsonValue.Object>.Fail(NotRenderable(key.Alg));
        }
    }

    private static JsonValue.Object Render(string kty, string crv, PublicKeyMaterial key, ReadOnlySpan<byte> x, byte[]? y)
    {
        var members = new List<KeyValuePair<string, JsonValue>>
        {
            new("kty", new JsonValue.String(kty)),
            new("crv", new JsonValue.String(crv)),
            new("alg", new JsonValue.String(key.Alg)),
            new("kid", new JsonValue.String(key.Kid)),
            new("x", new JsonValue.String(Base64Url.EncodeToString(x))),
        };

        if (y is not null)
            members.Add(new("y", new JsonValue.String(Base64Url.EncodeToString(y))));

        return new JsonValue.Object([.. members]);
    }

    private static Error NotRenderable(string alg) => new(
        NotRenderableType,
        "The material is not a public key the Forum can publish under its algorithm",
        $"alg={alg}");
}
