using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Ports;

/// <summary>
/// R7.27 (errata G48): one agent's posts and acceptances are counted and recorded one at a time. Held from before the Forum reads the log for the
/// count until the act has committed or the request has been refused; a second entry for the same agent while one is held waits, in the Forum's memory
/// and then at the database, for at most the published wait, and is refused past it or past the published queue.
/// </summary>
public interface IAgentWriteGate
{
    /// <summary>
    /// Ok(hold) once the agent's turn comes; Fail(<c>BudgetErrors.AgentInFlight()</c>) past the wait or the queue; Fail(<c>BudgetErrors.GateUnavailable()</c>)
    /// when the gate's store cannot be reached. Disposing the hold releases it, and disposing it twice is harmless.
    /// </summary>
    Task<Result<IAsyncDisposable>> EnterAsync(AgentId agent, CancellationToken cancellationToken);
}
