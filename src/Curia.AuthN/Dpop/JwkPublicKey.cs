using System.Security.Cryptography;
using Curia.Canon.Jws;

namespace Curia.AuthN.Dpop;

/// <summary>
/// Converts a parsed <see cref="Jwk"/> into the <see cref="PublicKeyMaterial"/> shape
/// <see cref="Curia.Canon.Jws.IContentVerifier"/> expects, matching exactly what
/// <c>Curia.Canon.Sodium</c>'s adapters read on the other side (confirmed against
/// <c>Curia.Canon.Sodium.Tests.AdapterTests</c>, the only place in the solution that already
/// constructs a real <c>PublicKeyMaterial</c> for each algorithm):
/// <list type="bullet">
/// <item>EdDSA: <c>Curia.Canon.Sodium.Ed25519Adapter</c> imports <c>Public</c> as NSec's raw
/// 32-byte <c>KeyBlobFormat.RawPublicKey</c> -- a JWK OKP key's decoded <c>x</c> is already
/// exactly that, no conversion needed.</item>
/// <item>ES256: <c>Curia.Canon.Sodium.Es256Adapter</c> imports <c>Public</c> via
/// <c>ECDsa.ImportSubjectPublicKeyInfo</c> -- DER SPKI, not the raw (x, y) coordinate pair RFC
/// 7518 §6.2.1's <c>EC</c> JWK form uses, so this method builds an <see cref="ECParameters"/>
/// point and re-exports it through the BCL's own <c>ECDsa</c> to get that DER encoding. This is
/// <see cref="System.Security.Cryptography"/> (BCL), not a native crypto library reference --
/// <c>Curia.Canon</c> itself uses the same namespace for <c>SHA256.HashData</c> in
/// <c>Digests.cs</c> despite CS-6 forbidding it any package reference at all, which is why doing
/// the same thing here does not put Curia.AuthN in CS-6/CS-7's "only Curia.Canon.Sodium links
/// native crypto" bucket.</item>
/// </list>
/// </summary>
public static class JwkPublicKey
{
    /// <param name="jwk">The parsed DPoP proof key.</param>
    /// <param name="kid">DPoP proofs carry no separate <c>kid</c> header member (the key <i>is</i>
    /// the embedded <c>jwk</c>); callers pass an empty string or a caller-chosen label purely for
    /// <see cref="PublicKeyMaterial"/>'s constructor, never as anything resolved or trusted.</param>
    public static PublicKeyMaterial ToPublicKeyMaterial(this Jwk jwk, string kid)
    {
        ArgumentNullException.ThrowIfNull(jwk);

        return jwk.Match(
            okpEd25519: okp => new PublicKeyMaterial("EdDSA", kid, okp.X),
            ecP256: ec => new PublicKeyMaterial("ES256", kid, BuildSubjectPublicKeyInfo(ec.X.Span, ec.Y.Span)));
    }

    private static byte[] BuildSubjectPublicKeyInfo(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y)
    {
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = x.ToArray(), Y = y.ToArray() },
        };

        using var ecdsa = ECDsa.Create(parameters);
        return ecdsa.ExportSubjectPublicKeyInfo();
    }
}
