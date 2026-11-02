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

    /// <summary>
    /// Every append returns a failed <see cref="Result{T}"/> of the type the route already decides again on (<c>DomainErrors.ConditionConflictType</c>, the author's stream
    /// moved) and commits nothing; reads are healthy. The fault is marked, so the reads after it are counted (errata G55, AFC38 and AFC39: a failed result is not a thrown fault, and no confirm is made).
    /// </summary>
    ReportConflictAppend,

    /// <summary>The append throws a driver fault and commits nothing.</summary>
    ThrowOnAppend,

    /// <summary>The append commits and then throws a driver fault: the act is stored and the caller is told nothing.</summary>
    AppendThenThrow,

    /// <summary>
    /// The append throws the timeout-classified driver fault at once and commits later, when the test releases <see cref="FaultableEventStore.ReleaseLateCommit"/>:
    /// the act is not stored when the caller is told, and is stored after (errata G53, the plan's ruling 10: a store that timed out can finish after it answered).
    /// </summary>
    ThrowThenCommitWhenReleased,

    /// <summary>
    /// The append commits, then waits for the caller's token to be cancelled and throws that cancellation: a request aborted by its client after its act is stored
    /// (the cancellation door of errata G54, R8.83). <see cref="FaultableEventStore.CommittedAndWaiting"/> says when the commit is made and the wait has begun.
    /// </summary>
    CommitThenCancel,

    /// <summary>Reads are healthy until the first append after arming commits, and fail from then on: the receipt must not read a store after the commit.</summary>
    FailReadsAfterFirstAppend,

    /// <summary>Every read waits until the caller's token is cancelled, then throws the cancellation: a client that aborts the request mid-read (ULG39).</summary>
    HangUntilCancelled,

    /// <summary>
    /// The append commits and throws a driver fault, and from then on every read throws <see cref="LogUnreadableException"/> around a driver fault: the confirm's own
    /// read fails over a post the log holds (errata G55, R8.84; AFC3). Reads before the fault are healthy.
    /// </summary>
    AppendThenThrowThenFailReads,

    /// <summary>
    /// The append commits and throws a driver fault, and from then on every read waits for its token and throws that cancellation: the confirm's read outlasts its
    /// bound (AFC12). Reads before the fault are healthy.
    /// </summary>
    AppendThenThrowThenHangReads,

    /// <summary>
    /// The append commits and throws a driver fault, and the first read after the fault is held until the probe calls <see cref="FaultableEventStore.ReleaseHeldRead"/>
    /// (or the read's token is cancelled): the confirm's read is in progress while the probe looks at the gates and the registration (AFC4, AFC16).
    /// <see cref="FaultableEventStore.HeldReadStarted"/> says when it is held. Once per arming.
    /// </summary>
    HoldNextReadAfterFault,

    /// <summary>
    /// On the first append after arming the act is committed, then the callback the probe gave to <see cref="FaultableEventStore.OnInterleave"/> runs (another agent's
    /// post, appended through the same host, with the fault suspended for its duration), then the append throws a driver fault: the log holds the act at a lower
    /// sequence than another agent's newest post (AFC1). Once per arming; later appends are healthy.
    /// </summary>
    InterleaveOtherAgentsPost,
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

    /// <summary>The failed result an append that lost the race on the author's stream returns: the type the route decides again on (R6.56).</summary>
    internal static Error ReportedConflict() => new(DomainErrors.ConditionConflictType, "The author's stream moved", "reported by the test double");

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
    private readonly TaskCompletionSource _lateGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _lateCommitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _committedAndWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _lateStarted;
    private int _interleaveStarted;
    private bool _faulted;
    private int _holdTaken;
    private Func<CancellationToken, Task>? _interleave;
    private readonly TaskCompletionSource _heldReadStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _heldReadGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _readsAfterFault;
    private int _forwardReadsAfterFault;
    private int _tailReadsAfterFault;
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

    /// <summary>
    /// Reads attempted (faulted or not) after an append left by an exception because of an armed mode, since that fault: the count of the confirm's reads and of any
    /// other read the host made after it. Zero before a fault. Reset by <see cref="Arm"/>.
    /// </summary>
    internal int ReadsAfterFault => Volatile.Read(ref _readsAfterFault);

    /// <summary>The forward (whole-log) reads among <see cref="ReadsAfterFault"/>.</summary>
    internal int ForwardReadsAfterFault => Volatile.Read(ref _forwardReadsAfterFault);

    /// <summary>
    /// The forward reads among <see cref="ReadsAfterFault"/> that began past the start of the log: the confirm's own shape (it reads the events after the sequence the route
    /// held, <c>log[^1].Seq</c>), which no read of the whole log, from sequence zero, has. A decide-again after a failed result reads from zero and is not counted.
    /// </summary>
    internal int TailReadsAfterFault => Volatile.Read(ref _tailReadsAfterFault);

    /// <summary>Completes when <see cref="EventStoreFault.HoldNextReadAfterFault"/> holds a read.</summary>
    internal Task HeldReadStarted => _heldReadStarted.Task;

    /// <summary>Lets the read that <see cref="EventStoreFault.HoldNextReadAfterFault"/> holds go ahead.</summary>
    internal void ReleaseHeldRead() => _heldReadGate.TrySetResult();

    /// <summary>The callback <see cref="EventStoreFault.InterleaveOtherAgentsPost"/> runs between the commit and the throw. It appends through the host; the fault is suspended while it runs.</summary>
    internal Func<CancellationToken, Task>? OnInterleave
    {
        get { lock (_gate) return _interleave; }
        set { lock (_gate) _interleave = value; }
    }

    /// <summary>Appends attempted since the host started.</summary>
    internal int Appends => Volatile.Read(ref _appends);

    /// <summary>Appends that reached the inner store and returned since the host started.</summary>
    internal int Committed => Volatile.Read(ref _committed);

    /// <summary>Completes when a read has started to wait under <see cref="EventStoreFault.HangUntilCancelled"/>.</summary>
    internal Task HangStarted => _hangStarted.Task;

    /// <summary>Completes when that read has been cancelled and has thrown, so the host has finished with the request.</summary>
    internal Task HangEnded => _hangEnded.Task;

    /// <summary>Completes when <see cref="EventStoreFault.CommitThenCancel"/> has committed and is waiting for the caller's token: the probe aborts the request then.</summary>
    internal Task CommittedAndWaiting
    {
        get { lock (_gate) return _committedAndWaiting.Task; }
    }

    /// <summary>Lets the commit that <see cref="EventStoreFault.ThrowThenCommitWhenReleased"/> deferred go ahead.</summary>
    internal void ReleaseLateCommit() => _lateGate.TrySetResult();

    /// <summary>Completes when the deferred commit has been made, so a probe reads the events table only after it landed.</summary>
    internal Task LateCommitted => _lateCommitted.Task;

    /// <summary>Arms <paramref name="fault"/>; <paramref name="exception"/> is what a throwing mode throws (a driver fault when omitted).</summary>
    internal void Arm(EventStoreFault fault, Func<Exception>? exception = null)
    {
        lock (_gate)
        {
            _fault = fault;
            _make = exception ?? StoreFaultKinds.Driver;
            _committedSinceArmed = false;
            _faulted = false;
            Volatile.Write(ref _readsAfterFault, 0);
            Volatile.Write(ref _forwardReadsAfterFault, 0);
            Volatile.Write(ref _tailReadsAfterFault, 0);
            Volatile.Write(ref _interleaveStarted, 0);
            Volatile.Write(ref _holdTaken, 0);
            _committedAndWaiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref _lateStarted, 0);
        }
    }

    /// <summary>Stops failing.</summary>
    internal void Disarm()
    {
        lock (_gate) _fault = EventStoreFault.None;
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken = default) =>
        AppendCoreAsync(token => inner.AppendAsync(aggregateId, expectedVersion, events, token), cancellationToken);

    public Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition condition, CancellationToken cancellationToken = default) =>
        AppendCoreAsync(token => inner.AppendAsync(aggregateId, expectedVersion, events, condition, token), cancellationToken);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The deferred commit runs on a task nobody awaits; whatever it throws is handed to the probe through LateCommitted.")]
    private async Task<Result<IReadOnlyList<AppendedEvent>>> AppendCoreAsync(
        Func<CancellationToken, Task<Result<IReadOnlyList<AppendedEvent>>>> append, CancellationToken cancellationToken)
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
                MarkFaulted();
                throw make();
            case EventStoreFault.ReportAppend:
                MarkFaulted();
                return Result<IReadOnlyList<AppendedEvent>>.Fail(StoreFaultKinds.ReportedAppend());
            case EventStoreFault.ReportConflictAppend:
                MarkFaulted();
                return Result<IReadOnlyList<AppendedEvent>>.Fail(StoreFaultKinds.ReportedConflict());
            case EventStoreFault.ThrowThenCommitWhenReleased when Interlocked.CompareExchange(ref _lateStarted, 1, 0) == 0:
                // The commit is the gate's, never a sleep's, and it is not the request's: the caller has been answered by then. Once per arming.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _lateGate.Task.ConfigureAwait(false);
                        await append(CancellationToken.None).ConfigureAwait(false);
                        Interlocked.Increment(ref _committed);
                        _lateCommitted.TrySetResult();
                    }
                    catch (Exception ex)
                    {
                        _lateCommitted.TrySetException(ex);
                    }
                }, CancellationToken.None);
                MarkFaulted();
                throw make();
            case EventStoreFault.ThrowThenCommitWhenReleased:
                MarkFaulted();
                throw make();
        }

        var result = await append(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _committed);
        lock (_gate) _committedSinceArmed = true;
        if (fault == EventStoreFault.InterleaveOtherAgentsPost && Interlocked.CompareExchange(ref _interleaveStarted, 1, 0) == 0)
        {
            Func<CancellationToken, Task>? other;
            lock (_gate)
            {
                other = _interleave;
                _fault = EventStoreFault.None;
            }

            try
            {
                if (other is not null) await other(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _fault = EventStoreFault.InterleaveOtherAgentsPost;
            }

            MarkFaulted();
            throw make();
        }

        if (fault is EventStoreFault.AppendThenThrow or EventStoreFault.AppendThenThrowThenFailReads or EventStoreFault.AppendThenThrowThenHangReads
            or EventStoreFault.HoldNextReadAfterFault)
        {
            MarkFaulted();
            throw make();
        }
        if (fault == EventStoreFault.CommitThenCancel)
        {
            MarkFaulted();
            TaskCompletionSource waiting;
            lock (_gate) waiting = _committedAndWaiting;
            waiting.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        CountAfterFault(forward: false, tail: false);
        if (ReadFault(forward: false, cancellationToken) is { } failed) return failed;
        return TakeHold() ? HoldThenAsync(() => inner.ReadByAggregateAsync(aggregateId, cancellationToken), cancellationToken) : inner.ReadByAggregateAsync(aggregateId, cancellationToken);
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        CountAfterFault(forward: true, tail: afterSeq.Value > 0);
        if (ReadFault(forward: true, cancellationToken) is { } failed) return failed;
        return TakeHold() ? HoldThenAsync(() => inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken), cancellationToken) : inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
    }

    private void MarkFaulted()
    {
        lock (_gate) _faulted = true;
    }

    private void CountAfterFault(bool forward, bool tail)
    {
        bool faulted;
        lock (_gate) faulted = _faulted;
        if (!faulted) return;

        Interlocked.Increment(ref _readsAfterFault);
        if (forward) Interlocked.Increment(ref _forwardReadsAfterFault);
        if (tail) Interlocked.Increment(ref _tailReadsAfterFault);
    }

    /// <summary>A task that fails the read as the armed mode says, or null where the read is healthy. Throws for a throwing mode.</summary>
    private Task<Result<IReadOnlyList<AppendedEvent>>>? ReadFault(bool forward, CancellationToken cancellationToken)
    {
        EventStoreFault fault;
        Func<Exception> make;
        bool committed;
        bool faulted;
        lock (_gate)
        {
            fault = _fault;
            make = _make;
            committed = _committedSinceArmed;
            faulted = _faulted;
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
            case EventStoreFault.AppendThenThrowThenFailReads when faulted:
                throw new LogUnreadableException("armed by the test after the fault", make());
            case EventStoreFault.AppendThenThrowThenHangReads when faulted:
                return HangAsync(cancellationToken);
            case EventStoreFault.ReportAll:
            case EventStoreFault.ReportForward when forward:
                return Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(StoreFaultKinds.ReportedRead()));
            default:
                return null;
        }
    }

    /// <summary>True for the one read <see cref="EventStoreFault.HoldNextReadAfterFault"/> holds: the first read after the fault, once per arming.</summary>
    private bool TakeHold()
    {
        lock (_gate)
        {
            if (_fault != EventStoreFault.HoldNextReadAfterFault || !_faulted) return false;
        }

        return Interlocked.CompareExchange(ref _holdTaken, 1, 0) == 0;
    }

    /// <summary>The held read: it waits for the probe (or its own token) and then goes on to the inner store, the real read, late.</summary>
    private async Task<Result<IReadOnlyList<AppendedEvent>>> HoldThenAsync(Func<Task<Result<IReadOnlyList<AppendedEvent>>>> read, CancellationToken cancellationToken)
    {
        _heldReadStarted.TrySetResult();
        await _heldReadGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        return await read().ConfigureAwait(false);
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
