using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>
/// The R11.4 in-memory adapter for <see cref="IEventStore"/>: a real, if non-durable,
/// implementation with its own tests below, not a mock -- so the domain and application layers
/// are testable with no database (per CS-16's "the fake is a first-class implementation" and the
/// scoping doc's placement of port fakes in the corresponding <c>*.Tests</c> project).
///
/// Stage 2's Postgres adapter is checked against the same <see cref="EventStorePortContractTests"/>
/// suite this type's own test class subclasses -- see that file for how a future
/// <c>Curia.Infrastructure.Tests</c> project reuses it rather than duplicating it.
///
/// A single lock guards the whole store. That serializes every operation, which is the wrong
/// trade for a production event store and the right one for a test double whose job is
/// correctness under concurrent test runs, not throughput.
/// </summary>
internal sealed class InMemoryEventStore : IEventStore
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly List<AppendedEvent> _log = [];
    private readonly Dictionary<string, List<AppendedEvent>> _byAggregate = [];

    public InMemoryEventStore(TimeProvider clock) => _clock = clock;

    public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        if (events.Count == 0)
            return Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(DomainErrors.EmptyAppendBatch()));

        lock (_gate)
        {
            _byAggregate.TryGetValue(aggregateId.Value, out var stream);
            var actualVersion = new AggregateVersion(stream?.Count ?? 0);

            if (actualVersion.Value != expectedVersion.Value)
            {
                return Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(
                    DomainErrors.ConcurrencyConflict(aggregateId, expectedVersion, actualVersion)));
            }

            // R11.3/CS-9: one clock read for the whole batch, mirroring the single transaction
            // timestamp a batched Postgres INSERT would see for every row it writes.
            var serverTimestamp = ServerTimestamp.At(_clock.GetUtcNow());

            var appended = new List<AppendedEvent>(events.Count);
            foreach (var domainEvent in events)
            {
                var seq = new EventSequence(_log.Count + 1L);
                var record = new AppendedEvent(seq, aggregateId, serverTimestamp, domainEvent);
                _log.Add(record);
                appended.Add(record);
            }

            var updatedStream = stream is null ? new List<AppendedEvent>(appended.Count) : [.. stream];
            updatedStream.AddRange(appended);
            _byAggregate[aggregateId.Value] = updatedStream;

            return Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Ok((IReadOnlyList<AppendedEvent>)appended));
        }
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(
        AggregateId aggregateId,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<AppendedEvent> result = _byAggregate.TryGetValue(aggregateId.Value, out var stream)
                ? [.. stream]
                : [];
            return Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Ok(result));
        }
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(
        EventSequence afterSeq,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IEnumerable<AppendedEvent> query = _log.Where(e => e.Seq.Value > afterSeq.Value);
            if (maxCount is { } limit)
                query = query.Take(limit);

            IReadOnlyList<AppendedEvent> result = [.. query];
            return Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Ok(result));
        }
    }
}
