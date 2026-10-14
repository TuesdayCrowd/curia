using Curia.Application.Credentials;
using Curia.AuthN.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Api.Adapters;

/// <summary>
/// R5.22's liveness read, offered through the port <c>Curia.AuthN</c> declares: <see cref="LogBoundKeys.StatusAsync"/>,
/// read from the log on every call and cached nowhere. The two ports cannot see each other (CS-7), so this
/// composition root joins them.
/// </summary>
public sealed class LogKeyLiveness(LogBoundKeys keys) : IAgentKeyLiveness
{
    public Task<Result<KeyStatus>> StatusAsync(AgentId agent, Kid kid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(kid);

        return keys.StatusAsync(agent, kid, cancellationToken);
    }
}
