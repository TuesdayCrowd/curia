using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curia.Canon.Jws;

namespace Curia.AuthN.Tests.Support;

/// <summary>Builds a real, correctly-signed RFC 7515 compact JWS from plain header/payload
/// dictionaries -- deliberately independent of <c>Curia.AuthN.Jwt.CompactJws</c> (the module
/// under test) so a bug shared between "build a token" and "parse a token" could not hide a test
/// failure from itself.</summary>
internal static class TestJwt
{
    /// <summary>Signs with the standard JWS signing input (base64url header, ".", base64url
    /// payload) -- the shape a real access token, client assertion, or DPoP proof uses; never
    /// the detached-JWS shape <c>Curia.Canon.Jws.DetachedJws</c> builds for content envelopes.</summary>
    public static string Sign(Dictionary<string, object> header, Dictionary<string, object?> payload, TestKeyPair key)
    {
        var headerB64 = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(header));
        var payloadB64 = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(payload));
        var input = Encoding.ASCII.GetBytes(headerB64 + "." + payloadB64);
        var signature = key.Signer.Sign(input, key.SigningKey);
        return $"{headerB64}.{payloadB64}.{Base64Url.EncodeToString(signature)}";
    }

    /// <summary>Same signing procedure as <see cref="Sign"/>, but corrupts one byte of the
    /// signature segment afterward -- for tests that need a structurally valid, wrong-signature
    /// token/proof rather than one rejected earlier for a header or claim reason.</summary>
    public static string SignWithTamperedSignature(Dictionary<string, object> header, Dictionary<string, object?> payload, TestKeyPair key)
    {
        var valid = Sign(header, payload, key);
        var segments = valid.Split('.');
        var signatureBytes = Base64Url.DecodeFromChars(segments[2]);
        signatureBytes[0] ^= 0xFF;
        return $"{segments[0]}.{segments[1]}.{Base64Url.EncodeToString(signatureBytes)}";
    }

    public static long ToUnixSeconds(DateTimeOffset instant) => instant.ToUnixTimeSeconds();

    /// <summary>RFC 9449 §4.2's <c>ath</c>: <c>base64url(sha256(access_token))</c>. Reimplemented
    /// independently from <c>AccessTokenValidator</c>'s private copy of the same formula -- this
    /// is what a real DPoP-proving client computes to build a proof in the first place, not a
    /// call into the module under test.</summary>
    public static string ComputeAth(string accessToken) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));
}
