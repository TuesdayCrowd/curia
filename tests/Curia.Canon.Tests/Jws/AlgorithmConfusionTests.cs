using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Canon.Tests.Jws;

/// <summary>
/// Algorithm confusion: the protected header names one algorithm and the key material another.
///
/// <para><b>Why this is a distinct check rather than a consequence of the others.</b> The header is
/// the attacker's — it travels with the signature — and the key comes from a published key set. If
/// the verifier is chosen by the header and the material is interpreted by the key set's own
/// declaration, the attacker picks which primitive reads somebody else's bytes. That is the classic
/// JWS confusion, and here it does not merely mislead: an <c>ES256</c> header over an
/// <c>OKP</c> key hands 32 raw bytes to <c>ImportSubjectPublicKeyInfo</c>, and an <c>EdDSA</c>
/// header over an <c>EC</c> key hands an SPKI blob to Ed25519. Both <b>throw</b> —
/// <c>ASN1 corrupted data</c> and <c>The key BLOB is not in the correct format</c> — out through a
/// method whose entire contract is that a bad signature is a value rather than an exception
/// (CS-10). A verifier that crashes on a chosen input is one an untrusted Forum can switch off.</para>
///
/// <para>Found while reviewing the MCP adapter's Stage 3, which is what made this path
/// agent-facing: <c>curia_verify</c> calls it on a JWKS and a signature the Forum serves.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AlgorithmConfusionTests
{
    private static readonly CanonicalBytes Canonical =
        CanonicalJson.Canonicalize(new JsonValue.Object([new("v", new JsonValue.Number(1))]))
            .Match(c => c, e => throw new InvalidOperationException(e.Type));

    private static DetachedJws Jws() => new(
        new Dictionary<string, IContentSigner>(StringComparer.Ordinal)
        {
            ["ES256"] = new Es256Adapter(),
            ["EdDSA"] = new Ed25519Adapter(),
        },
        new Dictionary<string, IContentVerifier>(StringComparer.Ordinal)
        {
            ["ES256"] = new Es256Adapter(),
            ["EdDSA"] = new Ed25519Adapter(),
        });

    /// <summary>The control: matched algorithms verify, so every refusal below is about the mismatch.</summary>
    [Theory]
    [InlineData("ES256")]
    [InlineData("EdDSA")]
    public void MatchedAlgorithmsVerify(string alg)
    {
        var (signing, verifying) = KeyPair(alg);

        var signature = Jws().Sign(Canonical, signing).Match(s => s, e => throw new InvalidOperationException(e.Type));

        Assert.True(Jws().Verify(Canonical, signature, verifying).TryGetValue(out _, out var error), error?.Type);
    }

    /// <summary>
    /// A header that names an algorithm the key does not is refused as a value, and named as a
    /// mismatch rather than as a bad signature — the two are different facts and only one of them
    /// says anything about the signer.
    /// </summary>
    [Theory]
    [InlineData("ES256", "EdDSA")]
    [InlineData("EdDSA", "ES256")]
    public void AHeaderThatDoesNotMatchTheKeysAlgorithmIsRefusedRatherThanThrown(string headerAlg, string keyAlg)
    {
        var (signing, _) = KeyPair(headerAlg);
        var (_, otherKey) = KeyPair(keyAlg);

        var signature = Jws().Sign(Canonical, signing).Match(s => s, e => throw new InvalidOperationException(e.Type));

        // The assertion that matters is that this returns at all. Before the check existed it threw
        // CryptographicException or FormatException, so a test asserting only on the Result would
        // have failed with an exception rather than reporting the defect.
        var result = Jws().Verify(Canonical, signature, otherKey);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/jws/alg-mismatch", error!.Type);
        Assert.Contains(headerAlg, error.Detail, StringComparison.Ordinal);
        Assert.Contains(keyAlg, error.Detail, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the refusal is not the same one an unsupported algorithm gets. A reader told
    /// "alg-not-allowed" would look for a configuration problem; the fact here is that the key set
    /// and the signature disagree, which is a fact about the Forum's answer.
    /// </summary>
    [Fact]
    public void AMismatchIsNotReportedAsAnUnsupportedAlgorithm()
    {
        var (signing, _) = KeyPair("ES256");
        var (_, ed) = KeyPair("EdDSA");

        var signature = Jws().Sign(Canonical, signing).Match(s => s, e => throw new InvalidOperationException(e.Type));

        Jws().Verify(Canonical, signature, ed).TryGetValue(out _, out var mismatch);

        var narrow = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal),
            new Dictionary<string, IContentVerifier>(StringComparer.Ordinal) { ["EdDSA"] = new Ed25519Adapter() });

        narrow.Verify(Canonical, signature, ed).TryGetValue(out _, out var unsupported);

        Assert.Equal("curia/jws/alg-mismatch", mismatch!.Type);
        Assert.Equal("curia/jws/alg-not-allowed", unsupported!.Type);
        Assert.NotEqual(mismatch.Type, unsupported.Type);
    }

    private static (SigningKey Signing, PublicKeyMaterial Verifying) KeyPair(string alg)
    {
        if (alg == "ES256")
        {
            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return (
                new SigningKey("ES256", "k-es256", ecdsa.ExportECPrivateKey()),
                new PublicKeyMaterial("ES256", "k-es256", ecdsa.ExportSubjectPublicKeyInfo()));
        }

        var seed = SHA256.HashData(Encoding.UTF8.GetBytes("conformance-ed25519-seed"));
        using var key = NSec.Cryptography.Key.Import(
            NSec.Cryptography.SignatureAlgorithm.Ed25519,
            seed,
            NSec.Cryptography.KeyBlobFormat.RawPrivateKey,
            new NSec.Cryptography.KeyCreationParameters
            {
                ExportPolicy = NSec.Cryptography.KeyExportPolicies.AllowPlaintextExport,
            });

        return (
            new SigningKey("EdDSA", "k-eddsa", key.Export(NSec.Cryptography.KeyBlobFormat.RawPrivateKey)),
            new PublicKeyMaterial("EdDSA", "k-eddsa", key.PublicKey.Export(NSec.Cryptography.KeyBlobFormat.RawPublicKey)));
    }
}
