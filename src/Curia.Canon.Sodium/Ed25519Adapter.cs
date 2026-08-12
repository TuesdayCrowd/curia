using Curia.Canon.Jws;
using NSec.Cryptography;

namespace Curia.Canon.Sodium;

/// <summary>Ed25519 via libsodium. The only assembly in the solution linking native crypto (CS-6).</summary>
public sealed class Ed25519Adapter : IContentSigner, IContentVerifier
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var privateKey = Key.Import(Algorithm, key.Private.Span, KeyBlobFormat.RawPrivateKey);
        return Algorithm.Sign(privateKey, input);
    }

    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(key);

        var publicKey = PublicKey.Import(Algorithm, key.Public.Span, KeyBlobFormat.RawPublicKey);
        return Algorithm.Verify(publicKey, input, sig);
    }
}
