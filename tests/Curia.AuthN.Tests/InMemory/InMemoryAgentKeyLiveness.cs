using Curia.AuthN.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.AuthN.Tests.InMemory;

/// <summary>
/// The R11.4 in-memory adapter for <see cref="IAgentKeyLiveness"/> (R5.22): answers one status for every
/// key until a test sets another, or sets the error a failed read would answer. It keeps no history, so
/// what a test sets is what the next request reads.
/// </summary>
internal sealed class InMemoryAgentKeyLiveness : IAgentKeyLiveness
{
    public KeyStatus Status { get; set; } = KeyStatus.Active;

    public Error? Failure { get; set; }

    public Task<Result<KeyStatus>> StatusAsync(AgentId agent, Kid kid, CancellationToken cancellationToken = default) =>
        Task.FromResult(Failure is { } failure ? Result<KeyStatus>.Fail(failure) : Result<KeyStatus>.Ok(Status));
}
