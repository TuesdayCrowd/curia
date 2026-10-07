using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>An event store that counts reads; a read counted before R10.68's cap is the cost its order exists to avoid.</summary>
internal sealed class CountingEventStore(IEventStore inner) : IEventStore
{
    private int _reads;

    /// <summary>How many times either read method was called.</summary>
    public int Reads => Volatile.Read(ref _reads);

    public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken cancellationToken = default) =>
        inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken);

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(
        AggregateId aggregateId,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        return inner.ReadByAggregateAsync(aggregateId, cancellationToken);
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(
        EventSequence afterSeq,
        int? maxCount = null,
        CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        return inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
    }
}

/// <summary>A flag-detail store that counts reads; a read counted before R10.68's cap is the cost its order exists to avoid.</summary>
internal sealed class CountingFlagDetailStore(IFlagDetailStore inner) : IFlagDetailStore
{
    private int _reads;

    /// <summary>How many times <see cref="ReadAllAsync"/> was called.</summary>
    public int Reads => Volatile.Read(ref _reads);

    public Task<Result<FlagDetail>> AppendAsync(FlagDetail detail, CancellationToken cancellationToken = default) =>
        inner.AppendAsync(detail, cancellationToken);

    public Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        return inner.ReadAllAsync(cancellationToken);
    }
}
