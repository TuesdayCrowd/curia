using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Primitives;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Curia.Api.Tests;

/// <summary>
/// PR 1's seam on the event store (errata G45, probes RVO1, RVO7, RVO8): a decorator over the host's real store that parks a revision's append
/// until a fact releases it, fails the append on demand, and fails the log reads made while a predicate holds. Nothing is fabricated: a parked
/// append goes on to the real store when released, so what a fact observes is the real store's state.
///
/// <para>An append is a revision's when it holds one <c>post.accepted</c> whose payload <c>kind</c> is <c>revision</c>. A park honours the append's
/// cancellation token, so a client that goes away releases its request as it does in production.</para>
/// </summary>
internal sealed class PausingEventStore(IEventStore inner) : IEventStore
{
    private readonly object _gate = new();
    private TaskCompletionSource _release = NewRelease();
    private bool _armed;
    private bool _once;
    private string _kind = "revision";
    private int _parked;

    private static TaskCompletionSource NewRelease() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>When true, a revision's append throws instead of reaching the store: the "store that throws at the append" path.</summary>
    internal bool ThrowOnRevisionAppend { get; set; }

    /// <summary>While this answers true, a forward read of the log fails: the "log that cannot be read under the hold" path.</summary>
    internal Func<bool>? FailReadsWhile { get; set; }

    /// <summary>How many revision appends have parked since <see cref="ParkRevisionAppends"/>.</summary>
    internal int Parked
    {
        get { lock (_gate) return _parked; }
    }

    /// <summary>From now, every revision append parks until <see cref="Release"/>.</summary>
    internal void ParkRevisionAppends()
    {
        lock (_gate)
        {
            _armed = true;
            _once = false;
            _kind = "revision";
            _parked = 0;
            _release = NewRelease();
        }
    }

    /// <summary>From now, the next append of a post of <paramref name="kind"/> parks until <see cref="Release"/>; the ones after it do not (RVO53).</summary>
    internal void ParkNextAppendOf(string kind)
    {
        lock (_gate)
        {
            _armed = true;
            _once = true;
            _kind = kind;
            _parked = 0;
            _release = NewRelease();
        }
    }

    /// <summary>Lets every parked append go on, and stops parking.</summary>
    internal void Release()
    {
        TaskCompletionSource release;
        lock (_gate)
        {
            _armed = false;
            release = _release;
        }

        release.TrySetResult();
    }

    /// <summary>Waits until <paramref name="count"/> appends have parked; false when they have not within <paramref name="timeout"/>.</summary>
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

    /// <summary>Back to a store that parks, throws and fails nothing; releases anything parked.</summary>
    internal void Reset()
    {
        ThrowOnRevisionAppend = false;
        FailReadsWhile = null;
        Release();
    }

    private static bool IsAppendOf(IReadOnlyList<DomainEvent> events, string kind) =>
        events.Count == 1
        && string.Equals(events[0].Type.Value, "post.accepted", StringComparison.Ordinal)
        && events[0].Payload is JsonValue.Object payload
        && payload.Members.Any(m => m.Key == "kind" && m.Value is JsonValue.String s && string.Equals(s.Value, kind, StringComparison.Ordinal));

    private async Task ParkIfRevisionAsync(IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken)
    {
        if (ThrowOnRevisionAppend && IsAppendOf(events, "revision")) throw new InvalidOperationException("the test store failed this revision's append");

        Task wait;
        lock (_gate)
        {
            if (!_armed || !IsAppendOf(events, _kind)) return;
            if (_once) _armed = false;
            _parked++;
            wait = _release.Task;
        }

        await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        CancellationToken cancellationToken = default)
    {
        await ParkIfRevisionAsync(events, cancellationToken).ConfigureAwait(false);
        return await inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId,
        AggregateVersion expectedVersion,
        IReadOnlyList<DomainEvent> events,
        StreamCondition condition,
        CancellationToken cancellationToken = default)
    {
        await ParkIfRevisionAsync(events, cancellationToken).ConfigureAwait(false);
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
        FailReadsWhile?.Invoke() == true
            ? Task.FromResult(Result<IReadOnlyList<AppendedEvent>>.Fail(new Error("curia/test/read-failed", "the test store failed this read")))
            : inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
}

/// <summary>
/// PR 1's seam on the revision gate (errata G45, probes RVO6, RVO7, RVO8), the shape of <see cref="ObservingFlagRaiserGate"/>: a decorator over the
/// host's real <see cref="IRevisionGate"/> that records every digest a request brought to the gate (<see cref="Entered"/>), how many holds are open
/// now (<see cref="HeldCount"/>), and, at a hold's first release, how many revision events the log holds for the digest (<see cref="Releases"/>),
/// counted from the table and not through the host. <see cref="FailEntries"/> answers every entry with the gate-unavailable refusal.
///
/// <para>The route does not call the gate yet (Task 0 seam), so <see cref="Entered"/> stays empty and every fact that reads it is red by assertion.</para>
/// </summary>
internal sealed class ObservingRevisionGate(IRevisionGate inner, Func<string, Task<int>>? revisionEventsOf = null) : IRevisionGate
{
    private readonly Func<string, Task<int>>? _revisionEventsOf = revisionEventsOf;
    private int _held;
    private int _entered;
    private int _released;

    /// <summary>The count recorded when the observer could not read the table.</summary>
    internal const int ReadFailed = -1;

    /// <summary>The digests that reached the gate, whether or not they entered, in order.</summary>
    internal ConcurrentQueue<string> Entered { get; } = new();

    /// <summary>Per released hold, the digest and the revision events that named it at the first release, in order.</summary>
    internal ConcurrentQueue<(string Digest, int RevisionEvents)> Releases { get; } = new();

    /// <summary>The holds open now.</summary>
    internal int HeldCount => Volatile.Read(ref _held);

    /// <summary>How many entries succeeded.</summary>
    internal int EnteredCount => Volatile.Read(ref _entered);

    /// <summary>How many holds were released (the first dispose of each).</summary>
    internal int ReleasedCount => Volatile.Read(ref _released);

    /// <summary>When true, every entry is refused as the gate's store being unreachable.</summary>
    internal bool FailEntries { get; set; }

    /// <summary>Forgets what was observed and stops failing entries; for a fact to start from nothing on the shared host.</summary>
    internal void Reset()
    {
        FailEntries = false;
        Entered.Clear();
        Releases.Clear();
        Interlocked.Exchange(ref _entered, 0);
        Interlocked.Exchange(ref _released, 0);
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The inner hold is owned by the returned ObservedHold, which disposes it; the caller owns that, as it owns the real gate's hold.")]
    public async Task<Result<IAsyncDisposable>> TryEnterAsync(string prevDigest, CancellationToken cancellationToken)
    {
        Entered.Enqueue(prevDigest);

        if (FailEntries) return Result<IAsyncDisposable>.Fail(IngestErrors.RevisionGateUnavailable());

        var entered = await inner.TryEnterAsync(prevDigest, cancellationToken).ConfigureAwait(false);
        if (!entered.TryGetValue(out var hold, out _)) return entered;

        Interlocked.Increment(ref _held);
        Interlocked.Increment(ref _entered);
        return Result<IAsyncDisposable>.Ok(new ObservedHold(this, prevDigest, hold!));
    }

    private sealed class ObservedHold(ObservingRevisionGate gate, string digest, IAsyncDisposable inner) : IAsyncDisposable
    {
        private int _disposed;

        [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failed observation is recorded as ReadFailed and fails the fact by its own message.")]
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            try
            {
                var events = ReadFailed;
                if (gate._revisionEventsOf is not null)
                {
                    try
                    {
                        events = await gate._revisionEventsOf(digest).ConfigureAwait(false);
                    }
                    catch (Exception)
                    {
                        events = ReadFailed;
                    }
                }

                gate.Releases.Enqueue((digest, events));
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

/// <summary>
/// PR 1's seam on authorization (errata G45, probe RVO12): a decorator over the host's real <see cref="IPolicyDecisionPoint"/> that parks the next
/// decision for a revision's <c>create</c> inside AUTHORIZE until released. The log AUTHORIZE reads is read before the decision, so a request parked here
/// holds a log that goes stale while it waits, which is what RVO12 needs.
/// </summary>
internal sealed class PausingPolicyDecisionPoint(IPolicyDecisionPoint inner) : IPolicyDecisionPoint
{
    private readonly object _gate = new();
    private TaskCompletionSource _release = NewRelease();
    private TaskCompletionSource _parked = NewRelease();
    private bool _armed;

    private static TaskCompletionSource NewRelease() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>The next revision <c>create</c> decision parks; the ones after it do not.</summary>
    internal void ParkNextRevisionDecision()
    {
        lock (_gate)
        {
            _armed = true;
            _release = NewRelease();
            _parked = NewRelease();
        }
    }

    /// <summary>Waits until a decision has parked; false when none has within <paramref name="timeout"/>.</summary>
    internal async Task<bool> WaitForParkedAsync(TimeSpan timeout)
    {
        Task parked;
        lock (_gate) parked = _parked.Task;
        try
        {
            await parked.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Lets the parked decision go on.</summary>
    internal void Release()
    {
        TaskCompletionSource release;
        lock (_gate)
        {
            _armed = false;
            release = _release;
        }

        release.TrySetResult();
    }

    public async ValueTask<Result<AuthorizationDecision>> EvaluateAsync(AuthorizationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        Task? wait = null;
        lock (_gate)
        {
            if (_armed && request.Resource == ResourceKind.Revision && request.Action == ActionKind.Create)
            {
                _armed = false;
                wait = _release.Task;
                _parked.TrySetResult();
            }
        }

        if (wait is not null) await wait.WaitAsync(cancellationToken).ConfigureAwait(false);

        return await inner.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The host for PR 1's route facts (errata G45): the production Forum with its event store wrapped by a <see cref="ContendedEventStore"/> and a
/// <see cref="PausingEventStore"/>, its revision gate by an <see cref="ObservingRevisionGate"/> and its policy decision point by a
/// <see cref="PausingPolicyDecisionPoint"/>. Every decorator forwards to the real object; none fabricates an answer.
/// </summary>
public sealed class RevisionOnceForumFixture : ForumFixture
{
    private ContendedEventStore? _contended;
    private PausingEventStore? _store;
    private ObservingRevisionGate? _gate;
    private PausingPolicyDecisionPoint? _decisions;

    internal ContendedEventStore Contention => _contended ?? throw new InvalidOperationException("the host has not composed its event store yet");

    internal PausingEventStore Store => _store ?? throw new InvalidOperationException("the host has not composed its event store yet");

    internal ObservingRevisionGate Gate => _gate ?? throw new InvalidOperationException("the host has not composed its revision gate yet");

    internal PausingPolicyDecisionPoint Decisions => _decisions ?? throw new InvalidOperationException("the host has not composed its policy decision point yet");

    /// <summary>Everything a fact may have set on the shared host, back to forwarding.</summary>
    internal void ResetSeams()
    {
        // Singletons are built on first resolution: resolve the three the decorators wrap, so a fact can reach them before any request.
        _ = Services.GetRequiredService<IEventStore>();
        _ = Services.GetRequiredService<IRevisionGate>();
        _ = Services.GetRequiredService<IPolicyDecisionPoint>();
        Store.Reset();
        Decisions.Release();
        Gate.Reset();
        Contention.Disarm();
    }

    /// <summary>The revision events the table holds whose signed <c>prev</c> is <paramref name="digest"/>.</summary>
    internal async Task<int> RevisionEventsNamingAsync(string digest)
    {
        await using var admin = new NpgsqlConnection(ConnectionString);
        await admin.OpenAsync().ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM events WHERE event_type = 'post.accepted' AND payload->>'kind' = 'revision' " +
            "AND (payload->>'canonical')::jsonb->>'prev' = @digest;",
            admin);
        command.Parameters.AddWithValue("digest", digest);
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
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
        // Errata G48, ruling 22: these facts' object is the revision gate (one revision of a post in flight, another refused) and the work around it, which the
        // agent gate now serializes in front of it. They run on the host as it was before the agent gate, so each keeps its strength; BRB33, BRB34 and BRB63
        // observe the two gates together through the real one.
        PassThroughAgentWriteGate.Install(services);

        var store = Registered<IEventStore>(services);
        services.Remove(store);
        services.AddSingleton<IEventStore>(sp =>
        {
            _contended = new ContendedEventStore((IEventStore)store.ImplementationFactory!(sp));
            _store = new PausingEventStore(_contended);
            return _store;
        });

        var gate = Registered<IRevisionGate>(services);
        services.Remove(gate);
        services.AddSingleton<IRevisionGate>(sp =>
        {
            _gate = new ObservingRevisionGate((IRevisionGate)gate.ImplementationFactory!(sp), RevisionEventsNamingAsync);
            return _gate;
        });

        var decisions = Registered<IPolicyDecisionPoint>(services);
        services.Remove(decisions);
        services.AddSingleton<IPolicyDecisionPoint>(sp =>
        {
            _decisions = new PausingPolicyDecisionPoint((IPolicyDecisionPoint)decisions.ImplementationFactory!(sp));
            return _decisions;
        });
    }
}
