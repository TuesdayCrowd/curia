using Curia.Application.Ports;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Npgsql;

namespace Curia.Tests.Shared;

/// <summary>
/// How a <see cref="FaultableEventStore"/> fails, once armed (the D95 PR's seam, errata G52). The first four are the reader's own: the way the real adapter
/// fails is by throwing, and the way a decorated reader failed before was by reporting, and a probe over a stand-in must be able to fail the way the real one
/// fails (trap 83).
/// </summary>
internal enum EventStoreFault
{
    /// <summary>Nothing fails.</summary>
    None,

    /// <summary>Every read throws <see cref="LogUnreadableException"/> around a driver fault, as the fixed event store does.</summary>
    ThrowUnreadable,

    /// <summary>Every read throws what the test supplied (a driver fault by default): the unwrapped escape the real adapter makes today.</summary>
    ThrowReads,

    /// <summary>The whole-log read returns a failed <see cref="Result{T}"/>; a read of one aggregate is healthy. This is the swallow's.</summary>
    ReportForward,

    /// <summary>Every read returns a failed <see cref="Result{T}"/>; the append is healthy. This is the validator's own mapping.</summary>
    ReportAll,

    /// <summary>The append returns a failed <see cref="Result{T}"/>; reads are healthy.</summary>
    ReportAppend,

    /// <summary>The append throws a driver fault and commits nothing.</summary>
    ThrowOnAppend,

    /// <summary>The append commits and then throws a driver fault: the act is stored and the caller is told nothing.</summary>
    AppendThenThrow,

    /// <summary>Reads are healthy until the first append after arming commits, and fail from then on: the receipt must not read a store after the commit.</summary>
    FailReadsAfterFirstAppend,

    /// <summary>Every read waits until the caller's token is cancelled, then throws the cancellation: a client that aborts the request mid-read (ULG39).</summary>
    HangUntilCancelled,
}

/// <summary>The faults a probe throws.</summary>
internal static class StoreFaultKinds
{
    /// <summary>The exception a starved pool throws: an <see cref="NpgsqlException"/> around a <see cref="TimeoutException"/> (Task 0's M4).</summary>
    internal static NpgsqlException Driver() =>
        new("The connection pool has been exhausted, either raise 'Max Pool Size' or 'Timeout' in your connection string.", new TimeoutException("The operation has timed out."));

    /// <summary>An exception nothing names: a bug, not a store fault.</summary>
    internal static InvalidOperationException Unclassified(string message) => new(message);

    /// <summary>The failed result a reporting reader returns for a read.</summary>
    internal static Error ReportedRead() => new(PublishedStoreFaults.LogUnreadableType, PublishedStoreFaults.LogUnreadableTitle, "reported by the test double");

    /// <summary>The failed result a reporting reader returns for the append.</summary>
    internal static Error ReportedAppend() => new(PublishedStoreFaults.StoreUnavailableType, PublishedStoreFaults.StoreUnavailableTitle, "reported by the test double");
}

/// <summary>
/// The production event store with a fault the test arms and disarms after it has seeded (ruling 9's decorators; the plan's "armed and disarmed by the test").
/// A host that registers it sees one store: <c>LogBoundKeys</c>, the access-token validator and every use case read through it, so one replacement is every
/// reader. It forwards to the real store; the fault is never fabricated by returning the real store's own result.
/// </summary>
internal sealed class FaultableEventStore(IEventStore inner) : IEventStore
{
    private readonly object _gate = new();
    private EventStoreFault _fault;
    private Func<Exception> _make = StoreFaultKinds.Driver;
    private bool _committedSinceArmed;
    private readonly TaskCompletionSource _hangStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _hangEnded = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _reads;
    private int _appends;
    private int _committed;

    /// <summary>The fault in force.</summary>
    internal EventStoreFault Fault
    {
        get { lock (_gate) return _fault; }
    }

    /// <summary>Reads attempted (faulted or not) since the host started.</summary>
    internal int Reads => Volatile.Read(ref _reads);

    /// <summary>Appends attempted since the host started.</summary>
    internal int Appends => Volatile.Read(ref _appends);

    /// <summary>Appends that reached the inner store and returned since the host started.</summary>
    internal int Committed => Volatile.Read(ref _committed);

    /// <summary>Completes when a read has started to wait under <see cref="EventStoreFault.HangUntilCancelled"/>.</summary>
    internal Task HangStarted => _hangStarted.Task;

    /// <summary>Completes when that read has been cancelled and has thrown, so the host has finished with the request.</summary>
    internal Task HangEnded => _hangEnded.Task;

    /// <summary>Arms <paramref name="fault"/>; <paramref name="exception"/> is what a throwing mode throws (a driver fault when omitted).</summary>
    internal void Arm(EventStoreFault fault, Func<Exception>? exception = null)
    {
        lock (_gate)
        {
            _fault = fault;
            _make = exception ?? StoreFaultKinds.Driver;
            _committedSinceArmed = false;
        }
    }

    /// <summary>Stops failing.</summary>
    internal void Disarm()
    {
        lock (_gate) _fault = EventStoreFault.None;
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken = default) =>
        AppendCoreAsync(() => inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken));

    public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition condition, CancellationToken cancellationToken = default) =>
        AppendCoreAsync(() => inner.AppendAsync(aggregateId, expectedVersion, events, condition, cancellationToken));

    private async Task<Result<IReadOnlyList<AppendedEvent>>> AppendCoreAsync(Func<Task<Result<IReadOnlyList<AppendedEvent>>>> append)
    {
        Interlocked.Increment(ref _appends);
        EventStoreFault fault;
        Func<Exception> make;
        lock (_gate)
        {
            fault = _fault;
            make = _make;
        }

        switch (fault)
        {
            case EventStoreFault.ThrowOnAppend:
                throw make();
            case EventStoreFault.ReportAppend:
                return Result<IReadOnlyList<AppendedEvent>>.Fail(StoreFaultKinds.ReportedAppend());
        }

        var result = await append().ConfigureAwait(false);
        Interlocked.Increment(ref _committed);
        lock (_gate) _committedSinceArmed = true;
        if (fault == EventStoreFault.AppendThenThrow) throw make();
        return result;
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        return ReadFault(forward: false, cancellationToken) is { } failed ? failed : inner.ReadByAggregateAsync(aggregateId, cancellationToken);
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        return ReadFault(forward: true, cancellationToken) is { } failed ? failed : inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
    }

    /// <summary>A task that fails the read as the armed mode says, or null where the read is healthy. Throws for a throwing mode.</summary>
    private Task<Result<IReadOnlyList<AppendedEvent>>>? ReadFault(bool forward, CancellationToken cancellationToken)
    {
        EventStoreFault fault;
        Func<Exception> make;
        bool committed;
        lock (_gate)
        {
            fault = _fault;
            make = _make;
            committed = _committedSinceArmed;
        }

        switch (fault)
        {
            case EventStoreFault.ThrowUnreadable:
                throw new LogUnreadableException("armed by the test", make());
            case EventStoreFault.ThrowReads:
                throw make();
            case EventStoreFault.FailReadsAfterFirstAppend when committed:
                throw make();
            case EventStoreFault.HangUntilCancelled:
                return HangAsync(cancellationToken);
            case EventStoreFault.ReportAll:
            case EventStoreFault.ReportForward when forward:
                return Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(StoreFaultKinds.ReportedRead()));
            default:
                return null;
        }
    }

    private async Task<Result<IReadOnlyList<AppendedEvent>>> HangAsync(CancellationToken cancellationToken)
    {
        _hangStarted.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return Result<IReadOnlyList<AppendedEvent>>.Fail(StoreFaultKinds.ReportedRead());
        }
        finally
        {
            _hangEnded.TrySetResult();
        }
    }
}

/// <summary>The nonce store with a driver fault the test arms.</summary>
internal sealed class FaultableNonceStore(IDpopNonceStore inner) : IDpopNonceStore
{
    private int _armed;

    internal void Arm() => Volatile.Write(ref _armed, 1);

    internal void Disarm() => Volatile.Write(ref _armed, 0);

    public Task<Result<IssuedDpopNonce>> IssueAsync(CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _armed) == 1 ? throw StoreFaultKinds.Driver() : inner.IssueAsync(cancellationToken);

    public Task<Result<bool>> IsCurrentAsync(DpopNonce nonce, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _armed) == 1 ? throw StoreFaultKinds.Driver() : inner.IsCurrentAsync(nonce, cancellationToken);
}

/// <summary>The replay cache with a driver fault the test arms.</summary>
internal sealed class FaultableReplayCache(IReplayCache inner) : IReplayCache
{
    private int _armed;

    internal void Arm() => Volatile.Write(ref _armed, 1);

    internal void Disarm() => Volatile.Write(ref _armed, 0);

    public Task<Result<bool>> TryInsertAsync(Jti jti, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _armed) == 1 ? throw StoreFaultKinds.Driver() : inner.TryInsertAsync(jti, expiresAt, cancellationToken);
}

/// <summary>The Registrar's key store (the table behind both of its ports) with a driver fault the test arms.</summary>
internal sealed class FaultableKeyStore(IAuthorKeyResolver resolver, IAuthorKeyRegistry registry) : IAuthorKeyResolver, IAuthorKeyRegistry
{
    private int _armed;

    internal void Arm() => Volatile.Write(ref _armed, 1);

    internal void Disarm() => Volatile.Write(ref _armed, 0);

    private void Check()
    {
        if (Volatile.Read(ref _armed) == 1) throw StoreFaultKinds.Driver();
    }

    public Task<Result<PublicKeyMaterial>> ResolveAsync(AgentId agentId, Kid kid, ServerTimestamp at, CancellationToken cancellationToken = default)
    {
        Check();
        return resolver.ResolveAsync(agentId, kid, at, cancellationToken);
    }

    public Task<Result<RegisteredKey>> EnrollAsync(AgentId agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default)
    {
        Check();
        return registry.EnrollAsync(agentId, key, notBefore, cancellationToken);
    }

    public Task<Result<RegisteredKey>> AddAsync(AgentId agentId, PublicKeyMaterial key, DateTimeOffset validFrom, CancellationToken cancellationToken = default)
    {
        Check();
        return registry.AddAsync(agentId, key, validFrom, cancellationToken);
    }

    public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(AgentId agentId, CancellationToken cancellationToken = default)
    {
        Check();
        return registry.KeysForAsync(agentId, cancellationToken);
    }
}
