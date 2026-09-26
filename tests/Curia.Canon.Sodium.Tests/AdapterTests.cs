using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Jws;
using Curia.Tests.Shared;
using NSec.Cryptography;
using Xunit;

namespace Curia.Canon.Sodium.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AdapterTests
{
    private static readonly byte[] Message = Encoding.UTF8.GetBytes("""{"a":1}""");

    /// <summary>What a question answered: <c>True</c>, <c>False</c>, or <c>THROWS</c> and the exception's type.</summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "A throw of any type is one of the outcomes a row renders, and is the defect some rows exist to show.")]
    private static string Outcome(Func<bool> question)
    {
        try
        {
            return question().ToString();
        }
        catch (Exception thrown)
        {
            return "THROWS " + thrown.GetType().Name;
        }
    }

    /// <summary>One line per row: whether the adapter calls the material a key, and whether it verifies under it.</summary>
    private static string Judged(Func<bool> isPublicKey, Func<bool> verify) =>
        $"IsPublicKey={Outcome(isPublicKey)}; Verify={Outcome(verify)}";

    /// <summary>
    /// R4.15: an <c>ES256</c> key is ECDSA on the curve named P-256, stored as its DER
    /// SubjectPublicKeyInfo with nothing after it (R4.28's form). Each row is refused as a key and
    /// verifies nothing, and none throws (CS-10). <c>p384-spki</c> carries a genuine P-384/SHA-256
    /// signature and the trailing-byte row a genuine P-256 one, so both would verify without the
    /// rule. The brainpool row is a genuine brainpoolP256r1 point, with P-256's coordinate length and
    /// key size and another curve's OID: on Linux, where OpenSSL imports it, only the OID clause
    /// refuses it; macOS cannot import it at all.
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("three-zero-bytes")]
    [InlineData("32-raw-bytes")]
    [InlineData("rsa-2048-spki")]
    [InlineData("p384-spki")]
    [InlineData("p256-spki-and-a-trailing-byte")]
    [InlineData("brainpoolP256r1-spki")]
    public void R4_15_Es256VerifiesUnderAP256SubjectPublicKeyInfoAndNothingElse(string material)
    {
        var (bytes, signature) = KeyMaterials.Build(material, Message);
        var key = new PublicKeyMaterial("ES256", "k", bytes);

        Assert.Equal(
            "IsPublicKey=False; Verify=False",
            Judged(() => Es256Adapter.IsPublicKey(bytes), () => new Es256Adapter().Verify(Message, signature, key)));
    }

    /// <summary>
    /// R4.15: an <c>EdDSA</c> key is the raw 32-byte Ed25519 public key (R4.28's form). Anything
    /// else is refused as a key and verifies nothing, and none throws (CS-10).
    /// </summary>
    [Theory]
    [InlineData("empty")]
    [InlineData("31-bytes")]
    [InlineData("33-bytes")]
    [InlineData("p256-spki")]
    public void R4_15_EdDsaVerifiesUnderA32ByteKeyAndNothingElse(string material)
    {
        var (bytes, signature) = KeyMaterials.Build(material, Message);
        var key = new PublicKeyMaterial("EdDSA", "k", bytes);

        Assert.Equal(
            "IsPublicKey=False; Verify=False",
            Judged(() => Ed25519Adapter.IsPublicKey(bytes), () => new Ed25519Adapter().Verify(Message, signature, key)));
    }

    /// <summary>
    /// The positive control for both theories above: an honest key of each algorithm is a key and
    /// verifies its own signature. A predicate that refused everything would pass both theories.
    /// </summary>
    [Fact]
    public void R4_15_AnHonestKeyOfEachAlgorithmIsAPublicKeyAndVerifies()
    {
        var (p256, p256Signature) = KeyMaterials.Build("p256-spki", Message);
        var es256Key = new PublicKeyMaterial("ES256", "k", p256);

        using var ed25519 = Key.Create(SignatureAlgorithm.Ed25519, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport,
        });
        var raw = ed25519.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var ed25519Signature = SignatureAlgorithm.Ed25519.Sign(ed25519, Message);
        var edDsaKey = new PublicKeyMaterial("EdDSA", "k", raw);

        Assert.Equal(
            "ES256: IsPublicKey=True; Verify=True. EdDSA: IsPublicKey=True; Verify=True",
            $"ES256: {Judged(() => Es256Adapter.IsPublicKey(p256), () => new Es256Adapter().Verify(Message, p256Signature, es256Key))}. " +
            $"EdDSA: {Judged(() => Ed25519Adapter.IsPublicKey(raw), () => new Ed25519Adapter().Verify(Message, ed25519Signature, edDsaKey))}");
    }

    [Fact]
    public void Ed25519SignsAndVerifies()
    {
        var algorithm = SignatureAlgorithm.Ed25519;
        using var key = Key.Create(algorithm, new KeyCreationParameters
        {
            ExportPolicy = KeyExportPolicies.AllowPlaintextExport
        });

        var adapter = new Ed25519Adapter();
        var signing = new SigningKey("EdDSA", "k", key.Export(KeyBlobFormat.RawPrivateKey));
        var publicKey = new PublicKeyMaterial("EdDSA", "k", key.PublicKey.Export(KeyBlobFormat.RawPublicKey));

        var sig = adapter.Sign(Message, signing);
        Assert.True(adapter.Verify(Message, sig, publicKey));
        Assert.False(adapter.Verify(Encoding.UTF8.GetBytes("""{"a":2}"""), sig, publicKey));
    }

    [Fact]
    public void Es256SignsAndVerifies()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var adapter = new Es256Adapter();
        var signing = new SigningKey("ES256", "k", ecdsa.ExportECPrivateKey());
        var publicKey = new PublicKeyMaterial("ES256", "k", ecdsa.ExportSubjectPublicKeyInfo());

        var sig = adapter.Sign(Message, signing);
        Assert.True(adapter.Verify(Message, sig, publicKey));
        Assert.False(adapter.Verify(Encoding.UTF8.GetBytes("""{"a":2}"""), sig, publicKey));
    }

    [Fact]
    public void Es256ProducesTheSixtyFourByteRawFormatJwsRequires()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var sig = new Es256Adapter().Sign(Message, new SigningKey("ES256", "k", ecdsa.ExportECPrivateKey()));
        Assert.Equal(64, sig.Length);   // R||S, not DER — RFC 7518 §3.4
    }
}
