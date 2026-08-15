using System.Buffers.Text;
using System.Security.Cryptography;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using NSec.Cryptography;

namespace Curia.AuthN.Tests.Support;

/// <summary>An Ed25519 or ES256 test keypair in every shape this project's tests need: the
/// <see cref="Curia.Canon.Jws.IContentSigner"/>/<see cref="SigningKey"/> pair to actually sign a
/// JWT, the <see cref="PublicKeyMaterial"/> shape an <c>IJwsKeyResolver</c> hands back, and (for
/// DPoP proofs) the RFC 8037/7518 JWK JSON object the key would appear as on the wire.</summary>
internal sealed record TestKeyPair(
    string Alg,
    string Kid,
    IContentSigner Signer,
    SigningKey SigningKey,
    PublicKeyMaterial PublicKey,
    Dictionary<string, object> Jwk,
    string Thumbprint);

/// <summary>Builds real Ed25519/ES256 keypairs via the same libraries
/// <c>Curia.Canon.Sodium.Tests.AdapterTests</c> already uses (NSec for Ed25519, the BCL's
/// <c>ECDsa</c> for ES256) -- signatures in this test project are genuine, not stubbed, so a
/// verifier bug (wrong signing input, wrong key encoding) fails these tests the same way it
/// would fail against a real agent.</summary>
internal static class TestKeys
{
    public static TestKeyPair Ed25519(string kid = "issuer-2026-Q3")
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });

        var privateBytes = key.Export(KeyBlobFormat.RawPrivateKey);
        var publicBytes = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);

        return new TestKeyPair(
            Alg: "EdDSA",
            Kid: kid,
            Signer: new Ed25519Adapter(),
            SigningKey: new SigningKey("EdDSA", kid, privateBytes),
            PublicKey: new PublicKeyMaterial("EdDSA", kid, publicBytes),
            Jwk: new Dictionary<string, object>
            {
                ["kty"] = "OKP",
                ["crv"] = "Ed25519",
                ["x"] = Base64Url.EncodeToString(publicBytes),
            },
            Thumbprint: TestThumbprint.ForEd25519(publicBytes));
    }

    public static TestKeyPair Es256(string kid = "issuer-es256")
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);

        return new TestKeyPair(
            Alg: "ES256",
            Kid: kid,
            Signer: new Es256Adapter(),
            SigningKey: new SigningKey("ES256", kid, ecdsa.ExportECPrivateKey()),
            PublicKey: new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo()),
            Jwk: new Dictionary<string, object>
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = Base64Url.EncodeToString(parameters.Q.X!),
                ["y"] = Base64Url.EncodeToString(parameters.Q.Y!),
            },
            Thumbprint: TestThumbprint.ForP256(parameters.Q.X!, parameters.Q.Y!));
    }

    /// <summary>Both algorithms' <see cref="IContentVerifier"/>, keyed by <c>alg</c> exactly the
    /// way <see cref="AccessTokenValidationContext.VerifiersByAlg"/> expects -- built once so
    /// every test context can share the same allow-list dictionary shape production code will use.</summary>
    public static IReadOnlyDictionary<string, IContentVerifier> Verifiers() =>
        new Dictionary<string, IContentVerifier>(StringComparer.Ordinal)
        {
            ["EdDSA"] = new Ed25519Adapter(),
            ["ES256"] = new Es256Adapter(),
        };
}
