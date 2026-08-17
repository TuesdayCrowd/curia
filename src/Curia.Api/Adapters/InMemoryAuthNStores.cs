using System.Collections.Concurrent;
using Curia.AuthN.Ports;
using Curia.Domain.Primitives;

namespace Curia.Api.Adapters;

/// <summary>
/// R5.x's replay cache, in memory.
///
/// <para><b>What "in memory" costs here, stated rather than left implicit.</b> A replay cache is a
/// security control: it is what stops a captured client assertion or DPoP proof from being used
/// twice. Held in one process, it protects only that process -- a second instance behind a load
/// balancer would accept a replay the first rejected, and a restart forgets everything. So this is
/// correct for a single-instance prototype and is a real vulnerability the moment there are two.
/// A shared store (Redis, or the Postgres already present) is the deployment answer, and the port
/// is what makes that a composition-root change.</para>
///
/// <para>Entries are pruned on insert rather than by a timer: a timer would be a second thing to
/// get right, and the cache is only ever read on the path that also writes it.</para>
/// </summary>
public sealed class InMemoryReplayCache(TimeProvider clock) : IReplayCache
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public Task<Result<bool>> TryInsertAsync(
        string jti, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var now = clock.GetUtcNow();
        foreach (var entry in _seen)
            if (entry.Value <= now) _seen.TryRemove(entry.Key, out _);

        // True means "this jti had not been seen", i.e. not a replay. TryAdd returning false is the
        // replay, and it is the only outcome that matters to the caller.
        return Task.FromResult(Result<bool>.Ok(_seen.TryAdd(jti, expiresAt)));
    }
}

/// <summary>
/// R5.x's DPoP nonce store, in memory, rotating on the published interval.
///
/// <para>The nonce is what lets the server choose when a proof was made, rather than trusting the
/// client's clock. <see cref="Curia.AuthN.AuthNConstants.MaxDpopNonceRotationInterval"/> is the
/// ceiling, taken from there rather than restated, so the rotation cannot drift from the figure the
/// specification fixes.</para>
///
/// <para>Both the current nonce and the immediately previous one are accepted. A single-nonce store
/// rejects every request in flight at the instant of rotation, which reads to a client as a random
/// failure and to an operator as a rotation bug; accepting the previous one bounds that to the
/// rotation interval instead of to zero.</para>
/// </summary>
public sealed class InMemoryDpopNonceStore(TimeProvider clock) : IDpopNonceStore
{
    private readonly object _gate = new();
    private DpopNonce? _current;
    private DpopNonce? _previous;

    public Task<Result<DpopNonce>> IssueAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Result<DpopNonce>.Ok(Current()));
    }

    public Task<Result<bool>> IsCurrentAsync(string nonce, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var current = Current();
        lock (_gate)
        {
            var accepted =
                string.Equals(nonce, current.Value, StringComparison.Ordinal)
                || (_previous is { } previous && string.Equals(nonce, previous.Value, StringComparison.Ordinal));

            return Task.FromResult(Result<bool>.Ok(accepted));
        }
    }

    private DpopNonce Current()
    {
        var now = clock.GetUtcNow();

        lock (_gate)
        {
            if (_current is { } existing && existing.ExpiresAt > now) return existing;

            _previous = _current;
            _current = new DpopNonce(
                Guid.NewGuid().ToString("N"),
                now + Curia.AuthN.AuthNConstants.MaxDpopNonceRotationInterval);

            return _current;
        }
    }
}

/// <summary>
/// The issuer's own signing key, as an <see cref="IJwsKeyResolver"/> for the resource server.
///
/// <para>Deliberately resolves exactly one <c>kid</c> and fails on every other. An issuer key
/// resolver that accepted any key it was shown would let anything that could sign a JWT mint
/// Forum credentials, which is the algorithm-confusion family of failure in a different costume.</para>
/// </summary>
public sealed class IssuerKeyResolver(Curia.Canon.Jws.PublicKeyMaterial key) : IJwsKeyResolver
{
    public Task<Result<Curia.Canon.Jws.PublicKeyMaterial>> ResolveAsync(
        string kid, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(string.Equals(kid, key.Kid, StringComparison.Ordinal)
            ? Result<Curia.Canon.Jws.PublicKeyMaterial>.Ok(key)
            : Result<Curia.Canon.Jws.PublicKeyMaterial>.Fail(KeyErrors.NotRegisteredToAgent("issuer", kid)));
    }
}

/// <summary>
/// The agent key store, adapted to AuthN's own <see cref="IAgentKeyResolver"/>.
///
/// <para>Two ports, one store. <c>Curia.Application</c> cannot see <c>Curia.AuthN</c> (the
/// architecture test confines it to Domain, Canon and Domain.Primitives), so each declares the
/// capability it needs and the composition root satisfies both from here. This is the adapter that
/// makes that arrangement real rather than a comment on the port.</para>
/// </summary>
public sealed class AgentKeyResolverAdapter(InMemoryAuthorKeyResolver keys) : IAgentKeyResolver
{
    public Task<Result<Curia.Canon.Jws.PublicKeyMaterial>> ResolveAsync(
        string kid, ServerTimestamp at, CancellationToken cancellationToken = default)
    {
        // AuthN asks by kid alone -- a client assertion names its key, not its owner, and the
        // subject is established by which key verified rather than by a claim. So this searches
        // across agents, which is sound for exactly that reason: a kid resolving to some agent's
        // key still only authenticates whoever holds the matching private key.
        foreach (var (agent, registeredKid) in keys.Registered)
        {
            if (!string.Equals(registeredKid, kid, StringComparison.Ordinal)) continue;
            return keys.ResolveAsync(agent, kid, at, cancellationToken);
        }

        return Task.FromResult(
            Result<Curia.Canon.Jws.PublicKeyMaterial>.Fail(KeyErrors.NotRegisteredToAgent("(any)", kid)));
    }
}
