using System.Security.Cryptography;
using Curia.Canon.Jws;

namespace Curia.Application.Tests;

/// <summary>
/// A real P-256 key under a given <c>kid</c>, stored as R4.28 stores an <c>ES256</c> key: its DER
/// SubjectPublicKeyInfo. <c>EnrollAgent</c> records an enrollment's key as its public JWK (R4.34,
/// errata G16), so a test that records one needs material the renderer calls a key, and a fresh one
/// each call, so two calls under one <c>kid</c> are two different keys.
/// </summary>
internal static class TestKeys
{
    internal static PublicKeyMaterial Es256(string kid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var material = new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo());
        KnownKeys.Remember(material, ecdsa.ExportPkcs8PrivateKey());
        return material;
    }
}

/// <summary>
/// The private halves of the keys the Application tests made, so a fixture that appends a lifecycle row by hand can sign it as the key's owner does (errata G68: a row the Forum honours
/// verifies, so a fixture that wants one honoured signs it). Looked up by the public bytes, or by <c>kid</c> (the latest key made under it).
/// </summary>
internal static class KnownKeys
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> ByPublic = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SigningKey> LatestByKid = new(StringComparer.Ordinal);

    internal static void Remember(PublicKeyMaterial material, byte[] pkcs8)
    {
        ByPublic[Convert.ToHexString(material.Public.Span)] = pkcs8;
        LatestByKid[material.Kid] = new SigningKey("ES256", material.Kid, pkcs8);
    }

    /// <summary>The key that owns <paramref name="material"/>, or the latest made under its <c>kid</c> when the bytes were not made here.</summary>
    internal static SigningKey Of(PublicKeyMaterial material) =>
        ByPublic.TryGetValue(Convert.ToHexString(material.Public.Span), out var pkcs8)
            ? new SigningKey("ES256", material.Kid, pkcs8)
            : Latest(material.Kid);

    /// <summary>The latest key made under <paramref name="kid"/>.</summary>
    internal static SigningKey Latest(string kid) =>
        LatestByKid.TryGetValue(kid, out var key) ? key : throw new InvalidOperationException($"no test key was made under kid={kid}");
}
