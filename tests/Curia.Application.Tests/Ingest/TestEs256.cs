using System.Security.Cryptography;
using Curia.Canon.Jws;

namespace Curia.Application.Tests.Ingest;

/// <summary>
/// A real ES256 signer/verifier over the BCL's <see cref="ECDsa"/>, local to this test project.
///
/// <para>Deliberately not <c>Curia.Canon.Sodium</c>'s adapter: these tests are about the
/// <i>pipeline</i>, and giving <c>Curia.Application.Tests</c> a project reference to the crypto
/// adapter would drag NSec's native dependency into a suite that has no other use for it. ES256 is
/// pure BCL, so the signatures here are genuine -- the point of the end-to-end test is that a real
/// signature verifies over the real canonical bytes, and a stub signer would prove nothing.</para>
///
/// <para>The allow-list in <c>DetachedJws</c> is the alg-keyed dictionary it is constructed with,
/// so registering ES256 here is exactly the seam R11.2 describes: the domain decides what must be
/// true, the adapter performs the operation.</para>
/// </summary>
internal sealed class TestEs256 : IContentSigner, IContentVerifier
{
    internal const string Alg = "ES256";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    internal ReadOnlyMemory<byte> PublicKey => _key.ExportSubjectPublicKeyInfo();

    internal ReadOnlyMemory<byte> PrivateKey => _key.ExportPkcs8PrivateKey();

    public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key)
    {
        using var signer = ECDsa.Create();
        signer.ImportPkcs8PrivateKey(key.Private.Span, out _);

        // JWS uses the fixed-width r||s form, not DER.
        return signer.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
    {
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(key.Public.Span, out _);

        return verifier.VerifyData(
            input, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
