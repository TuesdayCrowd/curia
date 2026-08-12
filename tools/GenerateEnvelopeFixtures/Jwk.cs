using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GenerateEnvelopeFixtures;

/// <summary>
/// JWK encode/decode for the two shapes errata D4 pins: RFC 8037 octet-key-pair for
/// Ed25519 (<c>kty: "OKP"</c>, single coordinate <c>x</c>), and RFC 7518 <c>EC</c> for
/// ES256/P-256 (<c>x</c>/<c>y</c>). <c>jwks.json</c> and <c>private-keys.json</c> are not
/// envelope content and are not run through the ADMIT phase or Curia's JsonValue tree --
/// they are local, operator-supplied verifier input, so plain System.Text.Json is used
/// directly rather than round-tripping through Curia.Canon.Json.
/// </summary>
internal static class Jwk
{
    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Base64UrlDecode(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        var padded = s.Length % 4 == 0 ? s : s + new string('=', 4 - (s.Length % 4));
        return Convert.FromBase64String(padded);
    }

    // ---- OKP (Ed25519), RFC 8037 -------------------------------------------------

    public static JsonObject OkpPublic(string kid, ReadOnlySpan<byte> publicKey32)
    {
        var o = new JsonObject
        {
            ["kty"] = "OKP",
            ["crv"] = "Ed25519",
            ["kid"] = kid,
            ["alg"] = "EdDSA",
            ["use"] = "sig",
            ["x"] = Base64UrlEncode(publicKey32),
        };
        return o;
    }

    public static JsonObject OkpPrivate(string kid, ReadOnlySpan<byte> publicKey32, ReadOnlySpan<byte> seed32, string? role = null)
    {
        var o = OkpPublic(kid, publicKey32);
        o["d"] = Base64UrlEncode(seed32);
        if (role is not null) o["role"] = role;
        return o;
    }

    // ---- EC P-256 (ES256), RFC 7518 ------------------------------------------------

    public static JsonObject EcPublic(string kid, ReadOnlySpan<byte> x32, ReadOnlySpan<byte> y32)
    {
        var o = new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["kid"] = kid,
            ["alg"] = "ES256",
            ["use"] = "sig",
            ["x"] = Base64UrlEncode(x32),
            ["y"] = Base64UrlEncode(y32),
        };
        return o;
    }

    public static JsonObject EcPrivate(string kid, ReadOnlySpan<byte> x32, ReadOnlySpan<byte> y32, ReadOnlySpan<byte> d32, string? role = null)
    {
        var o = EcPublic(kid, x32, y32);
        o["d"] = Base64UrlEncode(d32);
        if (role is not null) o["role"] = role;
        return o;
    }

    public static JsonObject KeySet(params ReadOnlySpan<JsonObject> keys)
    {
        var arr = new JsonArray();
        foreach (var k in keys) arr.Add(k);
        return new JsonObject { ["keys"] = arr };
    }

    /// <summary>
    /// UnsafeRelaxedJsonEscaping is "unsafe" only for HTML-embedding contexts (it stops
    /// escaping '+', apostrophe, etc.); these are conformance fixture files on disk, never
    /// interpolated into HTML, so plain readable JSON is the right choice here.
    /// </summary>
    public static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ToJsonText(JsonObject o) => o.ToJsonString(PrettyOptions);

    /// <summary>
    /// Finds the JWK entry matching <paramref name="kid"/> in a parsed <c>jwks.json</c>
    /// ("keys": [...]) document and reconstructs the raw <see cref="Curia.Canon.Jws.PublicKeyMaterial"/>
    /// bytes each adapter expects: 32 raw bytes for OKP (Ed25519Adapter), and an X.509
    /// SubjectPublicKeyInfo DER blob for EC (Es256Adapter, which imports SPKI -- see
    /// Es256Adapter.Verify). This is independent reconstruction from the published JWK
    /// coordinates, not reuse of any in-memory key from generation.
    /// </summary>
    public static byte[] ResolvePublicKeyBytes(JsonElement jwks, string kid)
    {
        if (!jwks.TryGetProperty("keys", out var keys) || keys.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("jwks.json has no \"keys\" array");

        foreach (var entry in keys.EnumerateArray())
        {
            if (!entry.TryGetProperty("kid", out var kidProp) || kidProp.GetString() != kid)
                continue;

            var kty = entry.GetProperty("kty").GetString();
            return kty switch
            {
                "OKP" => Base64UrlDecode(entry.GetProperty("x").GetString()!),
                "EC" => EcPublicKeyInfo(
                    Base64UrlDecode(entry.GetProperty("x").GetString()!),
                    Base64UrlDecode(entry.GetProperty("y").GetString()!)),
                _ => throw new InvalidOperationException($"unsupported kty '{kty}'"),
            };
        }

        throw new InvalidOperationException($"no JWK with kid '{kid}' in jwks.json");
    }

    private static byte[] EcPublicKeyInfo(byte[] x32, byte[] y32)
    {
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x32, Y = y32 },
        };
        using var ecdsa = ECDsa.Create(parameters);
        return ecdsa.ExportSubjectPublicKeyInfo();
    }
}
