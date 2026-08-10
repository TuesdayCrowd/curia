using System.Security.Cryptography;
using Curia.Canon.Jws;

namespace Curia.Canon.Sodium;

/// <summary>
/// ECDSA P-256 with SHA-256 via the BCL. JWS requires the fixed-width R||S encoding
/// of RFC 7518 §3.4, not DER — a mismatch here verifies fine in .NET and fails
/// everywhere else, which is the worst possible failure mode for an archive.
/// </summary>
public sealed class Es256Adapter : IContentSigner, IContentVerifier
{
    public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportECPrivateKey(key.Private.Span, out _);
        return ecdsa.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key.Public.Span, out _);
        return ecdsa.VerifyData(input, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
