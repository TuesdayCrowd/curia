using System.Text.Json.Nodes;
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;

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
/// wrong -- <c>curia-testis</c>'s own JWK module records that exact trap. Both forms are rendered by
/// <see cref="PublicJwk.Of"/>, which the log's key-binding entries use too (R4.34, errata G16), so
/// the key a reader is served and the key the log bound are one computation.</para>
/// </summary>
public static class Jwks
{
    /// <summary>
    /// Renders one agent's registered keys as an RFC 7517 <c>{"keys": [...]}</c> document, omitting
    /// any stored key it cannot publish (<see cref="CanPublish"/>).
    ///
    /// <para><b>Omitted, not failed.</b> Key rows written before the enrollment route checked a key's
    /// bytes stay in the store for good (R4.19 forbids the delete, R4.32 the repair). Rendering one
    /// threw, so the agent's key set answered 500 forever. Publishing one in a form it is not would
    /// be worse: <c>curia-testis</c> refuses a whole key set over one bad entry, so once an identity
    /// holds two keys (R4.17), one malformed entry would take the good key down with it. A malformed
    /// key in a JWKS is worse than an absent one: absent fails to resolve, malformed fails to verify,
    /// and the second looks like a signature problem. An agent whose only key is omitted gets an
    /// empty set, not a 404, which still means that the store holds no row.</para>
    /// </summary>
    public static JsonObject ForAgent(IReadOnlyList<RegisteredKey> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var array = new JsonArray();
        foreach (var registered in keys)
        {
            // Two refusals, one outcome: the rule the key's verifier owns, and the renderer the log's
            // key-binding entries use too (R4.34). PublicJwkTests holds them to the same answer on
            // every material KeyMaterials names, so the second omits nothing the first admits there.
            if (!CanPublish(registered.Key.Alg, registered.Key.Public.Span)) continue;
            if (!PublicJwk.Of(registered.Key).TryGetValue(out var jwk, out _)) continue;

            array.Add(Annotate(ActaEndpoints.ToObject(jwk!), registered));
        }

        return new JsonObject { ["keys"] = array };
    }

    /// <summary>
    /// R4.15 and R4.28: whether <paramref name="material"/> is a key of <paramref name="alg"/> in the
    /// form this key set publishes. The single switch from an algorithm to the rule its verifier owns
    /// (<see cref="Ed25519Adapter.IsPublicKey"/>, <see cref="Es256Adapter.IsPublicKey"/>), so a key is
    /// registered, published and verified under one predicate. An algorithm with no published shape
    /// here is not publishable.
    /// </summary>
    public static bool CanPublish(string alg, ReadOnlySpan<byte> material) => alg switch
    {
        "EdDSA" => Ed25519Adapter.IsPublicKey(material),
        "ES256" => Es256Adapter.IsPublicKey(material),
        _ => false,
    };

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
}
