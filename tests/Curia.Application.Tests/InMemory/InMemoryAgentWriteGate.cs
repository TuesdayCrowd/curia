using Curia.Application.Budget;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Tests.InMemory;

/// <summary>
/// R11.4's in-memory <see cref="IAgentWriteGate"/> (R7.27; errata G48): the queue alone, so the rule is written once in <see cref="AgentWriteQueue"/> and
/// the Postgres adapter is the same queue followed by the lock. <see cref="StoreUnreachable"/> stands in for the lock leg's store failing (BRB10).
///
/// <para>Task 0 seam: the queue admits every entry at once, so this adapter is a pass-through until Task 1 gives the queue its body.</para>
/// </summary>
internal sealed class InMemoryAgentWriteGate(TimeProvider clock, TimeSpan wait, int waiters) : IAgentWriteGate
{
    private readonly AgentWriteQueue _queue = new(clock, wait, waiters);

    /// <summary>When true, every entry is refused as the store being unreachable (the lock leg failing).</summary>
    public bool StoreUnreachable { get; set; }

    /// <summary>The agents the queue holds an entry for now; zero when it is idle.</summary>
    public int TrackedKeys => _queue.TrackedKeys;

    public Task<Result<IAsyncDisposable>> EnterAsync(AgentId agent, CancellationToken cancellationToken) =>
        _queue.EnterAsync(agent, cancellationToken);
}
