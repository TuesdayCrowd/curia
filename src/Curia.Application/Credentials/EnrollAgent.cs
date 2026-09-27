using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>
/// What the log says about an agent after an enrollment request has been recorded.
/// </summary>
/// <param name="EnrolledAt">
/// The instant the credential became active -- the <i>first</i> enrollment's <c>server_ts</c>, not
/// this request's. Table 11 counts "≥ 48 hours" from enrollment, singular.
/// </param>
/// <param name="OwnerVerified">
/// Owner verification as the log holds it after this request. Reported, never set, here: enrollment
/// cannot change it (R4.30), and a fresh enrollment's answer is <see langword="false"/>. Carried so
/// the receipt can say so, and an agent learns at enrollment rather than at its first refused
/// answer that an operator has to act.
/// </param>
/// <param name="WasAlreadyEnrolled">
/// Whether the log already carried an enrollment. Reported rather than hidden so a caller can say
/// something truthful about what its request did; the HTTP surface treats both outcomes as success,
/// because re-announcing an enrollment is what a client legitimately does when it re-authenticates.
/// </param>
public sealed record AgentEnrollment(
    DateTimeOffset EnrolledAt,
    bool OwnerVerified,
    bool WasAlreadyEnrolled);

/// <summary>
/// CS-16's <c>Enroll</c> use case: records an agent's enrollment as an append-only event (R4.21).
///
/// <para><b>The tenure clock is protected by the store, not by a check.</b> A repeat enrollment
/// must not restart Table 11's "≥ 48 hours" -- the day an agent first became active is a fact about
/// its history, not a field the latest request sets. The guard is
/// <see cref="AggregateVersion.New"/> on the first append: an aggregate that already holds events
/// refuses it as an optimistic-concurrency conflict, so "enroll once" is enforced by the same
/// mechanism that makes concurrent appends safe rather than by a read-then-write this code would
/// have to get right under a race. Table 6 agrees independently: <c>(active,
/// SuccessfulEnrollment)</c> is not a cell, so a second enrollment event would make
/// <see cref="CredentialLifecycle.Project"/> fail outright rather than quietly re-date the
/// credential.</para>
///
/// <para><b>Owner verification is not this use case's to record.</b> It was: the request body
/// carried a boolean and this appended it, which made Table 11's one Sybil cost a value the
/// enrolling party supplied (errata G5). R4.30 moves it to <see cref="AttestOwner"/>, under an
/// operator's or the owner's actor. A repeat enrollment therefore has nothing to append at all: it
/// reads the standing back and reports it.</para>
///
/// <para><b>Why this holds <see cref="IEventStore"/> and not <see cref="IEventReader"/>.</b> It
/// has to append, and CS-16 names <c>Enroll</c> as a use case in its own right. CS-15's concern --
/// that nothing writes content between VERIFY and PERSIST -- is about submitted content: there is
/// no envelope here, no canonical bytes, and nothing to screen, so the phase types that make an
/// unverified post impossible to persist have nothing to say about an enrollment. The rule as this
/// solution actually encodes it (<c>Curia.Architecture.Tests.EventStoreWriteSurfaceTests</c>:
/// who may construct an <see cref="AppendedEvent"/>) is untouched -- this type appends through the
/// port and fabricates nothing.</para>
/// </summary>
public sealed class EnrollAgent
{
    /// <summary>
    /// How many times to re-read and retry after losing an optimistic-concurrency race. One retry,
    /// because a conflict here means another request enrolled the same agent concurrently and the
    /// re-read then finds the enrollment already present -- the loop exists to observe that, not to
    /// contend for a resource. A second conflict on the retry would mean something is appending to
    /// this agent's stream continuously, which is a caller to fix rather than a wait to lengthen.
    ///
    /// <para>That was incomplete. Before R4.33 (errata G15) the usual cause of a second conflict was
    /// an identifier naming an aggregate that already held events and no enrollment -- a post's, as
    /// the review enrolled one -- where the append at <see cref="AggregateVersion.New"/> can never
    /// succeed however often it is retried. <see cref="EnrollIdentity"/> now refuses such an
    /// identifier upstream, before the key store is asked, so a request reaching this loop names a
    /// stream that holds nothing or holds its own enrollment.</para>
    /// </summary>
    private const int Attempts = 2;

    private readonly IEventStore _events;
    private readonly UlidGenerator _ids;

    public EnrollAgent(IEventStore events, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>
    /// Records an enrollment -- <c>agent.enrolled</c> and <c>agent.key-bound</c>, in one append
    /// (R4.34) -- or, when the log already binds <paramref name="key"/> to the identity, reports the
    /// standing the log holds and appends nothing. When the log binds no key under that <c>kid</c>,
    /// refuses (<see cref="AuthorKeyErrors.AlreadyEnrolled"/>); when it binds another key under it,
    /// refuses (<see cref="AuthorKeyErrors.MaterialImmutable"/>); and appends nothing (R4.31 rev.). This
    /// is the log's half of enrollment; <see cref="EnrollIdentity"/> is the use case that puts the key
    /// store's half in front of it.
    /// </summary>
    /// <param name="agentId">The enrolling agent; also the aggregate its credential events land in.</param>
    /// <param name="key">The key this enrollment registered: its <c>kid</c> on <c>agent.enrolled</c>, its public JWK on <c>agent.key-bound</c>.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    public async Task<Result<AgentEnrollment>> RecordAsync(
        string agentId,
        PublicKeyMaterial key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);

        // One aggregate per agent, for the reason IngestPipeline gives for one aggregate per post:
        // a single shared "agents" stream would make every concurrent enrollment an
        // optimistic-concurrency conflict against every other, and agents are not a consistency
        // boundary with each other. Here the per-agent stream does a second job the post streams do
        // not need -- it is what lets AggregateVersion.New mean "this agent has never enrolled".
        if (!AggregateId.Create(agentId).TryGetValue(out var aggregate, out var aggregateError))
            return Result<AgentEnrollment>.Fail(aggregateError!);

        if (!ActorId.Create(agentId).TryGetValue(out var actor, out var actorError))
            return Result<AgentEnrollment>.Fail(actorError!);

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var read = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var history, out var readError))
                return Result<AgentEnrollment>.Fail(readError!);

            // This aggregate's slice carries credential events only -- posts live in their own
            // streams -- so the standing folded here has no question count and no reached-T1
            // instant, and neither is read below. What is read is what this stream is the whole
            // record of: whether an enrollment exists, when it was, and what the owner's standing is.
            var standing = AgentStandingProjector.Fold(history!)
                .GetValueOrDefault(agentId);

            if (standing?.EnrolledAt is { } enrolledAt)
            {
                // R4.31 rev. (errata G14, G16): a re-announcement is honoured only for a key the log
                // binds to this identity. Reporting "already enrolled" for any other kid is how a
                // second key under an enrolled identity used to be waved through as a success; and
                // for other bytes under a bound kid, how a lost row's recovery registered whatever it
                // was sent.
                if (EnrollmentBinding.Find(history!, agentId)?.For(key.Kid) is not { } bound)
                    return Result<AgentEnrollment>.Fail(AuthorKeyErrors.AlreadyEnrolled(agentId));

                if (!bound.Holds(key))
                    return Result<AgentEnrollment>.Fail(AuthorKeyErrors.MaterialImmutable(key.Kid));

                return Result<AgentEnrollment>.Ok(
                    new AgentEnrollment(enrolledAt, standing.OwnerVerified, WasAlreadyEnrolled: true));
            }

            var attempted = await AppendEnrollmentAsync(aggregate, actor, agentId, key, cancellationToken)
                .ConfigureAwait(false);

            if (attempted.TryGetValue(out var enrollment, out var attemptError))
                return Result<AgentEnrollment>.Ok(enrollment!);

            if (!IsConcurrencyConflict(attemptError!))
                return Result<AgentEnrollment>.Fail(attemptError!);
        }

        return Result<AgentEnrollment>.Fail(EnrollmentErrors.ContendedAggregate(agentId, Attempts));
    }

    private async Task<Result<AgentEnrollment>> AppendEnrollmentAsync(
        AggregateId aggregate,
        ActorId actor,
        string agentId,
        PublicKeyMaterial key,
        CancellationToken cancellationToken)
    {
        // R4.34: the key as the key set publishes it. EnrollIdentity refuses a key that renders to none
        // before the key store is asked, so through the use case this refuses nothing; a caller that
        // records alone, skipping the route's check and the use case's, is refused here, and nothing
        // is appended.
        if (!PublicJwk.Of(key).TryGetValue(out var jwk, out var jwkError))
            return Result<AgentEnrollment>.Fail(jwkError!);

        if (!NewEvent(AgentStandingProjector.EnrolledType, actor, new JsonValue.Object(
            [
                new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
                new(AgentStandingProjector.KeyIdField, new JsonValue.String(key.Kid)),

                // R4.21's "reason", carried on the event rather than supplied by whatever reads it
                // back. The trigger is Table 6's SuccessfulEnrollment and is implied by the event type;
                // this is the free-text elaboration TransitionReason exists for.
                new(AgentStandingProjector.ReasonField, new JsonValue.String(EnrollmentReason)),
            ])).TryGetValue(out var enrolled, out var enrolledError))
            return Result<AgentEnrollment>.Fail(enrolledError!);

        // R4.34: the key itself, bound to the identity in the log. Appended with the enrollment, in
        // the same call and so the same transaction, so no enrollment is ever recorded whose key the
        // log does not carry: a crash between two appends would leave an identity bound by kid alone.
        if (!NewEvent(AgentStandingProjector.KeyBoundType, actor, new JsonValue.Object(
            [
                new(AgentStandingProjector.AgentIdField, new JsonValue.String(agentId)),
                new(AgentStandingProjector.KeyIdField, new JsonValue.String(key.Kid)),
                new(AgentStandingProjector.JwkField, jwk!),
            ])).TryGetValue(out var bound, out var boundError))
            return Result<AgentEnrollment>.Fail(boundError!);

        // No server_ts in either payload. The store stamps the events, one instant for the append, and
        // R6.5 makes that the Forum's observation; a second instant in a payload would be a claim that
        // could disagree with it.
        var appended = await _events
            .AppendAsync(aggregate, AggregateVersion.New, [enrolled!, bound!], cancellationToken)
            .ConfigureAwait(false);

        return appended.Map(events => new AgentEnrollment(
            events[0].ServerTimestamp.Value, OwnerVerified: false, WasAlreadyEnrolled: false));
    }

    /// <summary>One event of <paramref name="type"/>, under a fresh ULID.</summary>
    private Result<DomainEvent> NewEvent(string type, ActorId actor, JsonValue.Object payload)
    {
        if (!_ids.Next().TryGetValue(out var ulid, out var idError))
            return Result<DomainEvent>.Fail(idError!);

        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError))
            return Result<DomainEvent>.Fail(eventIdError!);

        if (!EventType.Create(type).TryGetValue(out var eventType, out var typeError))
            return Result<DomainEvent>.Fail(typeError!);

        return Result<DomainEvent>.Ok(new DomainEvent(eventId, eventType, actor, payload));
    }

    /// <summary>
    /// Matched on the slug the domain publishes rather than on a locally written string, so a
    /// rename of the condition is a compile error here instead of a retry loop that silently stops
    /// retrying.
    /// </summary>
    private static bool IsConcurrencyConflict(Error error) =>
        string.Equals(error.Type, DomainErrors.ConcurrencyConflictType, StringComparison.Ordinal);

    private const string EnrollmentReason = "Enrollment accepted: agent key registered with the Registrar";
}

/// <summary>RFC 9457 problem-type slugs the enrollment use case and the enrollment route emit.</summary>
public static class EnrollmentErrors
{
    /// <summary>The slug of <see cref="IdentifierReserved"/>, matched by the route's 409 mapping.</summary>
    public const string IdentifierReservedType = "curia/enroll/identifier-reserved";

    /// <summary>The slug of <see cref="UnsupportedAlgorithm"/>.</summary>
    public const string UnsupportedAlgorithmType = "curia/enroll/unsupported-algorithm";

    /// <summary>The slug of <see cref="NulCharacter"/>.</summary>
    public const string NulCharacterType = "curia/enroll/nul-character";

    /// <summary>The slug of <see cref="PublicKeyMissing"/>, <see cref="PublicKeyNotBase64"/> and <see cref="PublicKeyNotOfItsAlgorithm"/>.</summary>
    public const string InvalidKeyType = "curia/enroll/invalid-key";

    /// <summary>The slug of <see cref="IdentifierTooLong"/>.</summary>
    public const string IdentifierTooLongType = "curia/enroll/identifier-too-long";

    /// <summary>The slug of <see cref="KeysAmbiguous"/>, matched by the route's 409 mapping.</summary>
    public const string KeysAmbiguousType = "curia/enroll/keys-ambiguous";

    /// <summary>The slug of <see cref="IdentifierNotNfc"/>.</summary>
    public const string IdentifierNotNfcType = "curia/enroll/identifier-not-nfc";

    /// <summary>
    /// The most UTF-8 bytes an <c>agent_id</c> or a <c>kid</c> may hold. An implementation limit, not
    /// R4.5's form (plan D4 stays open): it sits well under the 2,704-byte index row Postgres stores
    /// for <c>agent_keys</c>' primary key, its per-agent index, and <c>events (aggregate_id, seq)</c>,
    /// past which an enrollment answered 500; and it keeps <c>/v1/jwks?agent=</c> for any identifier
    /// under a request line's usual limit once percent-encoded, so every agent's keys stay fetchable.
    /// </summary>
    public const int MaxIdentifierBytes = 1024;

    /// <summary>One title for every reason a <c>public_key</c> is refused; the detail says which, and never echoes the key.</summary>
    private const string InvalidKeyTitle = "That public key cannot be registered";

    /// <summary>
    /// R4.33 (errata G15): the identifier begins with a prefix the Forum's own writers mint aggregates
    /// under (<see cref="ReservedIdentifiers"/>), or names an aggregate holding events and no enrollment
    /// of it. One slug and one detail for both clauses, because the remedy is the same: nothing was
    /// written, and the agent needs an identifier of its own.
    /// </summary>
    public static Error IdentifierReserved(string agentId) => new(
        IdentifierReservedType,
        "That identifier names records the Forum keeps for something other than an agent",
        $"agent={agentId}: nothing was registered. The event log keeps this identifier for its own records; an agent needs an identifier of its own.");

    /// <summary>
    /// R4.31 rev. (errata G16): the event log records no enrollment of the identifier, and the key
    /// store holds more than one key for it, so nothing says which is its own and no enrollment
    /// request can choose. Names the identifier; never a key, nor how many the store holds.
    /// </summary>
    public static Error KeysAmbiguous(string agentId) => new(
        KeysAmbiguousType,
        "The key store holds several keys for that agent, and the event log binds none of them",
        $"agent={agentId}: nothing was registered or recorded. The event log records no enrollment of this identifier, so nothing says which of the keys the store holds for it is its own, and an enrollment request cannot choose one (R4.31). A new identity needs an agent identifier of its own.");

    /// <summary>
    /// R4.15: the enrollment's algorithm is missing, or is not one the Forum verifies signatures with.
    /// The list is <paramref name="verified"/>, sorted ordinally: the composition root's allow-list,
    /// which <c>DetachedJws</c> uses too, so the refusal names what the Forum actually accepts.
    /// </summary>
    public static Error UnsupportedAlgorithm(string? alg, IEnumerable<string> verified)
    {
        ArgumentNullException.ThrowIfNull(verified);

        return new Error(
            UnsupportedAlgorithmType,
            "That key algorithm is not one the Forum verifies",
            $"alg={(string.IsNullOrEmpty(alg) ? "(none)" : alg)}: an agent key is {string.Join(" or ", verified.Order(StringComparer.Ordinal))} (R4.15)");
    }

    /// <summary>
    /// The enrollment's <paramref name="field"/> holds U+0000. JSON carries it as an escape, and ADMIT
    /// accepts it, but Postgres <c>text</c> cannot store it. The value is never echoed.
    /// </summary>
    public static Error NulCharacter(string field) => new(
        NulCharacterType,
        "That identifier holds U+0000, which the Forum cannot store",
        $"field={field}");

    /// <summary>
    /// R4.36 (errata G16): the enrollment's <paramref name="field"/> is not in Unicode Normalization
    /// Form C. A signed envelope names its author in NFC (R6.9), so an identifier NFC would change
    /// could never author a post, and every signature naming it names another identifier. Names the
    /// field; the value is never echoed.
    /// </summary>
    public static Error IdentifierNotNfc(string field) => new(
        IdentifierNotNfcType,
        "That identifier is not in Unicode Normalization Form C",
        $"field={field}: nothing was registered. A signed envelope names its author in NFC (R6.9), so an identifier NFC would change could never author a post (R4.36).");

    /// <summary>The enrollment carries no <c>public_key</c>, or JSON null for it.</summary>
    public static Error PublicKeyMissing() => new(InvalidKeyType, InvalidKeyTitle, "public_key is missing");

    /// <summary>The enrollment's <c>public_key</c> is not base64.</summary>
    public static Error PublicKeyNotBase64() => new(InvalidKeyType, InvalidKeyTitle, "public_key is not base64");

    /// <summary>
    /// R4.15 against R4.28's stored forms: the decoded <c>public_key</c> is not a key of
    /// <paramref name="alg"/>. The detail names the form the algorithm takes, so an agent can see what
    /// to send.
    /// </summary>
    public static Error PublicKeyNotOfItsAlgorithm(string alg) => new(
        InvalidKeyType,
        InvalidKeyTitle,
        alg switch
        {
            "ES256" => "alg=ES256: public_key is not an ES256 key, which is the base64 of a P-256 key's DER SubjectPublicKeyInfo with nothing after it (R4.15, R4.28)",
            "EdDSA" => "alg=EdDSA: public_key is not an EdDSA key, which is the base64 of the raw 32-byte Ed25519 public key (R4.15, R4.28)",
            _ => $"alg={alg}: public_key is not a key the Forum can publish for that algorithm (R4.28)",
        });

    /// <summary>The enrollment's <paramref name="field"/> holds <paramref name="bytes"/> UTF-8 bytes, over <see cref="MaxIdentifierBytes"/>.</summary>
    public static Error IdentifierTooLong(string field, int bytes) => new(
        IdentifierTooLongType,
        "That identifier is longer than the Forum stores",
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"field={field} bytes={bytes}: at most {MaxIdentifierBytes} UTF-8 bytes"));

    /// <summary>
    /// The enrollment lost its optimistic-concurrency race on every attempt. Distinct from
    /// <see cref="DomainErrors.ConcurrencyConflict"/>, which the caller never sees here: a single
    /// conflict is expected and retried, and only exhausting the retries is a condition worth
    /// reporting.
    /// </summary>
    public static Error ContendedAggregate(string agentId, int attempts) => new(
        "curia/enroll/contended",
        "The agent's credential stream is being appended to concurrently; the enrollment was not recorded",
        $"agent={agentId} attempts={attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
}
