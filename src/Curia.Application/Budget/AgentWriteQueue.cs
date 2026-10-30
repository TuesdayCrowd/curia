using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Budget;

/// <summary>
/// R7.27's in-memory half (errata G48): first come first served, bounded in time and in length, idle entries dropped, no static state.
///
/// <para><b>Task 0 seam.</b> This class admits every entry at once and tracks nothing, so the probes that observe a queue are red by assertion and not by
/// compile error. Task 1 replaces the body; the shape (the constructor and the two members) is what the probes are written against.</para>
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "It is a queue: R7.27's first-come-first-served wait.")]
[SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Task 0 seam: Task 1 gives both members instance state.")]
public sealed class AgentWriteQueue
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _wait;
    private readonly int _waiters;

    public AgentWriteQueue(TimeProvider clock, TimeSpan wait, int waiters)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _wait = wait;
        _waiters = waiters;
    }

    /// <summary>The agents the queue holds an entry for now (a holder or a waiter); zero when it is idle.</summary>
    public int TrackedKeys => 0;

    /// <summary>The wait and the queue length this queue was built with.</summary>
    internal (TimeProvider Clock, TimeSpan Wait, int Waiters) Bounds => (_clock, _wait, _waiters);

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The hold is the result: the caller owns it and disposes it.")]
    public Task<Result<IAsyncDisposable>> EnterAsync(AgentId agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return Task.FromResult(Result<IAsyncDisposable>.Ok(new NullHold()));
    }

    private sealed class NullHold : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
