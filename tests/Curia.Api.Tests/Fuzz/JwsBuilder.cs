using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// ES256 JWS over bytes the fuzzer wrote, so a varied header or claims set is signed exactly as it
/// will be sent: <c>b64u(header) + "." + b64u(claims) + "." + b64u(ES256(ascii(b64u(header) + "." +
/// b64u(claims))))</c>, IEEE P1363. The post signature is RFC 7797's detached form, its signing input
/// built as <c>DetachedJws.Sign</c> builds it, with the header bytes the fuzzer's.
/// </summary>
internal static class JwsBuilder
{
    internal static string B64(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    internal static string Compact(byte[] header, byte[] claims, ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var input = B64(header) + "." + B64(claims);
        return input + "." + B64(Sign(key, Encoding.ASCII.GetBytes(input)));
    }

    /// <summary>The compact form with a signature segment taken from another JWS: an unre-signed copy.</summary>
    internal static string WithSignatureOf(byte[] header, byte[] claims, string original) =>
        B64(header) + "." + B64(claims) + "." + original[(original.LastIndexOf('.') + 1)..];

    /// <summary>RFC 7797 with <c>b64:false</c>: <c>b64u(header) + ".." + b64u(signature)</c> over <c>ascii(b64u(header) + ".") + canonical</c>.</summary>
    internal static string Detached(byte[] header, ReadOnlySpan<byte> canonical, ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var prefix = Encoding.ASCII.GetBytes(B64(header) + ".");
        var input = new byte[prefix.Length + canonical.Length];
        prefix.CopyTo(input, 0);
        canonical.CopyTo(input.AsSpan(prefix.Length));
        return B64(header) + ".." + B64(Sign(key, input));
    }

    /// <summary>The detached form with a signature segment taken from another: an unre-signed copy.</summary>
    internal static string DetachedWithSignatureOf(byte[] header, string original) =>
        B64(header) + ".." + original[(original.LastIndexOf('.') + 1)..];

    private static byte[] Sign(ECDsa key, byte[] input) =>
        key.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    /// <summary>The public key as an RFC 7517 JWK, as a DPoP proof's header carries it.</summary>
    internal static JsonObject Jwk(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var p = key.ExportParameters(includePrivateParameters: false);
        return new JsonObject
        {
            ["kty"] = "EC",
            ["crv"] = "P-256",
            ["x"] = B64(p.Q.X!),
            ["y"] = B64(p.Q.Y!),
        };
    }

    /// <summary>RFC 7638's thumbprint of an EC key: the required members in lexicographic order, hashed.</summary>
    internal static string Thumbprint(ECDsa key)
    {
        var jwk = Jwk(key);
        var json = $"{{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":\"{jwk["x"]}\",\"y\":\"{jwk["y"]}\"}}";
        return B64(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>RFC 9449's <c>ath</c>: the access token's SHA-256.</summary>
    internal static string Ath(string token) => B64(SHA256.HashData(Encoding.ASCII.GetBytes(token)));

    /// <summary>A compact JWS's segment decoded to a JSON object, for a template.</summary>
    internal static JsonObject Segment(string compact, int index) =>
        JsonNode.Parse(Base64Url.DecodeFromChars(compact.Split('.')[index]))!.AsObject();
}
