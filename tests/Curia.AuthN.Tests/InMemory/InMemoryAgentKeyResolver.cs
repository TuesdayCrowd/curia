using Curia.AuthN.Ports;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Tests.InMemory;

/// <summary>
/// The R11.4 in-memory adapter for <see cref="IAgentKeyResolver"/>: a fixed <c>kid</c> to
/// (<see cref="PublicKeyMaterial"/>, validity window) map, standing in for one agent's
/// Forum-served key set (<see cref="ClientAssertionValidator"/>) -- see
/// <see cref="ClientAssertionValidationContext.AgentKeyResolver"/>'s remarks. The issuer-JWKS
/// half, which has no validity window, has its own adapter, <see cref="InMemoryJwsKeyResolver"/>.
///
/// Deliberately does not reuse <c>Curia.Domain.AgentKeySet</c>'s window logic: this project does
/// not, and must not, reference <c>Curia.Domain</c> (CS-5, mirroring <c>Curia.AuthN</c>'s own
/// layering). The <c>[validFrom, validUntil)</c> half-open interval below is re-expressed here,
/// against <see cref="ServerTimestamp"/>, for exactly the reason <c>Curia.Domain.Keys.KeyValidityWindow</c>
/// exists in the first place -- see that type's remarks -- not copied from it.
/// </summary>
internal sealed class InMemoryAgentKeyResolver : IAgentKeyResolver
{
    /// <summary>One key's material and the half-open <c>[ValidFrom, ValidUntil)</c> window R6.31
    /// evaluates against -- <see cref="ValidFrom"/> and <see cref="ValidUntil"/> are
    /// <see langword="null"/> only via the "always valid" constructor overload below, standing in
    /// for a scenario that does not care about the validity window at all.</summary>
    private sealed record Entry(PublicKeyMaterial Key, ServerTimestamp? ValidFrom, ServerTimestamp? ValidUntil)
    {
        public bool Contains(ServerTimestamp at) =>
            (ValidFrom is not { } from || at >= from) && (ValidUntil is not { } until || at < until);
    }

    private readonly IReadOnlyDictionary<string, Entry> _entriesByKid;

    /// <summary>A single key, valid at every instant -- the common case for scenarios that are not
    /// specifically exercising R6.31/A12's validity window.</summary>
    public InMemoryAgentKeyResolver(string kid, PublicKeyMaterial key)
        : this(new Dictionary<string, Entry>(StringComparer.Ordinal) { [kid] = new Entry(key, null, null) })
    {
    }

    /// <summary>A single key with an explicit <c>[validFrom, validUntil)</c> window -- for tests
    /// proving R6.31/A12 through the validator: a key valid when it signed something and no longer
    /// valid at the <see cref="ServerTimestamp"/> the resolver is asked about.</summary>
    public InMemoryAgentKeyResolver(string kid, PublicKeyMaterial key, ServerTimestamp validFrom, ServerTimestamp? validUntil)
        : this(new Dictionary<string, Entry>(StringComparer.Ordinal) { [kid] = new Entry(key, validFrom, validUntil) })
    {
    }

    private InMemoryAgentKeyResolver(IReadOnlyDictionary<string, Entry> entriesByKid) => _entriesByKid = entriesByKid;

    public Task<Result<PublicKeyMaterial>> ResolveAsync(
        string kid, ServerTimestamp at, CancellationToken cancellationToken = default)
    {
        if (!_entriesByKid.TryGetValue(kid, out var entry))
            return Task.FromResult(Result<PublicKeyMaterial>.Fail(AuthNErrors.KidNotFound(kid)));

        return Task.FromResult(entry.Contains(at)
            ? Result<PublicKeyMaterial>.Ok(entry.Key)
            : Result<PublicKeyMaterial>.Fail(AuthNErrors.KeyNotValidAtServerTimestamp(kid, at)));
    }
}
