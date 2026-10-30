using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Budget;

/// <summary>
/// R7.27's in-memory half (errata G48): one agent's writes in the order they arrived, one at a time, bounded in time and in length, idle entries dropped,
/// no static state. The queue is one object per host, so a second host over the same database has its own, and the Postgres leg (the advisory lock)
/// is what holds an agent to one write across hosts.
///
/// <para>An entry is the agent's holder and at most <c>waiters</c> requests behind it (the holder is not counted). A request that finds the holder's
/// place taken and the line full is refused at once; one that waits the whole <c>wait</c>, counted from its arrival on the clock's timers
/// (<c>new CancellationTokenSource(wait, clock)</c>), is refused too and leaves the line, freeing its place; one whose caller cancels
/// leaves the line and is never handed the turn. Hand-off is direct: a release gives the turn to the head of the line without ever clearing it.</para>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is a queue: R7.27's first-come-first-served wait.")]
public sealed class AgentWriteQueue
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _wait;
    private readonly int _waiters;
    private readonly object _gate = new();
    private readonly Dictionary<string, Line> _lines = new(StringComparer.Ordinal);

    public AgentWriteQueue(TimeProvider clock, TimeSpan wait, int waiters)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(wait, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfNegative(waiters);
        _clock = clock;
        _wait = wait;
        _waiters = waiters;
    }

    /// <summary>The agents the queue holds an entry for now (a holder or a waiter); zero when it is idle.</summary>
    public int TrackedKeys
    {
        get
        {
            lock (_gate) return _lines.Count;
        }
    }

    /// <summary>The wait and the queue length this queue was built with.</summary>
    internal (TimeProvider Clock, TimeSpan Wait, int Waiters) Bounds => (_clock, _wait, _waiters);

    /// <summary>Ok(hold) once the agent's turn comes; Fail(<see cref="BudgetErrors.AgentInFlight"/>) past the wait or the line. Throws on the caller's cancellation.</summary>
    public async Task<Result<IAsyncDisposable>> EnterAsync(AgentId agent, CancellationToken cancellationToken)
    {
        var turn = await TakeTurnAsync(agent, cancellationToken).ConfigureAwait(false);
        return turn.Match(
            held => Result<IAsyncDisposable>.Ok(held),
            error => Result<IAsyncDisposable>.Fail(error));
    }

    /// <summary>As <see cref="EnterAsync"/>, and the hold says how much of the wait is left, for a leg that must finish inside the same deadline.</summary>
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The turn is the result: the caller owns it and disposes it.")]
    public async Task<Result<AgentWriteTurn>> TakeTurnAsync(AgentId agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        cancellationToken.ThrowIfCancellationRequested();

        var arrived = _clock.GetTimestamp();
        Waiter waiter;
        lock (_gate)
        {
            if (!_lines.TryGetValue(agent.Value, out var line))
            {
                _lines[agent.Value] = new Line();
                return Result<AgentWriteTurn>.Ok(new AgentWriteTurn(this, agent.Value, arrived));
            }

            if (line.Waiting.Count >= _waiters) return Result<AgentWriteTurn>.Fail(BudgetErrors.AgentInFlight());

            waiter = new Waiter();
            waiter.Node = line.Waiting.AddLast(waiter);
        }

        using var timeout = new CancellationTokenSource(_wait, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancellationToken);
        using var registration = linked.Token.Register(() => Leave(agent.Value, waiter, cancellationToken));

        var outcome = await waiter.Completion.Task.ConfigureAwait(false);
        switch (outcome)
        {
            case Outcome.Granted:
                return Result<AgentWriteTurn>.Ok(new AgentWriteTurn(this, agent.Value, arrived));
            case Outcome.TimedOut:
                return Result<AgentWriteTurn>.Fail(BudgetErrors.AgentInFlight());
            default:
                throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>How much of the wait is left for a request that arrived at <paramref name="arrived"/> (a timestamp of the queue's clock); never negative.</summary>
    internal TimeSpan RemainingSince(long arrived)
    {
        var left = _wait - _clock.GetElapsedTime(arrived);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>The waiter's token fired: it leaves the line if it is still in it, and says whether the wait ran out or its caller cancelled.</summary>
    private void Leave(string key, Waiter waiter, CancellationToken caller)
    {
        lock (_gate)
        {
            if (!_lines.TryGetValue(key, out var line) || waiter.Node is null || waiter.Node.List is null) return;

            line.Waiting.Remove(waiter.Node);
            waiter.Node = null;
        }

        waiter.Completion.TrySetResult(caller.IsCancellationRequested ? Outcome.Cancelled : Outcome.TimedOut);
    }

    /// <summary>The holder is done: the head of the line takes the turn, or the entry is dropped if no one is waiting.</summary>
    internal void Release(string key)
    {
        Waiter? next = null;
        lock (_gate)
        {
            if (!_lines.TryGetValue(key, out var line)) return;

            var head = line.Waiting.First;
            if (head is null)
            {
                _lines.Remove(key);
                return;
            }

            line.Waiting.RemoveFirst();
            next = head.Value;
            next.Node = null;
        }

        next.Completion.TrySetResult(Outcome.Granted);
    }

    private enum Outcome
    {
        Granted,
        TimedOut,
        Cancelled,
    }

    private sealed class Line
    {
        internal LinkedList<Waiter> Waiting { get; } = new();
    }

    private sealed class Waiter
    {
        internal TaskCompletionSource<Outcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal LinkedListNode<Waiter>? Node { get; set; }
    }
}

/// <summary>One agent's turn in an <see cref="AgentWriteQueue"/>: disposing it gives the turn to the next in line. Disposing twice is harmless.</summary>
public sealed class AgentWriteTurn : IAsyncDisposable
{
    private readonly AgentWriteQueue _queue;
    private readonly string _key;
    private readonly long _arrived;
    private int _released;

    internal AgentWriteTurn(AgentWriteQueue queue, string key, long arrived)
    {
        _queue = queue;
        _key = key;
        _arrived = arrived;
    }

    /// <summary>How much of the published wait is left, counted from the request's arrival; zero once it has run out.</summary>
    public TimeSpan Remaining => _queue.RemainingSince(_arrived);

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 0) _queue.Release(_key);
        return ValueTask.CompletedTask;
    }
}
