using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Curia.AuthN.Tests.Support;

/// <summary>An independent RFC 7638 thumbprint computation, deliberately not calling
/// <c>Curia.AuthN.Dpop.JwkThumbprint</c> (the module under test): if that production code had a
/// bug -- wrong member order, wrong algorithm -- a test that used it to compute both "what the
/// token's cnf.jkt says" and "what the proof verifies to" would have the bug cancel out against
/// itself and never fail.</summary>
internal static class TestThumbprint
{
    public static string ForEd25519(ReadOnlySpan<byte> rawPublicKey) =>
        Compute($$"""{"crv":"Ed25519","kty":"OKP","x":"{{Base64Url.EncodeToString(rawPublicKey)}}"}""");

    public static string ForP256(ReadOnlySpan<byte> x, ReadOnlySpan<byte> y) =>
        Compute($$"""{"crv":"P-256","kty":"EC","x":"{{Base64Url.EncodeToString(x)}}","y":"{{Base64Url.EncodeToString(y)}}"}""");

    private static string Compute(string json) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
}
