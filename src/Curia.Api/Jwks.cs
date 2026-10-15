using System.Text.Json.Nodes;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain.Primitives;

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
    /// Renders one agent's keys the log binds (R4.35) as an RFC 7517 <c>{"keys": [...]}</c> document,
    /// omitting any it cannot publish (<see cref="CanPublish"/>), and naming for each the leaf that
    /// binds it (<paramref name="logIndexOf"/>, R6.54) where it is known.
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
    /// <param name="keys">The keys, each with the binding the log holds for it.</param>
    /// <param name="logIndexOf">The leaf index of an event, by its id; <see langword="null"/> when the log could not say.</param>
    /// <param name="lifecycle">
    /// The identity's lifecycle indices, ascending (R4.35 rev.); <see langword="null"/> when the log would not
    /// fold, when <c>curia_lifecycle</c> is left out rather than published empty.
    /// </param>
    /// <param name="lifecycleKinds">
    /// The guard's state (R4.38 (add.)): <c>{"approved":[...],"enabled":[...]}</c>, the kinds the build approves
    /// and the Forum enables, both empty until the lift.
    /// </param>
    public static JsonObject ForAgent(
        IReadOnlyList<BoundKey> keys,
        Func<string, long?> logIndexOf,
        IReadOnlyList<long>? lifecycle,
        JsonObject lifecycleKinds)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(logIndexOf);
        ArgumentNullException.ThrowIfNull(lifecycleKinds);

        var array = new JsonArray();
        foreach (var bound in keys)
        {
            var registered = bound.Key;

            // Two refusals, one outcome: the rule the key's verifier owns, and the renderer the log's
            // key-binding entries use too (R4.34). PublicJwkTests holds them to the same answer on
            // every material KeyMaterials names, so the second omits nothing the first admits there.
            if (!CanPublish(registered.Key.Alg, registered.Key.Public.Span)) continue;
            if (!PublicJwk.Of(registered.Key).TryGetValue(out var jwk, out _)) continue;

            var node = Annotate(ActaEndpoints.ToObject(jwk!), registered);
            if (logIndexOf(bound.Binding.EventId) is { } index) node["curia_log_index"] = index;

            // R4.35 (rev.): what the log says of the key now, and where it says so.
            node["curia_status"] = StatusName(bound.Status);
            if (bound.Status is KeyStatus.Retired
                && bound.EndedByEventId is { } endedBy
                && logIndexOf(endedBy) is { } retiredAt)
                node["curia_retired_log_index"] = retiredAt;

            // R6.57: where the declaration that names the key sits, and the index it states the compromise from. The key set
            // names it for every kid the declaration lists, a cascaded key as well as the first, so a reader can fetch it
            // for a post under either. The Forum's word; a reader recomputes the declaration from the lifecycle.
            if (bound.Status is KeyStatus.Compromised
                && bound.CompromisedByEventId is { } declaredBy
                && logIndexOf(declaredBy) is { } declaredAt)
            {
                node["curia_compromised_log_index"] = declaredAt;
                if (bound.DisputedFrom is { } disputedFrom) node["curia_disputed_from"] = disputedFrom;
            }

            array.Add(node);
        }

        var document = new JsonObject { ["keys"] = array };
        if (lifecycle is not null)
        {
            var indices = new JsonArray();
            foreach (var index in lifecycle.Order()) indices.Add((JsonNode)index);
            document["curia_lifecycle"] = indices;
        }

        document["curia_lifecycle_kinds"] = lifecycleKinds;
        return document;
    }

    /// <summary>The wire spelling of a key's status (R4.35 rev.).</summary>
    private static string StatusName(KeyStatus status) => status switch
    {
        KeyStatus.Active => "active",
        KeyStatus.Retired => "retired",
        KeyStatus.Compromised => "compromised",
        KeyStatus.Unbound => "unbound",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a key status"),
    };

    /// <summary>
    /// R4.38 (add.): the guard's state as the key set publishes it, so a reader or an operator can see
    /// which kinds the build approves and the Forum enables without asking the Forum to append one.
    /// Both lists are in the kinds' declaration order.
    /// </summary>
    public static JsonObject LifecycleKinds(IEnumerable<LifecycleKind> approved, ILifecycleAppendGate gate)
    {
        ArgumentNullException.ThrowIfNull(approved);
        ArgumentNullException.ThrowIfNull(gate);

        var approvedSet = approved.ToHashSet();
        var approvedNames = new JsonArray();
        var enabledNames = new JsonArray();
        foreach (var kind in Enum.GetValues<LifecycleKind>())
        {
            if (approvedSet.Contains(kind)) approvedNames.Add((JsonNode)KindName(kind));
            if (gate.Enables(kind)) enabledNames.Add((JsonNode)KindName(kind));
        }

        return new JsonObject { ["approved"] = approvedNames, ["enabled"] = enabledNames };
    }

    private static string KindName(LifecycleKind kind) => kind switch
    {
        LifecycleKind.Rotation => "rotation",
        LifecycleKind.Retirement => "retirement",
        LifecycleKind.Compromise => "compromise",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a lifecycle kind"),
    };

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
