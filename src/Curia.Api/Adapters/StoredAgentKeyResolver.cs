using Curia.Application.Credentials;
using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Api.Adapters;

/// <summary>
/// The token endpoint's key resolver: the store's half of R4.35's rule (<see cref="LogBoundKeys.ResolveStoredAsync"/>),
/// offered through the port <c>Curia.AuthN</c> declares. The two ports have one signature and cannot see each other
/// (CS-7), so this composition root joins them. It reads no log (R5.25, errata G72): the assertion verifies under the
/// key the store holds, and the token endpoint then binds that key by the log (<see cref="LogBoundKeys.RequireBoundAsync"/>)
/// after both <c>jti</c>s are consumed, so the endpoint honours exactly the keys a post's signature is verified under:
/// no fewer, which would lock out an agent whose posts verify, and no more, which is errata G16's finding.
/// </summary>
public sealed class StoredAgentKeyResolver(LogBoundKeys keys) : IAgentKeyResolver
{
    public Task<Result<PublicKeyMaterial>> ResolveAsync(
        AgentId agentId, Kid kid, ServerTimestamp at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(kid);

        return keys.ResolveStoredAsync(agentId, kid, at, cancellationToken);
    }
}
