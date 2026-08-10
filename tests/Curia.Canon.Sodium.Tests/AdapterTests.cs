using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Jws;
using NSec.Cryptography;
using Xunit;

namespace Curia.Canon.Sodium.Tests;

public sealed class AdapterTests
{
    private static readonly byte[] Message = Encoding.UTF8.GetBytes("""{"a":1}""");

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
