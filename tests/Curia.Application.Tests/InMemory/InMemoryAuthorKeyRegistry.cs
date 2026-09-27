using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>
/// R11.4's in-memory <see cref="IAuthorKeyRegistry"/>: one dictionary keyed by <c>kid</c>, the shared
/// <see cref="KeyEnrollment.Decide"/> rule, and a lock that makes deciding and registering one act --
/// the in-process counterpart of the Postgres adapter's per-identifier advisory lock.
/// </summary>
internal sealed class InMemoryAuthorKeyRegistry : IAuthorKeyRegistry
{
    private readonly Dictionary<string, (string AgentId, RegisteredKey Key)> _byKid = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<Result<RegisteredKey>> EnrollAsync(
        string agentId, PublicKeyMaterial key, DateTimeOffset notBefore, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentNullException.ThrowIfNull(key);

        lock (_gate)
        {
            var decided = KeyEnrollment.Decide(agentId, key, HeldBy(agentId));
            if (!decided.TryGetValue(out var existing, out var refusal))
                return Task.FromResult(Result<RegisteredKey>.Fail(refusal!));

            if (existing is not null)
                return Task.FromResult(Result<RegisteredKey>.Ok(existing));

            if (_byKid.ContainsKey(key.Kid))
                return Task.FromResult(Result<RegisteredKey>.Fail(AuthorKeyErrors.KidRegisteredToAnotherAgent(agentId, key.Kid)));

            // A copy of the bytes, as a database row is: a caller that reuses its array afterwards
            // must not be able to change what the store holds.
            var registered = new RegisteredKey(new PublicKeyMaterial(key.Alg, key.Kid, key.Public.ToArray()), notBefore, null);
            _byKid.Add(key.Kid, (agentId, registered));
            return Task.FromResult(Result<RegisteredKey>.Ok(registered));
        }
    }

    public Task<IReadOnlyList<RegisteredKey>> KeysForAsync(string agentId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        lock (_gate)
        {
            IReadOnlyList<RegisteredKey> keys = HeldBy(agentId);
            return Task.FromResult(keys);
        }
    }

    /// <summary>
    /// The Postgres adapter's order: newest window first, then <c>kid</c> -- ordinally here, by the
    /// database's collation there. Nothing depends on that tie-break.
    /// </summary>
    private List<RegisteredKey> HeldBy(string agentId) =>
    [
        .. _byKid.Values
            .Where(row => string.Equals(row.AgentId, agentId, StringComparison.Ordinal))
            .Select(row => row.Key)
            .OrderByDescending(k => k.NotBefore)
            .ThenBy(k => k.Key.Kid, StringComparer.Ordinal),
    ];
}
