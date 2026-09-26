using System.Formats.Asn1;
using System.Security.Cryptography;

namespace Curia.Tests.Shared;

/// <summary>
/// Public-key material that is not a key of the algorithm it arrives under, named once, for R4.15 and
/// R4.28. The verifier, the enrollment route and the served key set are each asked about the same
/// rows, so the three suites that ask cannot drift into testing different inputs.
///
/// <para>Each material comes with a signature over the message it is asked about. Where a verifier
/// without R4.15's rule would accept the material -- a P-384 key under <c>ES256</c>, a P-256 key with
/// a byte after it -- the signature is a genuine one, so a verifier that answers <c>false</c> is
/// refusing the key and not a bad signature.</para>
/// </summary>
internal static class KeyMaterials
{
    /// <summary>The named-curve OID of brainpoolP256r1, a 256-bit curve that is not P-256.</summary>
    private const string BrainpoolP256r1 = "1.3.36.3.3.2.8.1.1.7";

    /// <summary><c>id-ecPublicKey</c>.</summary>
    private const string EcPublicKey = "1.2.840.10045.2.1";

    /// <summary>
    /// The material called <paramref name="name"/>, and a signature over <paramref name="message"/>.
    /// Throws for a name it does not know, so a misspelt row fails rather than testing nothing.
    /// </summary>
    internal static (byte[] Material, byte[] Signature) Build(string name, byte[] message)
    {
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var p256Signature = p256.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        switch (name)
        {
            case "empty":
                return ([], p256Signature);
            case "three-zero-bytes":
                return (new byte[3], p256Signature);
            case "31-bytes":
                return (RandomNumberGenerator.GetBytes(31), new byte[64]);
            case "32-raw-bytes":
                return (RandomNumberGenerator.GetBytes(32), p256Signature);
            case "33-bytes":
                return (RandomNumberGenerator.GetBytes(33), new byte[64]);
            case "rsa-2048-spki":
            {
                using var rsa = RSA.Create(2048);
                return (rsa.ExportSubjectPublicKeyInfo(), p256Signature);
            }
            case "p384-spki":
            {
                // ECDSA with SHA-256 over P-384: what a verifier that accepts any imported curve
                // verifies, and calls ES256.
                using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
                return (p384.ExportSubjectPublicKeyInfo(),
                    p384.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
            }
            case "p256-spki":
                return (p256.ExportSubjectPublicKeyInfo(), p256Signature);
            case "p256-spki-and-a-trailing-byte":
                return ([.. p256.ExportSubjectPublicKeyInfo(), 0x00], p256Signature);
            case "brainpoolP256r1-spki":
                return (BrainpoolSpki(p256), p256Signature);
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "no such material");
        }
    }

    /// <summary>
    /// A SubjectPublicKeyInfo naming brainpoolP256r1 around an honest P-256 point: 32-byte
    /// coordinates, as P-256's are, so only the curve's OID tells the two apart.
    /// </summary>
    private static byte[] BrainpoolSpki(ECDsa p256)
    {
        var point = p256.ExportParameters(includePrivateParameters: false).Q;
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier(EcPublicKey);
                writer.WriteObjectIdentifier(BrainpoolP256r1);
            }

            writer.WriteBitString([0x04, .. point.X!, .. point.Y!]);
        }

        return writer.Encode();
    }
}
