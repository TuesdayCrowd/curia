using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.AuthN.Ports;

/// <summary>
/// R5.22: what the log says of one key of one identity now, read for every authenticated request. A
/// token names the key its assertion was signed with, and this is how the resource server learns that
/// the key is still live. An adapter reads the log on every call and keeps no answer, so a retirement
/// takes effect at the next request. A read that fails is an error and never <see cref="KeyStatus.Active"/>:
/// the caller refuses the request with it.
/// </summary>
public interface IAgentKeyLiveness
{
    Task<Result<KeyStatus>> StatusAsync(AgentId agent, Kid kid, CancellationToken cancellationToken = default);
}
