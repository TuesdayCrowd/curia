using Curia.Domain.Primitives;

namespace Curia.AuthN.Ports;

/// <summary>
/// R5.14/R5.15/R5.17: one <c>jti</c> replay cache, shared across every resource-server instance,
/// for both client assertions and DPoP proofs. R5.17 is the load-bearing word in that sentence --
/// "atomic" -- so this port has exactly one operation, and it is the atomic one: there is no
/// <c>Contains</c> plus a separate <c>Insert</c> for a caller to accidentally compose into the
/// check-then-insert race R5.17 calls out by name. A conforming adapter over Redis is <c>SET NX
/// PX</c> in a single round trip; the in-memory adapter under test uses
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}.TryAdd"/> for the
/// same reason.
/// </summary>
public interface IReplayCache
{
    /// <summary>
    /// Atomically records <paramref name="jti"/> as seen. Returns <see langword="true"/> when this
    /// call is the one that performed the insertion (first use -- accept); <see langword="false"/>
    /// when <paramref name="jti"/> was already present (replay -- reject). <paramref
    /// name="expiresAt"/> bounds retention; R5.14 requires at least the artifact's own maximum
    /// lifetime plus the maximum permitted skew, computed by the caller (see
    /// <see cref="AccessTokenValidator"/>/<see cref="ClientAssertionValidator"/> for what each
    /// artifact type uses), not by this port.
    /// </summary>
    Task<Result<bool>> TryInsertAsync(string jti, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
}
