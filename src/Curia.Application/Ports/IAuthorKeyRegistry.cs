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
    /// Enrollment's one write (R4.31, R4.32): registers <paramref name="key"/> as
    /// <paramref name="agentId"/>'s key, valid from <paramref name="notBefore"/>, when and only when
    /// the identifier holds no key yet.
    ///
    /// <para><b>Four outcomes, decided by <see cref="KeyEnrollment.Decide"/> and made atomic by the
    /// adapter</b> against a concurrent enrollment of the same identifier:</para>
    /// <list type="bullet">
    /// <item>The identifier holds no key: the key is registered and returned.</item>
    /// <item>The identifier already holds exactly this key -- the same <c>kid</c>, algorithm and
    /// bytes: nothing is written, and the registered key is returned with its original window. A
    /// client re-announcing its enrollment is not an error, and it must not move
    /// <c>NotBefore</c>: R6.31 evaluates validity at each post's <c>server_ts</c>, so a later
    /// <c>NotBefore</c> would declare last week's posts signed by a key that did not yet
    /// exist.</item>
    /// <item>The identifier holds this <c>kid</c> with different material: refused,
    /// <see cref="AuthorKeyErrors.MaterialImmutable"/>. A kid whose bytes could be replaced is a
    /// kid whose past signatures stop verifying and whose future ones someone else makes.</item>
    /// <item>The identifier holds any other key: refused, <see cref="AuthorKeyErrors.AlreadyEnrolled"/>.
    /// An enrolled identity gains a key only through R4.18: by rotation, signed by a key it already
    /// holds, or by recovery on its owner's re-authorization. An enrollment that added one with
    /// neither would be a rotation that proved nothing.</item>
    /// </list>
    ///
    /// <para><b>And, as before, a <c>kid</c> registered to a different agent is refused</b>
    /// (<see cref="AuthorKeyErrors.KidRegisteredToAnotherAgent"/>): a <c>kid</c> identifies one key
    /// across every identifier (the table's primary key). Both resolvers ask by agent and <c>kid</c>
    /// together: ingest for a post's author (R6.2), and the token endpoint for the agent named as the
    /// client (R5.20). The token endpoint once asked by <c>kid</c> alone, on the premise that "the
    /// subject is established by which key verified". A signature shows possession of <i>some</i>
    /// registered key and not whose, so one enrolled key minted every identity's token (errata G15,
    /// D26).</para>
    ///
    /// <para><b>Why the port has no general "register".</b> It had one, and the enrollment endpoint
    /// called it for every request, so any caller could add a key to any identity or replace the
    /// bytes behind one (errata G14). A port offering that write to the application layer is an
    /// invitation to call it; rotation and revocation arrive as writes of their own, each proving
    /// what it must.</para>
    /// </summary>
    Task<Result<RegisteredKey>> EnrollAsync(
        string agentId,
        PublicKeyMaterial key,
        DateTimeOffset notBefore,
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
/// R4.31's decision, written once and applied by both adapters inside their own atomicity -- the
/// shape <c>FlagDetailRules</c> set: the rule lives in the application layer, and an adapter
/// contributes only the guarantee that nothing else touches the identifier while it applies it.
/// </summary>
public static class KeyEnrollment
{
    /// <summary>
    /// What an enrollment of <paramref name="key"/> for <paramref name="agentId"/> does, given every
    /// key the identifier already holds.
    ///
    /// <para><b>Two of enrollment's refusals are not decided here</b>, because
    /// <paramref name="held"/> cannot show them. A <c>kid</c> registered to a different identifier is
    /// the adapter's to refuse (<see cref="AuthorKeyErrors.KidRegisteredToAnotherAgent"/>), at its
    /// write, since only the whole store holds that <c>kid</c>. The event log's binding, which refuses
    /// a <c>kid</c> the identifier's <c>agent.enrolled</c> does not name, is <c>EnrollIdentity</c>'s,
    /// applied before the store is asked (R4.31).</para>
    /// </summary>
    /// <returns>
    /// <c>Ok(null)</c>: register it -- the identifier holds no key. The adapter's write still refuses
    /// it when another identifier holds the <c>kid</c>. <c>Ok(existing)</c>: the identifier already
    /// holds exactly this key, the same <c>kid</c>, algorithm and bytes; write nothing and return it,
    /// window unmoved. A failure: <see cref="AuthorKeyErrors.MaterialImmutable"/> when it holds this
    /// <c>kid</c> under another algorithm or with other bytes, and
    /// <see cref="AuthorKeyErrors.AlreadyEnrolled"/> when it holds any other key; refuse, and write
    /// nothing.
    /// </returns>
    public static Result<RegisteredKey?> Decide(string agentId, PublicKeyMaterial key, IReadOnlyList<RegisteredKey> held)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(held);

        if (held.Count == 0) return Result<RegisteredKey?>.Ok(null);

        foreach (var existing in held)
        {
            if (!string.Equals(existing.Key.Kid, key.Kid, StringComparison.Ordinal)) continue;

            return SameMaterial(existing.Key, key)
                ? Result<RegisteredKey?>.Ok(existing)
                : Result<RegisteredKey?>.Fail(AuthorKeyErrors.MaterialImmutable(key.Kid));
        }

        return Result<RegisteredKey?>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));
    }

    /// <summary>
    /// Same algorithm and same bytes. Compared by content: <see cref="PublicKeyMaterial"/> is a
    /// record over a <see cref="ReadOnlyMemory{T}"/>, and a record's generated equality compares
    /// that member by reference, which would call two identical keys read from two places different.
    /// </summary>
    public static bool SameMaterial(PublicKeyMaterial left, PublicKeyMaterial right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return string.Equals(left.Alg, right.Alg, StringComparison.Ordinal)
            && left.Public.Span.SequenceEqual(right.Public.Span);
    }
}

/// <summary>
/// The key store's refusals, each by name. Four say why a key does not resolve:
/// <see cref="NotRegisteredToAgent"/>, <see cref="NotYetValid"/>, <see cref="NoLongerValid"/> and,
/// since errata G16, <see cref="NotBoundByTheLog"/>. They are distinct because they mean different
/// things to an operator: a <c>kid</c> that is not the agent's is a possible impersonation attempt; a
/// key outside its window is ordinary lifecycle; a key the store holds and the log does not bind is a
/// store that has diverged from the log. Collapsing them would make the first and the last invisible
/// inside the second's noise. Three say why an enrollment registered nothing (R4.31, R4.32):
/// <see cref="KidRegisteredToAnotherAgent"/>, <see cref="AlreadyEnrolled"/> and
/// <see cref="MaterialImmutable"/>.
/// </summary>
public static class AuthorKeyErrors
{
    /// <summary>The slug of <see cref="KidRegisteredToAnotherAgent"/>, for callers that match on it.</summary>
    public const string KidRegisteredToAnotherAgentType = "curia/enroll/kid-already-registered";

    /// <summary>The slug of <see cref="AlreadyEnrolled"/>, for callers that match on it.</summary>
    public const string AlreadyEnrolledType = "curia/enroll/already-enrolled";

    /// <summary>The slug of <see cref="MaterialImmutable"/>, for callers that match on it.</summary>
    public const string MaterialImmutableType = "curia/keys/material-immutable";

    /// <summary>The slug of <see cref="NotBoundByTheLog"/>, for callers that match on it.</summary>
    public const string NotBoundByTheLogType = "curia/keys/not-bound-by-the-log";

    public static Error NotRegisteredToAgent(string agentId, string kid) => new(
        "curia/keys/not-registered-to-agent",
        "No key with that identifier is registered to that agent",
        $"agent={agentId} kid={kid}");

    public static Error NotYetValid(string kid, ServerTimestamp at) => new(
        "curia/keys/not-yet-valid",
        "The key was not yet valid at the receipt instant",
        $"kid={kid} server_ts={at}");

    /// <summary>
    /// R4.35 (errata G16): the store holds a key under this <c>kid</c> for this agent, and the event
    /// log binds no such key to it -- a row added through errata G14's hole, or bytes the store holds
    /// that are not the ones the log bound. Names the agent and the <c>kid</c>, never material.
    /// </summary>
    public static Error NotBoundByTheLog(string agentId, string kid) => new(
        NotBoundByTheLogType,
        "The event log binds no such key to that agent",
        $"agent={agentId} kid={kid}");

    public static Error NoLongerValid(string kid, ServerTimestamp at) => new(
        "curia/keys/no-longer-valid",
        "The key was no longer valid at the receipt instant",
        $"kid={kid} server_ts={at}");

    /// <summary>
    /// The enrollment refusal <see cref="IAuthorKeyRegistry.EnrollAsync"/> describes. Its own
    /// slug rather than a reuse of <see cref="NotRegisteredToAgent"/>: one is "you asked for a
    /// key that is not yours", the other is "you tried to claim an identifier that is someone
    /// else's", and only the second is an enrollment-time event an operator can act on.
    /// </summary>
    public static Error KidRegisteredToAnotherAgent(string agentId, string kid) => new(
        KidRegisteredToAnotherAgentType,
        "That key identifier is already registered to a different agent",
        $"agent={agentId} kid={kid}");

    /// <summary>
    /// R4.31: the identifier is enrolled, and not with this key. The detail says what to do,
    /// because the commonest way to meet it is honest -- two agents that chose the same identifier --
    /// and an agent told only "conflict" retries.
    /// </summary>
    public static Error AlreadyEnrolled(string agentId) => new(
        AlreadyEnrolledType,
        "That agent identifier is already enrolled with a different key",
        $"agent={agentId}: nothing was registered. An enrolled identity gains a key only through " +
        "R4.18, by rotation signed by a key it already holds or by recovery on its owner's " +
        "re-authorization; a new identity needs an agent identifier of its own.");

    /// <summary>
    /// R4.32: this <c>kid</c> is registered with other material, under another algorithm or with
    /// other bytes, and a registered key never changes. Names the kid and never the material, as
    /// every refusal here names identifiers and nothing the request carried beyond them.
    /// </summary>
    public static Error MaterialImmutable(string kid) => new(
        MaterialImmutableType,
        "That key identifier is already registered with different key material",
        $"kid={kid}: nothing was registered. The key registered under a kid never changes (R4.32).");
}
