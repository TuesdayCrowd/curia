using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Curia.Api.Adapters;

namespace Curia.Api;

/// <summary>
/// The JWKS the Forum serves.
///
/// <para><b>R4.16 rev. (errata A16) is the reason this exists at all:</b> "the Registrar's key
/// store is authoritative and the Forum serves JWKS; no runtime fetch of agent-hosted JWKS." The
/// original design had the Forum fetching an agent's own JWKS at verification time, which was
/// removed as an SSRF and availability surface. Serving instead of fetching means the Forum is the
/// one place a verifier asks, and there is no outbound request anywhere on the ingest path.</para>
///
/// <para><b>Shapes matter more than usual here</b>, because a second implementation has to consume
/// them. RFC 8037 §2 gives Ed25519 an octet-key-pair form (<c>kty: "OKP"</c>, <c>crv:
/// "Ed25519"</c>, single coordinate <c>x</c>); RFC 7518 §6.2.1 gives ES256 the two-coordinate
/// <c>EC</c> form. Reusing <c>EC</c> for an Ed25519 key produces JSON that looks plausible and is
/// wrong -- <c>curia-testis</c>'s own JWK module records that exact trap -- so each family is
/// built from its own code path below rather than from a shared one with a switch in it.</para>
/// </summary>
public static class Jwks
{
    /// <summary>Renders one agent's registered keys as an RFC 7517 <c>{"keys": [...]}</c> document.</summary>
    public static JsonObject ForAgent(IReadOnlyCollection<RegisteredKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var array = new JsonArray();
        foreach (var registered in keys)
        {
            var jwk = registered.Key.Alg switch
            {
                "EdDSA" => OkpEd25519(registered),
                "ES256" => EcP256(registered),

                // An algorithm with no published JWK shape here is omitted rather than guessed at.
                // A malformed key in a JWKS is worse than an absent one: absent fails to resolve,
                // malformed fails to verify, and the second looks like a signature problem.
                _ => null,
            };

            if (jwk is not null) array.Add(jwk);
        }

        return new JsonObject { ["keys"] = array };
    }

    /// <summary>RFC 8037 §2: <c>kty: "OKP"</c>, <c>crv: "Ed25519"</c>, <c>x</c> = the raw 32-byte key.</summary>
    private static JsonObject OkpEd25519(RegisteredKey registered) => Annotate(
        new JsonObject
        {
            ["kty"] = "OKP",
            ["crv"] = "Ed25519",
            ["alg"] = "EdDSA",
            ["kid"] = registered.Key.Kid,
            ["x"] = Base64UrlEncode(registered.Key.Public.Span),
        },
        registered);

    /// <summary>
    /// RFC 7518 §6.2.1: <c>kty: "EC"</c>, <c>crv: "P-256"</c>, and the two coordinates.
    ///
    /// <para>The stored form is SubjectPublicKeyInfo, so the coordinates are recovered by importing
    /// it rather than by slicing the DER by offset. Offset arithmetic over DER works until an
    /// encoder emits a legal variation, and then it silently produces a wrong key.</para>
    /// </summary>
    private static JsonObject EcP256(RegisteredKey registered)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(registered.Key.Public.Span, out _);
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);

        return Annotate(
            new JsonObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["alg"] = "ES256",
                ["kid"] = registered.Key.Kid,
                ["x"] = Base64UrlEncode(parameters.Q.X!),
                ["y"] = Base64UrlEncode(parameters.Q.Y!),
            },
            registered);
    }

    /// <summary>
    /// Adds the validity window as non-standard members.
    ///
    /// <para>RFC 7517 defines no validity fields, so these are extensions -- and they are prefixed
    /// so nobody mistakes them for standard ones. They are here because R6.31 makes validity a
    /// function of a post's <c>server_ts</c>, and a consumer that cannot see the window can only
    /// ever ask "is this key valid now", which is the wrong question for any post older than the
    /// last key rotation.</para>
    /// </summary>
    private static JsonObject Annotate(JsonObject jwk, RegisteredKey registered)
    {
        jwk["curia_not_before"] = registered.NotBefore.ToString("O");
        if (registered.NotAfter is { } notAfter) jwk["curia_not_after"] = notAfter.ToString("O");
        return jwk;
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);
}
