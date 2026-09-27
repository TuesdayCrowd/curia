using System.Security.Cryptography;
using Curia.Canon.Jws;

namespace Curia.Canon.Sodium;

/// <summary>
/// ECDSA P-256 with SHA-256 via the BCL. JWS requires the fixed-width R||S encoding
/// of RFC 7518 §3.4, not DER — a mismatch here verifies fine in .NET and fails
/// everywhere else, which is the worst possible failure mode for an archive.
///
/// <para><b>This adapter owns what an <c>ES256</c> key is</b> (R4.15, R4.28): the DER
/// SubjectPublicKeyInfo of a key on the curve named P-256, with nothing after it.
/// <see cref="IsPublicKey"/> is that rule, and <see cref="Verify"/>, the served key set and the
/// enrollment route all ask it, so "registered", "published" and "verified under" cannot disagree.
/// They did: this verifier imported any curve the platform could read, and verified a P-384 key's
/// SHA-256 signatures as <c>ES256</c>, while the key set published that key as <c>crv: "P-256"</c>
/// with 48-byte coordinates, a shape the independent verifier refuses.</para>
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

    /// <summary>
    /// Verifies under <paramref name="key"/> only when it is an <c>ES256</c> key
    /// (<see cref="IsPublicKey"/>), and answers <see langword="false"/> for anything else, never a
    /// throw (CS-10): material written before the enrollment route checked it stays in the store for
    /// good, and every token request naming it must be answered, not failed.
    /// </summary>
    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(key);

        using var ecdsa = ECDsa.Create();
        return Read(key.Public.Span, ecdsa)
            && ecdsa.VerifyData(input, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// R4.15 and R4.28: whether <paramref name="material"/> is an <c>ES256</c> public key, the DER
    /// SubjectPublicKeyInfo of a key on the curve named P-256, with nothing after it.
    /// </summary>
    public static bool IsPublicKey(ReadOnlySpan<byte> material)
    {
        using var ecdsa = ECDsa.Create();
        return Read(material, ecdsa);
    }

    /// <summary>
    /// Imports <paramref name="material"/> into <paramref name="ecdsa"/>, and answers whether it is an
    /// <c>ES256</c> key. The curve is judged by its OID, never by coordinate length or key size:
    /// brainpoolP256r1 and secp256k1 have 32-byte coordinates too. A platform that cannot read the
    /// material throws <see cref="CryptographicException"/>, and one that does not support its curve
    /// (brainpool, on macOS) throws <see cref="PlatformNotSupportedException"/>; both are "not a key".
    /// </summary>
    private static bool Read(ReadOnlySpan<byte> material, ECDsa ecdsa)
    {
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(material, out var read);
            return read == material.Length
                && ecdsa.ExportParameters(includePrivateParameters: false).Curve is { IsNamed: true } curve
                && curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }
}
