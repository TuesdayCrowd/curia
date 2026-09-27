using System.Security.Cryptography;
using Curia.Canon.Jws;

namespace Curia.Application.Tests;

/// <summary>
/// A real P-256 key under a given <c>kid</c>, stored as R4.28 stores an <c>ES256</c> key: its DER
/// SubjectPublicKeyInfo. <c>EnrollAgent</c> records an enrollment's key as its public JWK (R4.34,
/// errata G16), so a test that records one needs material the renderer calls a key, and a fresh one
/// each call, so two calls under one <c>kid</c> are two different keys.
/// </summary>
internal static class TestKeys
{
    internal static PublicKeyMaterial Es256(string kid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo());
    }
}
