using System.Collections.Concurrent;
using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Api.Adapters;

/// <summary>
/// A key registered to an agent, with the window it is valid over.
/// </summary>
/// <param name="NotBefore">Registration. A signature made earlier does not verify.</param>
/// <param name="NotAfter">
/// Revocation or expiry, exclusive. Null means still valid. R6.31 evaluates against
/// <c>server_ts</c>, so a key revoked today still verifies a post the Forum received last week --
/// which is the whole point of evaluating at the receipt instant rather than at "now".
/// </param>
public sealed record RegisteredKey(
    PublicKeyMaterial Key,
    DateTimeOffset NotBefore,
    DateTimeOffset? NotAfter);

/// <summary>
/// The Registrar's key store, in memory.
///
/// <para><b>R4.16 rev. (errata A16):</b> "the Registrar's key store is authoritative and the Forum
/// serves JWKS; no runtime fetch of agent-hosted JWKS." This adapter therefore never reaches the
/// network -- there is no HTTP client here and no place to put one. The SSRF and availability
/// surface that removal was about cannot reappear by accident.</para>
///
/// <para>In memory because Phase 1's Registrar is not built yet. The port is what the pipeline
/// depends on, so replacing this with a Postgres-backed adapter is a composition-root edit and
/// nothing else. Deliberately not silently persistent: an operator restarting the Forum and
/// finding enrollments gone is a better failure than one who believes a store is durable when it
/// is not.</para>
/// </summary>
public sealed class InMemoryAuthorKeyResolver : IAuthorKeyResolver
{
    private readonly ConcurrentDictionary<(string Agent, string Kid), RegisteredKey> _keys = new();

    /// <summary>Registers a key to an agent, valid from <paramref name="notBefore"/>.</summary>
    public void Register(string agentId, PublicKeyMaterial key, DateTimeOffset notBefore, DateTimeOffset? notAfter = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);

        _keys[(agentId, key.Kid)] = new RegisteredKey(key, notBefore, notAfter);
    }

    /// <summary>Every registered (agent, kid), for the JWKS the Forum serves rather than fetches.</summary>
    public IReadOnlyCollection<(string Agent, string Kid)> Registered => _keys.Keys.ToArray();

    public Task<Result<PublicKeyMaterial>> ResolveAsync(
        string agentId,
        string kid,
        ServerTimestamp at,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_keys.TryGetValue((agentId, kid), out var registered))
            return Task.FromResult(Result<PublicKeyMaterial>.Fail(KeyErrors.NotRegisteredToAgent(agentId, kid)));

        // R6.31 / errata A12: validity is evaluated at server_ts, not at "now".
        if (at.Value < registered.NotBefore)
            return Task.FromResult(Result<PublicKeyMaterial>.Fail(KeyErrors.NotYetValid(kid, at)));

        if (registered.NotAfter is { } notAfter && at.Value >= notAfter)
            return Task.FromResult(Result<PublicKeyMaterial>.Fail(KeyErrors.NoLongerValid(kid, at)));

        return Task.FromResult(Result<PublicKeyMaterial>.Ok(registered.Key));
    }
}

/// <summary>
/// Three distinct reasons a key does not resolve. Distinct because they mean different things to
/// an operator: a kid that is not the agent's is a possible impersonation attempt; a key outside
/// its window is ordinary lifecycle. Collapsing them would make the first invisible inside the
/// second's noise.
/// </summary>
public static class KeyErrors
{
    public static Error NotRegisteredToAgent(string agentId, string kid) => new(
        "curia/keys/not-registered-to-agent",
        "No key with that identifier is registered to that agent",
        $"agent={agentId} kid={kid}");

    public static Error NotYetValid(string kid, ServerTimestamp at) => new(
        "curia/keys/not-yet-valid",
        "The key was not yet valid at the receipt instant",
        $"kid={kid} server_ts={at}");

    public static Error NoLongerValid(string kid, ServerTimestamp at) => new(
        "curia/keys/no-longer-valid",
        "The key was no longer valid at the receipt instant",
        $"kid={kid} server_ts={at}");
}
