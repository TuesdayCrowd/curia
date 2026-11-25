using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Curia.Tests.Shared;

/// <summary>
/// The enrollment proof of R4.46 (errata G79), built in the test projects from the errata's sentence and from nothing in <c>Curia.AuthN</c> or <c>Curia.Client</c>: a third construction
/// beside the validator's and the client's, so that a proof both of those got wrong in the same way is not one a probe accepts. A header of <c>alg</c>, <c>kid</c> and
/// <c>typ</c> <c>curia-enroll-pop+jwt</c>, claims <c>agent_id</c>, <c>aud</c>, <c>iat</c>, <c>jkt</c>, <c>jti</c> and <c>nonce</c>, signed by the callback it is given (the key a case
/// means to sign with, which is a different key from the request's when a probe is about possession). A mutator changes one member of the header or the payload; nothing
/// else is varied, so a refusal cannot come from another cause.
/// </summary>
internal static class EnrollProofKit
{
    internal const string Typ = "curia-enroll-pop+jwt";

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>RFC 7638's thumbprint of the key as R4.34 renders it, from its stored form (R4.28): <c>EdDSA</c> the 32 raw bytes, <c>ES256</c> a SubjectPublicKeyInfo. Computed here, not by the module under test.</summary>
    internal static string Jkt(string alg, ReadOnlySpan<byte> stored)
    {
        string members;
        if (alg == "EdDSA")
        {
            members = $$"""{"crv":"Ed25519","kty":"OKP","x":"{{Base64Url.EncodeToString(stored)}}"}""";
        }
        else
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(stored, out _);
            var q = ecdsa.ExportParameters(false).Q;
            members = $$"""{"crv":"P-256","kty":"EC","x":"{{Base64Url.EncodeToString(q.X!)}}","y":"{{Base64Url.EncodeToString(q.Y!)}}"}""";
        }

        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(members)));
    }

    /// <summary>Signs as ES256 does (RFC 7518 section 3.4: the fixed-width R||S).</summary>
    internal static Func<byte[], byte[]> SignWith(ECDsa key) =>
        data => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    internal static string NewJti() => "epp-" + Guid.NewGuid().ToString("N");

    /// <summary>The proof of R4.46 for the request named, signed by <paramref name="sign"/>. <paramref name="header"/> and <paramref name="payload"/> may change, add or remove a member.</summary>
    internal static string Build(
        string alg, string agentId, string kid, ReadOnlySpan<byte> storedPublic, string audience, DateTimeOffset iat, string nonce, string jti,
        Func<byte[], byte[]> sign, Action<JsonObject>? header = null, Action<JsonObject>? payload = null)
    {
        ArgumentNullException.ThrowIfNull(sign);
        var h = new JsonObject { ["alg"] = alg, ["kid"] = kid, ["typ"] = Typ };
        header?.Invoke(h);
        var p = new JsonObject
        {
            ["agent_id"] = agentId,
            ["aud"] = audience,
            ["iat"] = iat.ToUnixTimeSeconds(),
            ["jkt"] = Jkt(alg, storedPublic),
            ["jti"] = jti,
            ["nonce"] = nonce,
        };
        payload?.Invoke(p);
        return Compose(h.ToJsonString(Compact), p.ToJsonString(Compact), sign);
    }

    /// <summary>The compact form of the two documents as written, signed by <paramref name="sign"/> over the encoded segments.</summary>
    internal static string Compose(string headerJson, string payloadJson, Func<byte[], byte[]> sign)
    {
        var input = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(headerJson)) + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payloadJson));
        return input + "." + Base64Url.EncodeToString(sign(Encoding.ASCII.GetBytes(input)));
    }
}
