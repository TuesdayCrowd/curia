using Curia.Application.Ports;
using Curia.Canon.Canonical;
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

        // The port's payload-admissibility promise (see IEventStore.AppendAsync's remarks),
        // honoured by calling the very canonicalizer the Postgres adapter admits with -- not by
        // a duplicate scan written a second time here. That this adapter holds an in-process
        // object graph and *could* retain a duplicate-membered tree losslessly is not a licence
        // to accept one: a fake more permissive than the real store is exactly the drift errata
        // E10 found, and it fails in the direction that hurts, with code passing against the
        // fake and losing data against Postgres.
        //
        // CanonicalizeWithNfc, not the bare Canonicalize (R11.24, errata E12). The store's own
        // rendering is pure RFC 8785 -- storage is not signing -- but admission is a different
        // question from storage, and a payload whose member names collide only after NFC has a
        // pure canonical form and no Cūria-profile one. Postgres would keep both such members
        // (jsonb compares keys bytewise) and nothing downstream would object, so the row would
        // sit in the system of record until something tried to sign it. This adapter uses the
        // identical function for the identical reason; the profile is a property of the port,
        // and the two adapters agreeing about it by construction is the whole point of R11.21.
        //
        // The canonical bytes are discarded on purpose: this store keeps the tree itself, so it
        // is the verdict it needs, not the rendering -- and the rendering it would get is the
        // NFC one, which is not what this store holds. The check is linear in each object's
        // member count (RFC 8785 sec. 3.2.3's sort leaves equal names adjacent), so that
        // verdict costs one walk of the payload.
        //
        // The whole batch is checked before a single event is appended, and before the lock is
        // even taken -- mirroring the Postgres adapter refusing before it opens a connection.
        // A per-event check inside the append loop below would leave a half-written batch in an
        // append-only log with nothing to roll it back. Doing it here, ahead of the version
        // check inside the lock, is also what makes the port's failure precedence (R11.25) hold
        // in this adapter: an inadmissible payload is refused as such even when the caller's
        // expectedVersion is stale as well.
        //
        // The payload is stored in RFC 8785 member order, not in the caller's (R11.23). Postgres
        // has no choice about this -- jsonb re-sorts object keys by its own rule and its adapter
        // restores canonical order on the way out -- so a fake that handed back the caller's
        // exact tree would be the more faithful of the two, and code written against it could
        // depend on member order and break in production. Ordering here rather than on each read
        // costs one walk per append instead of one per read, and mirrors Postgres storing the
        // canonicalized text rather than the caller's.
        var admitted = new DomainEvent[events.Count];
        for (var i = 0; i < events.Count; i++)
        {
            var domainEvent = events[i];
            if (!CanonicalJson.CanonicalizeWithNfc(domainEvent.Payload).TryGetValue(out _, out var payloadError))
                return Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(payloadError!));

            if (!CanonicalJson.InCanonicalMemberOrder(domainEvent.Payload).TryGetValue(out var ordered, out var orderError))
                return Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(orderError!));

            admitted[i] = domainEvent with { Payload = ordered };
        }

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

            var appended = new List<AppendedEvent>(admitted.Length);
            foreach (var domainEvent in admitted)
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
