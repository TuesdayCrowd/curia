using System.Security.Cryptography;
using Curia.Canon;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain.Primitives;
using NSec.Cryptography;

namespace GenerateEnvelopeFixtures;

/// <summary>Ed25519 key material: seed (private) and the raw public key it derives.</summary>
internal sealed record Ed25519KeyPair(byte[] Seed32, byte[] Public32);

/// <summary>
/// P-256 key material in every shape this tool needs: the raw JWK coordinates
/// (<see cref="D32"/>, <see cref="X32"/>, <see cref="Y32"/>) and the SEC1 DER blob
/// <see cref="Es256Adapter"/> actually signs with (RFC 7518 section 3.4 needs the
/// fixed-width R||S signature, not DER, but the *private key* input to .NET's ECDsa is
/// DER SEC1).
/// </summary>
internal sealed record Es256KeyPair(byte[] D32, byte[] X32, byte[] Y32, byte[] EcPrivateKeyDer);

/// <summary>
/// Key generation and end-to-end sign/verify, mirroring the exact adapter usage pattern
/// already proven in tests/Curia.Canon.Sodium.Tests/AdapterTests.cs -- no new crypto
/// plumbing invented here, only reuse of the shipped Curia.Canon(.Sodium) surface.
/// </summary>
internal static class Signing
{
    private static readonly DetachedJws Jws = new(
        new Dictionary<string, IContentSigner> { ["EdDSA"] = new Ed25519Adapter(), ["ES256"] = new Es256Adapter() },
        new Dictionary<string, IContentVerifier> { ["EdDSA"] = new Ed25519Adapter(), ["ES256"] = new Es256Adapter() });

    public static Ed25519KeyPair NewEd25519()
    {
        using var key = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        return new Ed25519KeyPair(
            key.Export(KeyBlobFormat.RawPrivateKey),
            key.PublicKey.Export(KeyBlobFormat.RawPublicKey));
    }

    public static Es256KeyPair NewEs256()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p = ecdsa.ExportParameters(includePrivateParameters: true);
        return new Es256KeyPair(p.D!, p.Q.X!, p.Q.Y!, ecdsa.ExportECPrivateKey());
    }

    public static CanonicalBytes Canonicalize(JsonValue.Object envelope) =>
        CanonicalJson.CanonicalizeEnvelope(new EnvelopeDocument(envelope))
            .Match(b => b, e => throw new InvalidOperationException($"canonicalize failed: {e.Type}"));

    public static JwsSignature Sign(CanonicalBytes canonical, string alg, string kid, byte[] privateKeyBytes) =>
        Jws.Sign(canonical, new SigningKey(alg, kid, privateKeyBytes))
            .Match(s => s, e => throw new InvalidOperationException($"sign failed: {e.Type}"));

    /// <summary>The independent read-back verification path Program's report drives.</summary>
    public static Result<VerifiedContent> Verify(CanonicalBytes canonical, JwsSignature sig, string alg, string kid, byte[] publicKeyBytes) =>
        Jws.Verify(canonical, sig, new PublicKeyMaterial(alg, kid, publicKeyBytes));

    public static Result<JwsProtectedHeader> ReadHeader(JwsSignature sig) => DetachedJws.ReadProtectedHeader(sig);

    /// <summary>
    /// A deterministic, plausible-looking SHA-256 hex digest for illustrative envelope
    /// content (<c>prev</c>, a <c>refs</c> "post" target) -- computed, not hand-typed, so
    /// there is no risk of a mistyped hex literal, and reproducible across regenerations.
    /// </summary>
    public static string Sha256Hex(string label) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(label)));
}
