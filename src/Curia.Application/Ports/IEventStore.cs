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
    /// stream) and a single <see cref="DateTimeOffset"/> read once from the store's clock port
    /// for the whole call -- mirroring how a batched Postgres insert inside one transaction would
    /// see one <c>now()</c> for every row.
    ///
    /// Fails, leaving the store unchanged, when: the batch is empty
    /// (<see cref="DomainErrors.EmptyAppendBatch"/>); or <paramref name="expectedVersion"/> does
    /// not match the aggregate's actual current version
    /// (<see cref="DomainErrors.ConcurrencyConflict"/>) -- optimistic concurrency as a
    /// <see cref="Result{T}"/> value, never an exception, never a silent overwrite.
    /// </summary>
    Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken cancellationToken = default);
}
