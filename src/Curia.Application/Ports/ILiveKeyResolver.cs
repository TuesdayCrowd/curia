using Curia.Application.Credentials;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Ports;

/// <summary>
/// The key an act by <paramref name="agentId"/> may be verified against now, with the version of the
/// author's stream the decision was made on (R6.56). Ingest asks this rather than
/// <see cref="IAuthorKeyResolver"/>, whose answer carries no position: an append conditioned on
/// <see cref="LiveKey.AuthorStream"/> lands only while no entry has joined the stream since the
/// decision, so a post cannot follow its key's retirement into the log.
/// </summary>
public interface ILiveKeyResolver
{
    /// <summary>The key, live at <paramref name="at"/>, and the stream version it was read at; or the refusal.</summary>
    Task<Result<LiveKey>> ResolveLiveAsync(
        AgentId agentId,
        Kid kid,
        ServerTimestamp at,
        CancellationToken cancellationToken = default);
}
