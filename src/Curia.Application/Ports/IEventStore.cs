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
    /// (<see cref="DomainErrors.EmptyAppendBatch"/>); any event's payload carries an object with
    /// two members of the same name, at any depth (<c>curia/admit/duplicate-key</c> -- see
    /// "Payload admissibility" below); or <paramref name="expectedVersion"/> does
    /// not match the aggregate's actual current version
    /// (<see cref="DomainErrors.ConcurrencyConflict"/>) -- optimistic concurrency as a
    /// <see cref="Result{T}"/> value, never an exception, never a silent overwrite.
    ///
    /// <para><b>Payload admissibility (R6.42, errata E10).</b> Every event's payload SHALL be
    /// canonicalizable. An object carrying two members with the same name has no RFC 8785
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
    /// <c>Curia.Canon.Canonical.CanonicalJson.Canonicalize</c> and propagating its failure --
    /// never by scanning for duplicates itself. One rule with two implementations is how the
    /// rule drifts, which is what E10 caught happening. The check is linear in each object's
    /// member count (RFC 8785 sec. 3.2.3's sort leaves equal names adjacent), so honouring this
    /// costs one walk of the tree even for an adapter that needs no canonical bytes of its own,
    /// and R6.39's member cap governs ADMIT alone -- nothing bounds the width of an object in a
    /// payload a caller built in memory rather than parsed.</para>
    /// </summary>
    Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken cancellationToken = default);
}
