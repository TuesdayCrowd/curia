using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Jws;
using NSec.Cryptography;

namespace Curia.Canon.Sodium;

/// <summary>
/// Ed25519 via libsodium. The only assembly in the solution linking native crypto (CS-6).
///
/// <para><b>This adapter owns what an <c>EdDSA</c> key is</b> (R4.15, R4.28): the raw 32-byte
/// Ed25519 public key, the form every consumer reads. <see cref="IsPublicKey"/> is that rule, asked
/// by <see cref="Verify"/>, the served key set and the enrollment route alike. To NSec the form is
/// exactly the length: any 32 bytes import, including ones that are not a curve point, under which
/// libsodium then verifies nothing.</para>
/// </summary>
public sealed class Ed25519Adapter : IContentSigner, IContentVerifier
{
    private static readonly SignatureAlgorithm Algorithm = SignatureAlgorithm.Ed25519;

    public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var privateKey = Key.Import(Algorithm, key.Private.Span, KeyBlobFormat.RawPrivateKey);
        return Algorithm.Sign(privateKey, input);
    }

    /// <summary>
    /// Verifies under <paramref name="key"/> only when it is an <c>EdDSA</c> key
    /// (<see cref="IsPublicKey"/>), and answers <see langword="false"/> for anything else, never a
    /// throw (CS-10). It threw on anything but 32 bytes, so an assertion whose header said
    /// <c>EdDSA</c> over any agent's <c>ES256</c> key answered 500.
    /// </summary>
    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(key);

        return Read(key.Public.Span, out var publicKey) && Algorithm.Verify(publicKey, input, sig);
    }

    /// <summary>R4.15 and R4.28: whether <paramref name="material"/> is an <c>EdDSA</c> public key, the raw 32 bytes.</summary>
    public static bool IsPublicKey(ReadOnlySpan<byte> material) => Read(material, out _);

    /// <summary>
    /// <paramref name="material"/> as an Ed25519 public key, through NSec's non-throwing import.
    /// </summary>
    private static bool Read(ReadOnlySpan<byte> material, [NotNullWhen(true)] out PublicKey? key)
    {
        return PublicKey.TryImport(Algorithm, material, KeyBlobFormat.RawPublicKey, out key);
    }
}
