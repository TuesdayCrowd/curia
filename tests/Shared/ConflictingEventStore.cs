using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Tests.Shared;

/// <summary>
/// An event store that conflicts on demand (R8.79, errata G46; PR 2 Task 0). While armed on a stream, every <b>unconditioned</b> append to it first gives the
/// test's <c>rival</c> a turn, and then forwards the caller's own append to the inner store. A rival that appends something moves the stream, so the inner
/// store raises its own <see cref="DomainErrors.ConcurrencyConflictType"/>: the conflict is never fabricated here, because a decorator that returned its own
/// result would make the use case's tests restate the use case. A rival that returns <c>false</c> appends nothing and the caller's append lands.
///
/// <para>It counts the caller's appends and reads on the armed stream, can fail every read of it with an error of the test's choosing (the "another
/// failure" case), and stops a runaway loop: past <see cref="MaxAppends"/> appends it throws, so a use case that retries without a bound is a red and not a
/// hang.</para>
/// </summary>
internal sealed class ConflictingEventStore(IEventStore inner) : IEventStore
{
    /// <summary>The most appends one armed stream is allowed to see before the decorator throws.</summary>
    internal const int MaxAppends = 64;

    private readonly object _gate = new();
    private AggregateId? _scope;
    private Func<int, CancellationToken, Task<bool>>? _rival;
    private Error? _readFailure;
    private int _appends;
    private int _reads;

    /// <summary>The caller's appends tried on the armed stream since <see cref="Arm"/>, lost or not.</summary>
    internal int AppendsTried
    {
        get { lock (_gate) return _appends; }
    }

    /// <summary>The caller's reads of the armed stream since <see cref="Arm"/>.</summary>
    internal int ReadsTried
    {
        get { lock (_gate) return _reads; }
    }

    /// <summary>
    /// Watches <paramref name="scope"/>. <paramref name="rival"/> is given the 1-based number of the caller's append about to be made and answers whether
    /// it appended to the stream (true: the caller's append will now lose).
    /// </summary>
    internal void Arm(AggregateId scope, Func<int, CancellationToken, Task<bool>> rival)
    {
        ArgumentNullException.ThrowIfNull(rival);
        lock (_gate)
        {
            _scope = scope;
            _rival = rival;
            _readFailure = null;
            _appends = 0;
            _reads = 0;
        }
    }

    /// <summary>Fails every read of <paramref name="scope"/> with <paramref name="failure"/>; appends are not given a rival.</summary>
    internal void FailReads(AggregateId scope, Error failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_gate)
        {
            _scope = scope;
            _rival = null;
            _readFailure = failure;
            _appends = 0;
            _reads = 0;
        }
    }

    /// <summary>Stops watching; the counters keep their values for the assertions that follow.</summary>
    internal void Disarm()
    {
        lock (_gate)
        {
            _scope = null;
            _rival = null;
            _readFailure = null;
        }
    }

    public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken cancellationToken = default)
    {
        Func<int, CancellationToken, Task<bool>>? rival = null;
        var number = 0;
        lock (_gate)
        {
            if (_scope is { } scope && scope == aggregateId)
            {
                number = ++_appends;
                rival = _rival;
            }
        }

        if (number > MaxAppends)
            throw new InvalidOperationException($"the use case appended to {aggregateId.Value} {number} times: an unbounded retry");

        cancellationToken.ThrowIfCancellationRequested();
        if (rival is not null)
            await rival(number, cancellationToken).ConfigureAwait(false);

        return await inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken).ConfigureAwait(false);
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        StreamCondition condition,
        CancellationToken cancellationToken = default) =>
        inner.AppendAsync(aggregateId, expectedVersion, events, condition, cancellationToken);

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(
        AggregateId aggregateId,
        CancellationToken cancellationToken = default)
    {
        Error? failure = null;
        lock (_gate)
        {
            if (_scope is { } scope && scope == aggregateId)
            {
                _reads++;
                failure = _readFailure;
            }
        }

        return failure is not null
            ? Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(failure))
            : inner.ReadByAggregateAsync(aggregateId, cancellationToken);
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(
        EventSequence afterSeq,
        int? maxCount = null,
        CancellationToken cancellationToken = default) =>
        inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
}
