using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Api.Tests;

/// <summary>
/// R6.56 over HTTP: the production event store with a stream that moves under one author's conditioned
/// append. While armed, a conditioned append on the scoped stream first lets <c>interfere</c> append an
/// unrelated entry to it, then forwards the original append to the inner store, so the real store
/// raises <see cref="DomainErrors.ConditionConflictType"/>. The conflict is never fabricated here: a
/// decorator that returned its own result would make the route's tests restate the route.
///
/// <para>Only the armed aggregate's conditioned appends are counted or consumed, so enrollment and
/// every other request on the shared host leave the counters alone.</para>
/// </summary>
internal sealed class ContendedEventStore(IEventStore inner) : IEventStore
{
    private readonly object _gate = new();
    private AggregateId? _scope;
    private Func<CancellationToken, Task>? _interfere;
    private int _remaining;
    private int _count;
    private int _injected;

    /// <summary>How many injections are still to come.</summary>
    internal int ConflictsRemaining
    {
        get { lock (_gate) return _remaining; }
    }

    /// <summary>Conditioned appends seen on the armed stream since <see cref="Arm"/>.</summary>
    internal int ConditionedAppendCount
    {
        get { lock (_gate) return _count; }
    }

    /// <summary>Conflicts actually provoked since <see cref="Arm"/>.</summary>
    internal int ConflictsInjected
    {
        get { lock (_gate) return _injected; }
    }

    /// <summary>Watches <paramref name="scope"/> and provokes <paramref name="conflicts"/> conflicts on it.</summary>
    internal void Arm(AggregateId scope, int conflicts, Func<CancellationToken, Task> interfere)
    {
        ArgumentNullException.ThrowIfNull(interfere);
        lock (_gate)
        {
            _scope = scope;
            _interfere = interfere;
            _remaining = conflicts;
            _count = 0;
            _injected = 0;
        }
    }

    /// <summary>Stops watching; the counters keep their values for the assertions that follow.</summary>
    internal void Disarm()
    {
        lock (_gate)
        {
            _scope = null;
            _interfere = null;
            _remaining = 0;
        }
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken cancellationToken = default) =>
        inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken);

    public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        StreamCondition condition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(condition);

        Func<CancellationToken, Task>? interfere = null;
        lock (_gate)
        {
            if (_scope is { } scope && condition.Aggregate == scope)
            {
                _count++;
                if (_remaining > 0)
                {
                    _remaining--;
                    _injected++;
                    interfere = _interfere;
                }
            }
        }

        if (interfere is not null)
            await interfere(cancellationToken).ConfigureAwait(false);

        return await inner.AppendAsync(aggregateId, expectedVersion, events, condition, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(
        AggregateId aggregateId,
        CancellationToken cancellationToken = default) =>
        inner.ReadByAggregateAsync(aggregateId, cancellationToken);

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(
        EventSequence afterSeq,
        int? maxCount = null,
        CancellationToken cancellationToken = default) =>
        inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
}
