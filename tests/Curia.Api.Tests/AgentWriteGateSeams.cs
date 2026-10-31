using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Curia.Application.Budget;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Domain.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G48's seams on the host (probes BRB16 to BRB41, BRB60 to BRB65), the shape of <c>RevisionOnceSeams.cs</c>. Every decorator forwards to the
/// real object; none fabricates an answer. The pass-through double, <see cref="PassThroughAgentWriteGate"/>, is named because several facts need the host
/// as it was before the gate (the controls that must see the overshoot, and the revision-gate facts whose object is the revision gate).
/// </summary>
internal sealed class PassThroughAgentWriteGate : IAgentWriteGate
{
    /// <summary>Registers this double in place of the host's gate: the host as it was before R7.27, for a fact whose object is a gate the agent gate would stand in front of.</summary>
    internal static void Install(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.RemoveAll<IAgentWriteGate>();
        services.AddSingleton<IAgentWriteGate>(new PassThroughAgentWriteGate());
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The hold is the result: the caller owns it and disposes it.")]
    public Task<Result<IAsyncDisposable>> EnterAsync(AgentId agent, CancellationToken cancellationToken) =>
        Task.FromResult(Result<IAsyncDisposable>.Ok(new NullHold()));

    private sealed class NullHold : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>What the events table holds for one agent: the posts it authored, the acceptances it recorded, and the vector rows of its posts.</summary>
internal sealed record GateSnapshot(long Posts, long Accepts, long Vectors)
{
    /// <summary>The snapshot recorded when the table could not be read.</summary>
    internal static readonly GateSnapshot ReadFailed = new(-1, -1, -1);
}

/// <summary>The order in which the agent gate and the revision gate were entered, across both observers.</summary>
internal sealed class GateSequence
{
    internal ConcurrentQueue<string> Entries { get; } = new();

    internal void Note(string what) => Entries.Enqueue(what);

    internal void Clear() => Entries.Clear();
}

/// <summary>
/// A decorator over the host's <see cref="IAgentWriteGate"/> that records every subject that reached the gate (<see cref="Entered"/>), how long each entry
/// waited (<see cref="Waits"/>), how many holds are open now (<see cref="HeldCount"/>), and, at each hold's first release, what the table holds for the agent
/// (<see cref="Releases"/>), counted from the table and not through the host. <see cref="UsePassThrough"/> serves the host as it was before the gate;
/// <see cref="FailEntries"/> answers every entry with the gate-unavailable refusal.
/// </summary>
internal sealed class ObservingAgentWriteGate(IAgentWriteGate inner, GateSequence sequence, Func<string, Task<GateSnapshot>> snapshotOf) : IAgentWriteGate
{
    private readonly PassThroughAgentWriteGate _passThrough = new();
    private int _held;
    private int _entered;
    private int _released;

    /// <summary>The subjects that reached the gate, whether or not they entered, in order.</summary>
    internal ConcurrentQueue<string> Entered { get; } = new();

    /// <summary>How long each entry waited before it was answered, in order.</summary>
    internal ConcurrentQueue<TimeSpan> Waits { get; } = new();

    /// <summary>Per entered hold, the subject and what the table held for it when the hold was granted.</summary>
    internal ConcurrentQueue<(string Subject, GateSnapshot AtEntry)> Grants { get; } = new();

    /// <summary>Per released hold, the subject and what the table held for it at the hold's first release, in order.</summary>
    internal ConcurrentQueue<(string Subject, GateSnapshot AtRelease)> Releases { get; } = new();

    internal int HeldCount => Volatile.Read(ref _held);

    internal int EnteredCount => Volatile.Read(ref _entered);

    internal int ReleasedCount => Volatile.Read(ref _released);

    /// <summary>When true, the host is served as it was before the gate: every entry is granted at once.</summary>
    internal bool UsePassThrough { get; set; }

    /// <summary>When true, every entry is refused as the gate's store being unreachable.</summary>
    internal bool FailEntries { get; set; }

    /// <summary>Forgets what was observed and puts the gate back to the real one; for a fact to start from nothing on the shared host.</summary>
    internal void Reset()
    {
        UsePassThrough = false;
        FailEntries = false;
        Entered.Clear();
        Waits.Clear();
        Grants.Clear();
        Releases.Clear();
        sequence.Clear();
        Interlocked.Exchange(ref _entered, 0);
        Interlocked.Exchange(ref _released, 0);
    }

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The inner hold is owned by the returned ObservedHold, which disposes it.")]
    public async Task<Result<IAsyncDisposable>> EnterAsync(AgentId agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        Entered.Enqueue(agent.Value);
        sequence.Note("agent:" + agent.Value);

        if (FailEntries) return Result<IAsyncDisposable>.Fail(BudgetErrors.GateUnavailable());

        var timer = Stopwatch.StartNew();
        var entered = await (UsePassThrough ? _passThrough : inner).EnterAsync(agent, cancellationToken).ConfigureAwait(false);
        Waits.Enqueue(timer.Elapsed);
        if (!entered.TryGetValue(out var hold, out _)) return entered;

        Interlocked.Increment(ref _held);
        Interlocked.Increment(ref _entered);
        Grants.Enqueue((agent.Value, await SafeSnapshotAsync(agent.Value).ConfigureAwait(false)));
        return Result<IAsyncDisposable>.Ok(new ObservedHold(this, agent.Value, hold!));
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed observation is recorded as ReadFailed and fails the fact by its own message.")]
    private async Task<GateSnapshot> SafeSnapshotAsync(string subject)
    {
        try
        {
            return await snapshotOf(subject).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return GateSnapshot.ReadFailed;
        }
    }

    private sealed class ObservedHold(ObservingAgentWriteGate gate, string subject, IAsyncDisposable inner) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                gate.Releases.Enqueue((subject, await gate.SafeSnapshotAsync(subject).ConfigureAwait(false)));
            }
            finally
            {
                Interlocked.Decrement(ref gate._held);
                Interlocked.Increment(ref gate._released);
                await inner.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

/// <summary>The host's <see cref="IRevisionGate"/> with each entry noted in the shared <see cref="GateSequence"/> (BRB34).</summary>
internal sealed class SequencedRevisionGate(IRevisionGate inner, GateSequence sequence) : IRevisionGate
{
    public Task<Result<IAsyncDisposable>> TryEnterAsync(string prevDigest, CancellationToken cancellationToken)
    {
        sequence.Note("revision:" + prevDigest);
        return inner.TryEnterAsync(prevDigest, cancellationToken);
    }
}

/// <summary>
/// A decorator over the host's event store for the burst probes: it parks the next matching append until released (the forced interleave), throws at a
/// matching append, loses the next conditioned append to the author's stream, and counts the whole-log reads and the concurrency conflicts the real store
/// raised. A parked append goes on to the real store when released, so what a fact observes is the real store's state.
/// </summary>
internal sealed class ParkingEventStore(IEventStore inner) : IEventStore
{
    private readonly object _gate = new();
    private TaskCompletionSource _release = NewRelease();
    private Func<DomainEvent, bool>? _parkWhen;
    private Func<DomainEvent, bool>? _throwWhen;
    private Func<DomainEvent, bool>? _parkEveryWhen;
    private readonly Queue<TaskCompletionSource> _parkedEvery = new();
    private int _permits;
    private bool _loseNextConditioned;
    private int _parked;
    private int _reads;
    private int _conflicts;
    private int _conditionedAppends;

    private static TaskCompletionSource NewRelease() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The whole-log reads served since the host started (a forward read is what <c>ReadAllAsync</c> makes).</summary>
    internal int ForwardReads => Volatile.Read(ref _reads);

    /// <summary>The appends the real store refused with a concurrency conflict.</summary>
    internal int ConcurrencyConflicts => Volatile.Read(ref _conflicts);

    /// <summary>The appends that carried a stream condition (the submit route's, one per attempt).</summary>
    internal int ConditionedAppends => Volatile.Read(ref _conditionedAppends);

    internal int Parked
    {
        get { lock (_gate) return _parked; }
    }

    /// <summary>The next append holding an event <paramref name="matches"/> parks until <see cref="Release"/>; the ones after it do not.</summary>
    internal void ParkNext(Func<DomainEvent, bool> matches)
    {
        lock (_gate)
        {
            _parkWhen = matches;
            _parked = 0;
            _release = NewRelease();
        }
    }

    /// <summary>
    /// Every append holding an event <paramref name="matches"/> parks until <see cref="ReleaseOne"/> gives it a permit, in the order they reach the semaphore, until
    /// <see cref="Reset"/>. The test releases the appends one at a time, so what it observes does not depend on how fast the machine is.
    /// </summary>
    internal void ParkEvery(Func<DomainEvent, bool> matches)
    {
        lock (_gate)
        {
            _parkEveryWhen = matches;
            _parked = 0;
            _permits = 0;
            _parkedEvery.Clear();
        }
    }

    /// <summary>Lets exactly one parked append (of <see cref="ParkEvery"/>) go on to the real store.</summary>
    internal void ReleaseOne()
    {
        lock (_gate)
        {
            if (_parkedEvery.Count > 0) _parkedEvery.Dequeue().TrySetResult();
            else _permits++;
        }
    }

    /// <summary>Every append holding an event <paramref name="matches"/> throws instead of reaching the store, until <see cref="Reset"/>.</summary>
    internal void ThrowAt(Func<DomainEvent, bool> matches)
    {
        lock (_gate) _throwWhen = matches;
    }

    /// <summary>The next append that carries a stream condition and a <c>post.accepted</c> is answered as the author's stream having moved, writing nothing.</summary>
    internal void LoseNextConditionedPostAppend()
    {
        lock (_gate) _loseNextConditioned = true;
    }

    internal void Release()
    {
        TaskCompletionSource release;
        lock (_gate)
        {
            _parkWhen = null;
            release = _release;
        }

        release.TrySetResult();
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _throwWhen = null;
            _parkEveryWhen = null;
            _loseNextConditioned = false;
        }

        lock (_gate)
        {
            _permits = int.MaxValue / 2;
            while (_parkedEvery.Count > 0) _parkedEvery.Dequeue().TrySetResult();
        }

        Release();
    }

    internal async Task<bool> WaitForParkedAsync(int count, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (Parked >= count) return true;
            await Task.Delay(10).ConfigureAwait(false);
        }

        return Parked >= count;
    }

    private async Task<Result<IReadOnlyList<AppendedEvent>>?> BeforeAppendAsync(AggregateId aggregateId, IReadOnlyList<DomainEvent> events, bool conditioned, CancellationToken cancellationToken)
    {
        Task? wait = null;
        Task? waitEvery = null;
        lock (_gate)
        {
            if (conditioned) Interlocked.Increment(ref _conditionedAppends);
            if (_throwWhen is not null && events.Any(_throwWhen)) throw new InvalidOperationException("the test store failed this append");

            if (conditioned && _loseNextConditioned && events.Any(e => string.Equals(e.Type.Value, "post.accepted", StringComparison.Ordinal)))
            {
                _loseNextConditioned = false;
                return Result<IReadOnlyList<AppendedEvent>>.Fail(DomainErrors.ConditionConflict(aggregateId, AggregateVersion.New, AggregateVersion.From(1).Match(v => v, _ => AggregateVersion.New)));
            }

            if (_parkEveryWhen is not null && events.Any(_parkEveryWhen))
            {
                _parked++;
                if (_permits > 0) _permits--;
                else
                {
                    var parkedHere = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _parkedEvery.Enqueue(parkedHere);
                    waitEvery = parkedHere.Task;
                }
            }
            else if (_parkWhen is not null && events.Any(_parkWhen))
            {
                _parkWhen = null;
                _parked++;
                wait = _release.Task;
            }
        }

        if (waitEvery is not null) await waitEvery.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (wait is not null) await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
        return null;
    }

    private Result<IReadOnlyList<AppendedEvent>> Counted(Result<IReadOnlyList<AppendedEvent>> result)
    {
        if (!result.TryGetValue(out _, out var error) && string.Equals(error!.Type, DomainErrors.ConcurrencyConflictType, StringComparison.Ordinal))
            Interlocked.Increment(ref _conflicts);

        return result;
    }

    public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken = default)
    {
        var early = await BeforeAppendAsync(aggregateId, events, conditioned: false, cancellationToken).ConfigureAwait(false);
        return early ?? Counted(await inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken).ConfigureAwait(false));
    }

    public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition condition, CancellationToken cancellationToken = default)
    {
        var early = await BeforeAppendAsync(aggregateId, events, conditioned: true, cancellationToken).ConfigureAwait(false);
        return early ?? Counted(await inner.AppendAsync(aggregateId, expectedVersion, events, condition, cancellationToken).ConfigureAwait(false));
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default) =>
        inner.ReadByAggregateAsync(aggregateId, cancellationToken);

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _reads);
        return inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
    }
}

/// <summary>
/// The host's <see cref="IVectorIndex"/> with its upserts failing on demand: the "index that fails after the append" path (BRB32, BRB60). Errata G51's modes
/// (the D92 PR, Task 0): fail the upserts of chosen digests (<see cref="FailDigests"/>), throw an exception where a result would be returned
/// (<see cref="ThrowWith"/>), or hold inside the upsert until released (<see cref="HoldUpserts"/>), recording whether the caller's token was cancelled while it held.
/// </summary>
internal sealed class FailingVectorIndex(IVectorIndex inner) : IVectorIndex
{
    private readonly Lock _gate = new();
    private readonly HashSet<string> _failDigests = [];
    private TaskCompletionSource? _held;
    private TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _upserts;
    private int _cancelledWhileHeld;

    internal bool FailUpserts { get; set; }

    /// <summary>When set, every nearest-neighbour read fails as a returned <c>Error</c> (the near-duplicate check's read, before the append).</summary>
    internal bool FailNearest { get; set; }

    /// <summary>When set, every upsert throws what this makes (a fault that is not a returned <c>Error</c>).</summary>
    internal Func<Exception>? ThrowWith { get; set; }

    /// <summary>How many upserts reached this decorator since the last <see cref="ResetModes"/>.</summary>
    internal int Upserts => Volatile.Read(ref _upserts);

    /// <summary>How many upserts saw their token cancelled while they were held.</summary>
    internal int CancelledWhileHeld => Volatile.Read(ref _cancelledWhileHeld);

    /// <summary>Fails the upsert of <paramref name="digest"/> and of no other.</summary>
    internal void FailDigests(params string[] digests)
    {
        lock (_gate) foreach (var d in digests) _failDigests.Add(d);
    }

    /// <summary>Stops failing the upserts of <paramref name="digests"/>.</summary>
    internal void HealDigests(params string[] digests)
    {
        lock (_gate) foreach (var d in digests) _failDigests.Remove(d);
    }

    /// <summary>The next upserts block inside the decorator until <see cref="ReleaseUpserts"/>; <see cref="WaitForHeldAsync"/> says when one has arrived.</summary>
    internal void HoldUpserts()
    {
        lock (_gate)
        {
            _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal void ReleaseUpserts()
    {
        TaskCompletionSource? held;
        lock (_gate)
        {
            held = _held;
            _held = null;
        }

        held?.TrySetResult();
    }

    /// <summary>Whether an upsert has arrived and is held, within <paramref name="within"/>.</summary>
    internal async Task<bool> WaitForHeldAsync(TimeSpan within)
    {
        Task entered;
        lock (_gate) entered = _entered.Task;
        return await Task.WhenAny(entered, Task.Delay(within)) == entered;
    }

    /// <summary>Every mode off and the counters at zero.</summary>
    internal void ResetModes()
    {
        FailUpserts = false;
        FailNearest = false;
        ThrowWith = null;
        ReleaseUpserts();
        lock (_gate) _failDigests.Clear();
        Volatile.Write(ref _upserts, 0);
        Volatile.Write(ref _cancelledWhileHeld, 0);
    }

    public async Task<Result<IndexedVector>> UpsertAsync(string digest, string postId, long sequence, Embedding embedding, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _upserts);

        TaskCompletionSource? held;
        TaskCompletionSource entered;
        lock (_gate)
        {
            held = _held;
            entered = _entered;
        }

        if (held is not null)
        {
            entered.TrySetResult();
            try
            {
                await held.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref _cancelledWhileHeld);
                throw;
            }

            if (cancellationToken.IsCancellationRequested) Interlocked.Increment(ref _cancelledWhileHeld);
        }

        if (ThrowWith is { } make) throw make();

        bool failsThisDigest;
        lock (_gate) failsThisDigest = _failDigests.Contains(digest);
        return FailUpserts || failsThisDigest
            ? Result<IndexedVector>.Fail(new Error("curia/retrieval/index-unavailable", "The vector index failed after the append"))
            : await inner.UpsertAsync(digest, postId, sequence, embedding, cancellationToken);
    }

    public Task<Result<System.Collections.Immutable.ImmutableArray<VectorMatch>>> NearestAsync(Embedding query, int limit, CancellationToken cancellationToken = default) =>
        FailNearest
            ? Task.FromResult(Result<System.Collections.Immutable.ImmutableArray<VectorMatch>>.Fail(new Error("curia/retrieval/index-unavailable", "The vector index failed before the append")))
            : inner.NearestAsync(query, limit, cancellationToken);

    public Task<Result<long>> CountAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => inner.CountAsync(model, cancellationToken);

    public Task<Result<IReadOnlySet<string>>> IndexedDigestsAsync(EmbeddingModel model, CancellationToken cancellationToken = default) => inner.IndexedDigestsAsync(model, cancellationToken);
}

/// <summary>
/// The host for errata G48's route facts: the production Forum with its event store wrapped by a <see cref="ParkingEventStore"/>, its agent write gate by an
/// <see cref="ObservingAgentWriteGate"/>, its revision gate by a <see cref="SequencedRevisionGate"/> and its vector index by a <see cref="FailingVectorIndex"/>.
/// Every seam is reached through the host's own services (<see cref="StoreOf"/>, <see cref="GateOf"/>), so a second host built with
/// <c>WithWebHostBuilder</c> over the same database has its own seams: the two hosts share nothing in this process but the database.
/// </summary>
public sealed class AgentWriteGateForumFixture : ForumFixture
{
    internal static ParkingEventStore StoreOf(IServiceProvider services) => (ParkingEventStore)services.GetRequiredService<IEventStore>();

    internal static ObservingAgentWriteGate GateOf(IServiceProvider services) => (ObservingAgentWriteGate)services.GetRequiredService<IAgentWriteGate>();

    internal static FailingVectorIndex IndexOf(IServiceProvider services) => (FailingVectorIndex)services.GetRequiredService<IVectorIndex>();

    internal static GateSequence SequenceOf(IServiceProvider services) => services.GetRequiredService<GateSequence>();

    /// <summary>How the production composition registered <see cref="IAgentWriteGate"/>: its lifetime and how many registrations it made (BRB52), captured before the decorator replaced it.</summary>
    internal (ServiceLifetime Lifetime, int Registrations) ProductionRegistration { get; private set; }

    /// <summary>Whether this fixture's log has been seeded to the reference size, and the gate that makes the seeding happen once (BRB57).</summary>
    internal SemaphoreSlim SeedGate { get; } = new(1, 1);

    internal bool LogSeeded { get; set; }

    internal ParkingEventStore Store => StoreOf(Services);

    internal ObservingAgentWriteGate Gate => GateOf(Services);

    internal FailingVectorIndex Index => IndexOf(Services);

    internal GateSequence Sequence => SequenceOf(Services);

    /// <summary>Every seam on a host back to forwarding, with what was observed forgotten.</summary>
    internal static void ResetSeams(IServiceProvider services)
    {
        StoreOf(services).Reset();
        GateOf(services).Reset();
        IndexOf(services).ResetModes();
        SequenceOf(services).Clear();
        _ = services.GetRequiredService<IRevisionGate>();
    }

    internal void ResetSeams() => ResetSeams(Services);

    /// <summary>What the table holds for <paramref name="agentId"/>, read by SQL from the fixture's own connection string (the password included).</summary>
    internal async Task<GateSnapshot> SnapshotAsync(string agentId)
    {
        await using var admin = new NpgsqlConnection(ConnectionString);
        await admin.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT " +
            "(SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'author' = @a), " +
            "(SELECT count(*) FROM events WHERE event_type = 'answer.accepted' AND payload->>'accepted_by' = @a), " +
            "(SELECT count(*) FROM post_embeddings e WHERE EXISTS (SELECT 1 FROM events v WHERE v.event_type = 'post.accepted' " +
            "AND v.payload->>'author' = @a AND v.payload->>'post_id' = e.post_id));",
            admin);
        command.Parameters.AddWithValue("a", agentId);
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);
        await reader.ReadAsync().ConfigureAwait(false);
        return new GateSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static ServiceDescriptor Registered<T>(IServiceCollection services)
    {
        var registered = services.Last(d => d.ServiceType == typeof(T));
        if (registered.ImplementationFactory is null)
            throw new InvalidOperationException($"{typeof(T).Name} is registered by a factory; this decorator wraps what it builds");

        return registered;
    }

    protected override void ConfigureStore(IServiceCollection services)
    {
        var fixture = this;
        services.AddSingleton(_ => new GateSequence());

        var store = Registered<IEventStore>(services);
        services.Remove(store);
        services.AddSingleton<IEventStore>(sp => new ParkingEventStore((IEventStore)store.ImplementationFactory!(sp)));

        var gate = Registered<IAgentWriteGate>(services);
        ProductionRegistration = (gate.Lifetime, services.Count(d => d.ServiceType == typeof(IAgentWriteGate)));
        services.Remove(gate);
        services.AddSingleton<IAgentWriteGate>(sp => new ObservingAgentWriteGate(
            (IAgentWriteGate)gate.ImplementationFactory!(sp), sp.GetRequiredService<GateSequence>(), fixture.SnapshotAsync));

        var revision = Registered<IRevisionGate>(services);
        services.Remove(revision);
        services.AddSingleton<IRevisionGate>(sp => new SequencedRevisionGate((IRevisionGate)revision.ImplementationFactory!(sp), sp.GetRequiredService<GateSequence>()));

        var index = Registered<IVectorIndex>(services);
        services.Remove(index);
        services.AddSingleton<IVectorIndex>(sp => new FailingVectorIndex((IVectorIndex)index.ImplementationFactory!(sp)));
    }
}

/// <summary>
/// The wall clock of a host (<see cref="ManualTimeProvider"/>'s instants) with a monotonic timestamp that moves only when the test says and timers that never
/// fire: the agent write queue's wait is then measured on this clock alone, so a fact can place a request anywhere in its wait without waiting (BRB67).
/// </summary>
internal sealed class StalledGateClock(ManualTimeProvider wall) : TimeProvider
{
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => wall.GetUtcNow();

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    /// <summary>Moves the queue's clock, and nothing else, forward.</summary>
    internal void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new InertTimer();

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>The oracles the budget probes share (ruling 11 (d), (e)): the events table counted from the fixture's own connection string, never a response.</summary>
internal static class BudgetOracle
{
    /// <summary>The trailing window the budgets count in (R7.22's 24 hours, which Table 11's "per day" means).</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>The <c>post.accepted</c> events whose author is <paramref name="agentId"/> inside the trailing window.</summary>
    internal static Task<long> PostsInWindowAsync(ForumFixture forum, string agentId) =>
        CountAsync(forum, "post.accepted", "payload->>'author'", agentId);

    /// <summary>The <c>answer.accepted</c> events whose <c>accepted_by</c> is <paramref name="agentId"/> inside the trailing window.</summary>
    internal static Task<long> AcceptsInWindowAsync(ForumFixture forum, string agentId) =>
        CountAsync(forum, "answer.accepted", "payload->>'accepted_by'", agentId);

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only concatenated parts are two constant strings chosen by this file's own callers; the values are bound.")]
    private static async Task<long> CountAsync(ForumFixture forum, string eventType, string actorExpression, string agentId)
    {
        await using var admin = new NpgsqlConnection(forum.ConnectionString);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"SELECT count(*) FROM events WHERE event_type = @type AND {actorExpression} = @a AND server_ts > @since;", admin);
        command.Parameters.AddWithValue("type", eventType);
        command.Parameters.AddWithValue("a", agentId);
        command.Parameters.AddWithValue("since", forum.Now - Window);
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// A Forum host whose agent write gate is the pass-through double (errata G48, ruling 22): the fixture of the facts whose object is a gate or a race the agent
/// gate now stands in front of, and that keep their strength by running the host as it was before it. Their real-gate twins are BRB63 and BRB64.
/// </summary>
public sealed class PassThroughGateForumFixture : ForumFixture
{
    protected override void ConfigureStore(IServiceCollection services) => PassThroughAgentWriteGate.Install(services);
}

/// <summary>
/// Errata G51's hosts that need a clock with timers (the D92 PR, ruling 15): <see cref="ManualTimeProvider"/> moves <c>GetUtcNow</c> only, so a bound made with
/// <c>CreateCancellationTokenSource</c> or a loop on <c>Task.Delay(.., TimeProvider)</c> cannot see an advance (trap 68). A fact that needs one builds a host
/// of its own with <see cref="TimerClockHost"/>, after its identities are made: the clock starts at the fixture clock's current instant, so the tier the
/// stored events compute against it agrees.
/// </summary>
internal static class TimerClockHost
{
    /// <summary>A second host over the fixture's database whose <see cref="TimeProvider"/> is <paramref name="clock"/> and whose vector index is the fixture's decorator, wrapped afresh.</summary>
    internal static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Of(AgentWriteGateForumFixture forum, Curia.Tests.Shared.TimerManualTimeProvider clock, LogCapture? log = null) =>
        forum.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            if (log is not null) services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(log);
        }));

    /// <summary>A timer-capable clock at the fixture clock's current instant.</summary>
    internal static Curia.Tests.Shared.TimerManualTimeProvider At(ForumFixture forum) => new(forum.Now);
}
