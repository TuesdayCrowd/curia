using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Curia.Canon.Json;

namespace Curia.Client;

/// <summary>
/// R4.46 (errata G79): the proof of possession an enrollment carries, a compact JWS signed with the registered key through <see cref="IAgentSigner"/> (the key in this process or the external
/// signer's <c>sign</c>; no verb of its own). Header <c>alg</c>, <c>kid</c>, <c>typ</c> <c>curia-enroll-pop+jwt</c>; claims <c>agent_id</c>, <c>aud</c> (the absolute URL of the enrollment route as
/// this client dials it), <c>iat</c>, <c>jkt</c> (RFC 7638 over the key as R4.34 renders it), <c>jti</c> and <c>nonce</c>. Each call makes a new <c>jti</c> and asks the signer once.
/// </summary>
internal static class EnrollmentProof
{
    internal const string Typ = "curia-enroll-pop+jwt";

    internal static string Build(EnrolledAgent agent, Uri audience, string nonce, DateTimeOffset now)
    {
        var profile = agent.Profile;
        var header = new List<KeyValuePair<string, JsonValue>>
        {
            new("alg", profile.Alg.AsJson()),
            new("kid", profile.Kid.AsJson()),
            new("typ", Typ.AsJson()),
        };

        var payload = new List<KeyValuePair<string, JsonValue>>
        {
            new("agent_id", profile.AgentId.AsJson()),
            new("aud", audience.ToString().AsJson()),
            new("iat", new JsonValue.Number(now.ToUnixTimeSeconds())),
            new("jkt", Thumbprint(profile.Alg, agent.Signer.PublicKey.Span).AsJson()),
            new("jti", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)).AsJson()),
            new("nonce", nonce.AsJson()),
        };

        var input = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(ClientJson.Render(header))) + "." +
                    Base64Url.EncodeToString(Encoding.UTF8.GetBytes(ClientJson.Render(payload)));
        return input + "." + Base64Url.EncodeToString(agent.Signer.Sign(Encoding.ASCII.GetBytes(input)));
    }

    /// <summary>RFC 7638's thumbprint of the key as R4.34 stores it: <c>EdDSA</c> the 32 raw bytes, <c>ES256</c> a SubjectPublicKeyInfo.</summary>
    private static string Thumbprint(string alg, ReadOnlySpan<byte> stored)
    {
        string members;
        if (alg == "EdDSA")
        {
            members = $$"""{"crv":"Ed25519","kty":"OKP","x":"{{Base64Url.EncodeToString(stored)}}"}""";
        }
        else
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(stored, out _);
            var q = key.ExportParameters(includePrivateParameters: false).Q;
            members = $$"""{"crv":"P-256","kty":"EC","x":"{{Base64Url.EncodeToString(q.X!)}}","y":"{{Base64Url.EncodeToString(q.Y!)}}"}""";
        }

        return Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(members)));
    }
}
