using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Credentials;

/// <summary>A key the store holds and the log binds, with the binding (R4.35).</summary>
public sealed record BoundKey(RegisteredKey Key, KeyBinding Binding);

/// <summary>
/// One identity's keys as the Forum publishes them (R4.35): the keys the store holds that the log
/// binds, and how many the store holds at all, so a caller can still tell an identity the store has
/// never heard of from one whose every row the log refuses.
/// </summary>
public sealed record AgentKeySet(int Stored, IReadOnlyList<BoundKey> Bound);

/// <summary>
/// R4.35 (errata G16): the Forum honours a key the Registrar's store holds only when the event log
/// binds it to the same identity -- an <c>agent.key-bound</c> entry carrying exactly that key, or,
/// for an identity enrolled before R4.34, an <c>agent.enrolled</c> naming its <c>kid</c> -- for a
/// post's author (R6.2), for a token's client (R5.20), and in the key set it serves (R4.16 rev.).
///
/// <para><b>Why a rule over the store and not a column in it.</b> The store can hold rows the log
/// never bound: keys added through errata G14's hole before it closed, and bytes a lost row's recovery
/// accepted under a bound <c>kid</c>. Both are rows R4.19 forbids deleting and R4.32 forbids changing,
/// so the only place they can stop counting is where a key is read. The log is append-only under
/// R11.6's grant and committed to by signed heads, so a key it binds is a key any reader can find
/// bound there too (R6.54).</para>
///
/// <para><b>The store is asked first.</b> It refuses a <c>kid</c> registered to another agent, a key
/// outside its window at the instant asked (R6.31), and text no row can hold, before this reads the
/// log at all; what it answers is then held to the log's binding. So every refusal the store gave
/// before R4.35 is unchanged, byte for byte, and R5.20's refusal still does not say whose a
/// <c>kid</c> is.</para>
/// </summary>
public sealed class LogBoundKeys : IAuthorKeyResolver
{
    private readonly IAuthorKeyResolver _store;
    private readonly IAuthorKeyRegistry _registry;
    private readonly IEventReader _events;

    public LogBoundKeys(IAuthorKeyResolver store, IAuthorKeyRegistry registry, IEventReader events)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(events);

        _store = store;
        _registry = registry;
        _events = events;
    }

    /// <inheritdoc/>
    public async Task<Result<PublicKeyMaterial>> ResolveAsync(
        string agentId, string kid, ServerTimestamp at, CancellationToken cancellationToken = default)
    {
        var resolved = await _store.ResolveAsync(agentId, kid, at, cancellationToken).ConfigureAwait(false);
        if (!resolved.TryGetValue(out var key, out _)) return resolved;

        var binding = await BindingAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!binding.TryGetValue(out var found, out var readError))
            return Result<PublicKeyMaterial>.Fail(readError!);

        return found?.For(kid) is { } bound && bound.Holds(key!)
            ? resolved
            : Result<PublicKeyMaterial>.Fail(AuthorKeyErrors.NotBoundByTheLog(agentId, kid));
    }

    /// <summary>The keys the store holds for <paramref name="agentId"/> that the log binds to it, each with its binding.</summary>
    public async Task<Result<AgentKeySet>> KeySetAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var held = await _registry.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (held.Count == 0) return Result<AgentKeySet>.Ok(new AgentKeySet(0, []));

        var binding = await BindingAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!binding.TryGetValue(out var found, out var readError))
            return Result<AgentKeySet>.Fail(readError!);

        var bound = new List<BoundKey>();
        foreach (var registered in held)
        {
            if (found?.For(registered.Key.Kid) is { } keyBinding && keyBinding.Holds(registered.Key))
                bound.Add(new BoundKey(registered, keyBinding));
        }

        return Result<AgentKeySet>.Ok(new AgentKeySet(held.Count, bound));
    }

    /// <summary>What the log binds to <paramref name="agentId"/>, or <see langword="null"/> when it records no enrollment of it.</summary>
    private async Task<Result<EnrollmentBinding?>> BindingAsync(string agentId, CancellationToken cancellationToken)
    {
        if (!AggregateId.Create(agentId).TryGetValue(out var aggregate, out _))
            return Result<EnrollmentBinding?>.Ok(null);

        var read = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
        return read.Map(history => EnrollmentBinding.Find(history, agentId));
    }
}
