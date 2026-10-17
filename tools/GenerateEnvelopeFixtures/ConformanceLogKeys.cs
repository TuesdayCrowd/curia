using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Json;

namespace GenerateEnvelopeFixtures;

/// <summary>A fixed P-256 key in every shape the compromise vectors need.</summary>
internal sealed record P256LogKey(string Kid, byte[] X32, byte[] Y32, byte[] EcPrivateKeyDer, byte[] Spki, JsonValue Jwk);

/// <summary>
/// The conformance log key and the unpublished signer (keys that end, Task 2.0). A compromise declaration is ES256 by
/// construction: the operator's log key is P-256 only (<c>LogSigningKey.FromPem</c> refuses anything else), so no Ed25519
/// seed and not the <c>acta/head-entry</c> vector's kid.
///
/// <para>TEST KEYS ONLY. The private scalars below are published on purpose: they sign nothing but conformance vectors,
/// and no deployment may trust either key. A fixed scalar makes the public key and the kid reproducible; it does not make
/// an ES256 signature reproducible (signing is randomized), so a declaration's signature bytes are pinned by the vector
/// that carries them and the generator never re-signs one (see <c>LifecycleCases.PinnedDeclaration</c>).</para>
///
/// <para>The kid is the RFC 7638 JWK thumbprint of the public key, computed as <c>LogSigningKey.FromPem</c> computes it
/// (<c>JwkThumbprint.Compute</c>): production derives a log key's kid and never configures one, and a vector whose kid no
/// real log key could carry would test a shape the system cannot produce. This tool references <c>Curia.Canon</c>,
/// <c>Curia.Canon.Sodium</c> and <c>Curia.Domain.Primitives</c> only, not <c>Curia.AuthN</c>, so the RFC 7638 form is
/// computed here, the kid is pinned as a literal, and the generator asserts the two are equal.</para>
/// </summary>
internal static class ConformanceLogKeys
{
    // Test key only. SHA-256 of the ASCII text "curia-conformance-log-key-p256".
    private const string LogScalarHex = "c82905a0459cc186f88844b01f7ceee5fb3cefa08602b006bd54ba9c80aef7b1";

    // Test key only. SHA-256 of the ASCII text "curia-conformance-unpublished-signer-p256". No log.key entry publishes it.
    private const string UnpublishedScalarHex = "d58f9906b827d3c0c5a7b0040fc10192b48745b192ba5c3d61e2ca46971b5305";

    private const string PinnedLogKid = "qHPbhs_9o0jxVndzhwI7c3RLiGYoec5rTmfEtwT5j30";
    private const string PinnedUnpublishedKid = "NxiuQ5uF-fNn1_evbt7401ASmUe19wBeG7z7An5fXlk";

    // Lazy, so that --log-key-fixture can print the thumbprints before the literals are pinned.
    private static readonly Lazy<P256LogKey> LogKey = new(() => Build(LogScalarHex, PinnedLogKid));
    private static readonly Lazy<P256LogKey> UnpublishedKey = new(() => Build(UnpublishedScalarHex, PinnedUnpublishedKid));

    public static P256LogKey Log => LogKey.Value;

    public static P256LogKey Unpublished => UnpublishedKey.Value;

    /// <summary>The RFC 7638 thumbprints, for pinning the literals above: printed by <c>--log-key-fixture</c>.</summary>
    public static (string Log, string Unpublished) ComputedKids() =>
        (Thumbprint(Derive(LogScalarHex)), Thumbprint(Derive(UnpublishedScalarHex)));

    private static P256LogKey Build(string scalarHex, string pinnedKid)
    {
        var parameters = Derive(scalarHex);
        var computed = Thumbprint(parameters);
        if (!string.Equals(computed, pinnedKid, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"the pinned log-key kid {pinnedKid} is not the key's RFC 7638 thumbprint {computed}");
        }

        using var ecdsa = ECDsa.Create(parameters);
        var x = parameters.Q.X!;
        var y = parameters.Q.Y!;
        var jwk = JsonBuilders.Obj(
            ("alg", JsonBuilders.Str("ES256")),
            ("crv", JsonBuilders.Str("P-256")),
            ("kid", JsonBuilders.Str(computed)),
            ("kty", JsonBuilders.Str("EC")),
            ("use", JsonBuilders.Str("sig")),
            ("x", JsonBuilders.Str(Jwk.Base64UrlEncode(x))),
            ("y", JsonBuilders.Str(Jwk.Base64UrlEncode(y))));
        return new P256LogKey(computed, x, y, ecdsa.ExportECPrivateKey(), ecdsa.ExportSubjectPublicKeyInfo(), jwk);
    }

    private static ECParameters Derive(string scalarHex)
    {
        using var candidate = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Convert.FromHexString(scalarHex),
        });
        return candidate.ExportParameters(includePrivateParameters: true);
    }

    /// <summary>RFC 7638 section 3.2 over an EC key: the required members, sorted, no whitespace.</summary>
    private static string Thumbprint(ECParameters parameters)
    {
        var json = $$"""{"crv":"P-256","kty":"EC","x":"{{Jwk.Base64UrlEncode(parameters.Q.X!)}}","y":"{{Jwk.Base64UrlEncode(parameters.Q.Y!)}}"}""";
        return Jwk.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
}
