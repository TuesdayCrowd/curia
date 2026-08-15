using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

/// <summary>
/// The read half of the event store port (Figure 12's "EventStore"), split out from
/// <see cref="IEventStore"/> for CS-15: a component whose dependency is typed as
/// <see cref="IEventReader"/> has no member that reaches the store's write surface, so it
/// cannot append even by accident -- the compiler enforces that, not a code-review convention.
/// Projections, replay consumers, and any query-only use case should depend on this interface,
/// never on <see cref="IEventStore"/>.
///
/// <para><b>What a payload reads back as (R11.23, errata E12).</b> Every event this interface
/// hands back -- from either read method, and from <see cref="IEventStore.AppendAsync"/>'s own
/// return value -- SHALL carry its payload with every object's members in RFC 8785 §3.2.3
/// order, at every depth, whichever adapter is underneath. Array order and every scalar are
/// the ones that were appended; only member order is promised, and only because member order
/// is the one thing about a JSON document that carries no information (RFC 8259 §4: an object
/// is an unordered collection), so normalizing it is not a mutation of the fact stored.</para>
///
/// <para>The port promises the canonical order rather than the caller's original tree because
/// the caller's original tree is not something a real store can promise. PostgreSQL's
/// <c>jsonb</c> is a parsed binary form, not text: it re-sorts every object's keys by its own
/// rule -- key length first, then bytewise -- so a payload appended as
/// <c>{"z":1,"longer_key_b":2,"a":3}</c> comes back as
/// <c>{"a":3,"z":1,"longer_key_b":2}</c> even though the adapter handed the database RFC 8785
/// order to begin with. The ordering is lost inside the database, downstream of everything the
/// adapter controls. Promising byte-for-byte tree fidelity would therefore mean either changing
/// the column type or declaring the in-memory adapter authoritative over the real one, for a
/// property nothing in this system needs: digests are taken over canonical bytes, which sort.
/// </para>
///
/// <para>What made this worth pinning is the direction the adapters diverged in, which is E11's
/// finding one step over (R11.22). The in-memory adapter returned the caller's exact tree,
/// because an in-process object graph physically can -- so it was the *more faithful* of the
/// two, and code written and tested against it could depend on payload member order and break
/// against Postgres. A fake that is more faithful than the real store misleads exactly as a
/// fake that is more permissive does: in both cases the fake supports a property production
/// does not have, and the test suite reports agreement. An adapter satisfies this promise with
/// <c>Curia.Canon.Canonical.CanonicalJson.InCanonicalMemberOrder</c>, which shares §3.2.3's
/// ordering with the canonicalizer's own writer -- never by sorting members itself.</para>
/// </summary>
public interface IEventReader
{
    /// <summary>Every event recorded for one aggregate, in append (ascending <c>seq</c>) order.
    /// An aggregate nothing has been appended to yields an empty list, not a failure.</summary>
    Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(
        AggregateId aggregateId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Events with <c>Seq</c> strictly greater than <paramref name="afterSeq"/>, in ascending
    /// <c>seq</c> order, across every aggregate -- the replay primitive R11.9 needs. Pass
    /// <see cref="EventSequence.Zero"/> to replay the whole store from the beginning.
    /// <paramref name="maxCount"/> bounds how many are returned; <see langword="null"/> means no
    /// limit, which the in-memory adapter can afford but a database-backed one should treat as an
    /// explicit caller choice rather than a default nobody thought about.
    /// </summary>
    Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(
        EventSequence afterSeq,
        int? maxCount = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The event store port, in full: everything <see cref="IEventReader"/> offers, plus the one
/// write operation. CS-15: no component outside the code that has actually verified and screened
/// what it is about to persist (the ingest pipeline's eventual <c>Persist</c> phase; scoping doc
/// sec. 5.1) should hold a dependency typed as <see cref="IEventStore"/> rather than
/// <see cref="IEventReader"/> -- see the Stage 1 report for exactly what this interface split
/// achieves today and what still needs Stage 4's architecture tests to close.
/// </summary>
public interface IEventStore : IEventReader
{
    /// <summary>
    /// Appends one or more events to a single aggregate's stream. Every event in the batch is
    /// assigned a store-wide, strictly increasing <see cref="EventSequence"/> (Appendix D's
    /// <c>seq</c> is one <c>IDENTITY</c> column shared by every aggregate, not one counter per
    /// stream) and a single <see cref="ServerTimestamp"/> read once from the store's clock port
    /// for the whole call -- mirroring how a batched Postgres insert inside one transaction would
    /// see one <c>now()</c> for every row.
    ///
    /// Fails, leaving the store unchanged, when: the batch is empty
    /// (<see cref="DomainErrors.EmptyAppendBatch"/>); any event's payload has no Cūria-profile
    /// canonical form, at any depth (<c>curia/admit/duplicate-key</c>,
    /// <c>curia/canon/duplicate-normalized-key</c> -- see "Payload admissibility" below); or
    /// <paramref name="expectedVersion"/> does
    /// not match the aggregate's actual current version
    /// (<see cref="DomainErrors.ConcurrencyConflict"/>) -- optimistic concurrency as a
    /// <see cref="Result{T}"/> value, never an exception, never a silent overwrite.
    ///
    /// <para><b>Payload admissibility (R6.42, R11.24, errata E10 and E12).</b> Every event's
    /// payload SHALL be canonicalizable under the Cūria profile
    /// (<c>Curia.Canon.Canonical.CanonicalJson.CanonicalizeWithNfc</c>). An object carrying two
    /// members with the same name has no RFC 8785
    /// canonical form -- sec. 3.2.3 orders members by name, and two equal names have no order --
    /// so such a payload cannot be digested, cannot be signed, and does not re-parse to one
    /// unambiguous document. The event table is the system of record and every read model is
    /// rebuilt from it by replay (R11.9), so a document that has no unambiguous form is not a
    /// fact the system of record can faithfully hold, whichever adapter is underneath. Refusal
    /// is the only outcome the no-mutation invariant (R6.12-R6.17) permits: there is no repair
    /// primitive, and silently storing a collapsed payload is data loss in the one table replay
    /// treats as ground truth. The refusal names the condition rather than the layer that
    /// noticed it (R6.42, R6.40): <c>curia/admit/duplicate-key</c>, the same slug ADMIT reports
    /// for the same defect.</para>
    ///
    /// <para><b>Why the profile is the signing one, when the storage rendering is not.</b> An
    /// adapter renders what it stores with the pure <c>Canonicalize</c>, because R6.9's NFC
    /// mandate governs content that will be signed or verified and NFC-normalizing stored
    /// content would be a mutation sec. 6.4 forbids outright. That reasoning settles what is
    /// *written*; it does not settle what is *admitted*, and the two questions have different
    /// answers. Admission decides only whether to refuse, and refusing mutates nothing --
    /// so nothing about the no-mutation invariant argues for admitting more. What argues
    /// against it is that a payload whose member names collide only after NFC -- precomposed
    /// <c>café</c> against <c>cafe</c> + U+0301 -- has a pure canonical form but no Cūria-profile
    /// one (<c>curia/canon/duplicate-normalized-key</c>), and <c>jsonb</c> retains both members
    /// because it compares keys bytewise, so nothing downstream objects either. Nothing is lost
    /// today. But this is the system of record, and the moment event payloads are digested into
    /// a Merkle leaf or an <i>Acta</i> entry -- sec. 9's dump manifests being the obvious
    /// candidate -- the store already holds rows that cannot be canonicalized for that purpose,
    /// and the discovery arrives at signing time instead of write time. Refusing at the door
    /// costs one extra walk of the tree and is the narrowest possible tightening: a payload
    /// carrying NFD text, or any amount of it, still passes -- only a *collision* is refused.
    /// This is not the store adopting the signing profile as its storage format; it is the
    /// store declining to accept a fact it can already prove it will not be able to sign.</para>
    ///
    /// <para>This is a promise of the port, not a quirk of one adapter. That an in-process
    /// adapter <i>can</i> physically retain such a tree is an artifact of it being an object
    /// graph, not evidence that the port permits it -- and a fake more permissive than the real
    /// store is worse than no fake, because code tested against it passes and then fails in
    /// production, which is the failure R11.4's "every port has an in-memory adapter" exists to
    /// prevent. The promise is written here because a contract stated only in a test class is
    /// discoverable by whoever runs the tests, not by whoever reads the interface: the two
    /// adapters had in fact drifted -- Postgres refusing (its <c>jsonb</c> resolves duplicate
    /// keys last-wins on input, so accepting one lost a member with no error anywhere on the
    /// path) while the in-memory adapter accepted -- with no failing test, until
    /// <c>EventStorePortContractTests</c> grew a case for it.</para>
    ///
    /// <para>An adapter satisfies this by canonicalizing the payload with
    /// <c>Curia.Canon.Canonical.CanonicalJson.CanonicalizeWithNfc</c> and propagating its
    /// failure -- discarding the bytes, which are the signing profile's rendering and not what
    /// this store writes -- never by scanning for duplicates itself. One rule with two
    /// implementations is how the rule drifts, which is what E10 caught happening. The check is
    /// linear in each object's
    /// member count (RFC 8785 sec. 3.2.3's sort leaves equal names adjacent), so honouring this
    /// costs one walk of the tree even for an adapter that needs no canonical bytes of its own,
    /// and R6.39's member cap governs ADMIT alone -- nothing bounds the width of an object in a
    /// payload a caller built in memory rather than parsed.</para>
    ///
    /// <para><b>Failure precedence (R11.25, errata E12).</b> When more than one of the failures
    /// above applies to the same call, the ones decidable from the arguments alone -- an empty
    /// batch, and payload admissibility -- SHALL be reported in preference to
    /// <see cref="DomainErrors.ConcurrencyConflict"/>, which is a claim about the store's state.
    /// The principle is that admissibility is a property of the argument, settled without
    /// reading anything, so it can be settled before anything is read; and the caller is better
    /// served by it. A concurrency conflict invites a re-read and a retry, which is the right
    /// response to a stale version and a permanently wrong one for a payload that will be
    /// refused identically every time. This was already how both adapters behaved -- each
    /// checks payloads before it touches the store, which is also what makes an all-or-nothing
    /// batch refusal possible in an append-only log -- but until it was stated here and given a
    /// case in the shared contract suite it was an accident of two implementations agreeing
    /// rather than a promise, which is exactly the shape E11 found.</para>
    /// </summary>
    Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken cancellationToken = default);
}
