using Curia.Application.Credentials;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Api.Adapters;

/// <summary>
/// The token endpoint's key resolver: R4.35's rule (<see cref="LogBoundKeys"/>), offered through the
/// port <c>Curia.AuthN</c> declares. The two ports have one signature and cannot see each other
/// (CS-7), so this composition root joins them, and the token endpoint honours exactly the keys a
/// post's signature is verified under: no fewer, which would lock out an agent whose posts verify,
/// and no more, which is errata G16's finding.
/// </summary>
public sealed class LogBoundAgentKeyResolver(LogBoundKeys keys) : IAgentKeyResolver
{
    public Task<Result<PublicKeyMaterial>> ResolveAsync(
        string agentId, string kid, ServerTimestamp at, CancellationToken cancellationToken = default) =>
        keys.ResolveAsync(agentId, kid, at, cancellationToken);
}
