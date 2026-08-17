using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

/// <summary>
/// A key registered to an agent, with the window it is valid over.
/// </summary>
/// <param name="Key">The exact material a verifier consumes -- raw 32 bytes for Ed25519, DER
/// SubjectPublicKeyInfo for ES256. Not a JWK: JWK is the shape the Forum <i>serves</i> (R4.28,
/// rendered by <c>Curia.Api.Jwks</c>), and converting on the way in and out again would put a
/// parse on the ingest path that R6.12-R6.17 has no reason to tolerate.</param>
/// <param name="NotBefore">Registration. A signature made earlier does not verify.</param>
/// <param name="NotAfter">
/// Revocation or expiry, exclusive. Null means still valid. R6.31 evaluates against
/// <c>server_ts</c>, so a key revoked today still verifies a post the Forum received last week --
/// which is the whole point of evaluating at the receipt instant rather than at "now".
/// </param>
public sealed record RegisteredKey(
    PublicKeyMaterial Key,
    DateTimeOffset NotBefore,
    DateTimeOffset? NotAfter);

/// <summary>
/// The write and enumerate half of the Registrar's key store, whose read half is
/// <see cref="IAuthorKeyResolver"/>.
///
/// <para><b>Split from the resolver rather than merged into it</b> for the reason CS-15 splits
/// <c>IEventStore</c> from <c>IEventReader</c>: the ingest pipeline needs to resolve a key and
/// has no business registering one, and a component typed to the narrower interface cannot reach
/// the wider one even by accident. The composition root resolves both from a single adapter over
/// a single table -- the same arrangement, and the same reasoning, as the event store's two
/// registrations.</para>
///
/// <para><b>R4.16 rev. (errata A16) constrains every implementation</b>, exactly as it does
/// <see cref="IAuthorKeyResolver"/>: the Registrar's key store is authoritative and the Forum
/// serves JWKS. Nothing behind this port fetches key material from a URL at request time. The
/// shape helps -- there is no parameter here an adapter could mistake for a location -- but the
/// obligation is stated because a port cannot make an adapter refuse to open a socket it was
/// never asked to open.</para>
/// </summary>
public interface IAuthorKeyRegistry
{
    /// <summary>
    /// Registers <paramref name="key"/> to <paramref name="agentId"/>, valid from
    /// <paramref name="notBefore"/> until <paramref name="notAfter"/> (null: still valid).
    ///
    /// <para><b>Fails when the <c>kid</c> is already registered to a <i>different</i> agent.</b>
    /// This store is asked for keys two ways: by (agent, kid) on the ingest path, and by
    /// <c>kid</c> alone by <c>Curia.AuthN.Ports.IAgentKeyResolver</c> -- correctly, because a
    /// client assertion names its key and the subject is established by <i>which key verified</i>,
    /// not by a claim. That second question only has an answer if a <c>kid</c> identifies one
    /// key. Two agents sharing one makes assertion resolution ambiguous, and an ambiguity
    /// resolved by iteration order is the kind of defect that authenticates the wrong agent
    /// intermittently. So the collision is refused at enrollment, where it is a clear error with
    /// a name, rather than left to surface later as an authentication that succeeded for the
    /// wrong subject.</para>
    ///
    /// <para>Re-registering the same (agent, <c>kid</c>) is permitted -- a repeat enrollment, not
    /// a collision -- and SHALL NOT move <paramref name="notBefore"/> later than the instant
    /// already recorded. Moving it forward would retroactively invalidate every signature the key
    /// made in between, because R6.31 evaluates validity at each post's <c>server_ts</c>; the day
    /// a key first became valid is a fact about the archive, not a field the latest enrollment
    /// gets to overwrite.</para>
    /// </summary>
    Task<Result<RegisteredKey>> RegisterAsync(
        string agentId,
        PublicKeyMaterial key,
        DateTimeOffset notBefore,
        DateTimeOffset? notAfter = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The keys registered to one agent, whatever their validity window.
    ///
    /// <para>Expired and revoked keys are included deliberately. R6.31 evaluates validity at a
    /// post's <c>server_ts</c>, so a key retired last week is still the right key for a post
    /// received last month -- and a JWKS that served only currently-valid keys would make every
    /// older post unverifiable by anyone but the Forum, which is the archive quietly losing the
    /// property Phase 1 exists to establish. R4.19 says the same thing from the other side:
    /// revoked <c>kid</c>s are retained indefinitely with their interval. The window travels with
    /// each key so a consumer can apply R6.31 itself rather than having the answer pre-baked for
    /// "now".</para>
    /// </summary>
    Task<IReadOnlyList<RegisteredKey>> KeysForAsync(string agentId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Three distinct reasons a key does not resolve. Distinct because they mean different things to
/// an operator: a <c>kid</c> that is not the agent's is a possible impersonation attempt; a key
/// outside its window is ordinary lifecycle. Collapsing them would make the first invisible
/// inside the second's noise.
/// </summary>
public static class AuthorKeyErrors
{
    public static Error NotRegisteredToAgent(string agentId, string kid) => new(
        "curia/keys/not-registered-to-agent",
        "No key with that identifier is registered to that agent",
        $"agent={agentId} kid={kid}");

    public static Error NotYetValid(string kid, ServerTimestamp at) => new(
        "curia/keys/not-yet-valid",
        "The key was not yet valid at the receipt instant",
        $"kid={kid} server_ts={at}");

    public static Error NoLongerValid(string kid, ServerTimestamp at) => new(
        "curia/keys/no-longer-valid",
        "The key was no longer valid at the receipt instant",
        $"kid={kid} server_ts={at}");

    /// <summary>
    /// The enrollment refusal <see cref="IAuthorKeyRegistry.RegisterAsync"/> describes. Its own
    /// slug rather than a reuse of <see cref="NotRegisteredToAgent"/>: one is "you asked for a
    /// key that is not yours", the other is "you tried to claim an identifier that is someone
    /// else's", and only the second is an enrollment-time event an operator can act on.
    /// </summary>
    public static Error KidRegisteredToAnotherAgent(string agentId, string kid) => new(
        "curia/enroll/kid-already-registered",
        "That key identifier is already registered to a different agent",
        $"agent={agentId} kid={kid}");
}
