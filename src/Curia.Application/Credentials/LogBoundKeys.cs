using Curia.Application.Ports;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Credentials;

/// <summary>
/// A key the store holds and the log binds, with the binding (R4.35) and what the log says of it now
/// (R4.35 rev.): <paramref name="Status"/>, and the entry that ended it when it is retired.
/// </summary>
public sealed record BoundKey(RegisteredKey Key, KeyBinding Binding, KeyStatus Status, string? EndedByEventId);

/// <summary>
/// A key resolved for use now, with the version of its author's stream the decision was made on
/// (R6.56): an append conditioned on <paramref name="AuthorStream"/> lands only while no entry has
/// joined the stream since.
/// </summary>
public sealed record LiveKey(PublicKeyMaterial Key, AggregateVersion AuthorStream);

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
///
/// <para><b>A log that cannot be read refuses nothing.</b> Whether it binds the key is then unknown,
/// so the answer is <see cref="LogUnreadable"/>, a server fault, and never the reader's refusal
/// handed on as if it were the key's: ingest answers it 503 and the token endpoint
/// <c>server_error</c>, where a 401 would tell an agent its key had been refused.</para>
/// </summary>
public sealed class LogBoundKeys : IAuthorKeyResolver, ILiveKeyResolver
{
    /// <summary>The slug of <see cref="LogUnreadable"/>, for callers that match on it.</summary>
    public const string LogUnreadableType = "curia/log/unreadable";

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
        AgentId agentId, Kid kid, ServerTimestamp at, CancellationToken cancellationToken = default) =>
        (await ResolveLiveAsync(agentId, kid, at, cancellationToken).ConfigureAwait(false)).Map(live => live.Key);

    /// <summary>
    /// The key <paramref name="kid"/> of <paramref name="agentId"/> as it stands now, for an act decided
    /// at <paramref name="at"/>: the store's refusals first, unchanged; then the log's binding (R4.35);
    /// then the log's lifecycle (R4.35 rev.), so a key the identity retired fails with
    /// <see cref="AuthorKeyErrors.Retired"/> after the refusals above and before nothing else. One read
    /// of the author's stream yields both the fold and <see cref="LiveKey.AuthorStream"/>; a second read
    /// for the version would be a different instant from the one the decision was made on (R6.56).
    /// </summary>
    public async Task<Result<LiveKey>> ResolveLiveAsync(
        AgentId agentId, Kid kid, ServerTimestamp at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(kid);

        var resolved = await _store.ResolveAsync(agentId, kid, at, cancellationToken).ConfigureAwait(false);
        if (!resolved.TryGetValue(out var key, out var storeError)) return Result<LiveKey>.Fail(storeError!);

        var read = await ReadAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var stream, out var readError))
            return Result<LiveKey>.Fail(readError!);

        if (stream!.Binding?.For(kid) is not { } bound || !bound.Holds(key!))
            return Result<LiveKey>.Fail(AuthorKeyErrors.NotBoundByTheLog(agentId.Value, kid.Value));

        if (!KeyLifecycle.Of(stream.History, agentId).TryGetValue(out var lifecycle, out var foldError))
            return Result<LiveKey>.Fail(foldError!);

        return lifecycle!.StatusNow(kid.Value) == KeyStatus.Active
            ? Result<LiveKey>.Ok(new LiveKey(key!, stream.Version))
            : Result<LiveKey>.Fail(AuthorKeyErrors.Retired(agentId.Value, kid.Value));
    }

    /// <summary>
    /// What the log says of <paramref name="kid"/> of <paramref name="agentId"/> now (R5.22): <see cref="KeyStatus.Unbound"/>
    /// when no entry binds it to the identity, otherwise the lifecycle's answer. Read from the log on every
    /// call; <see cref="LogUnreadable"/> when it cannot be read, which is never an answer about the key.
    /// </summary>
    public async Task<Result<KeyStatus>> StatusAsync(AgentId agentId, Kid kid, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(kid);

        var read = await ReadAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var stream, out var readError))
            return Result<KeyStatus>.Fail(readError!);

        if (stream!.Binding?.For(kid) is null) return Result<KeyStatus>.Ok(KeyStatus.Unbound);

        return KeyLifecycle.Of(stream.History, agentId).TryGetValue(out var lifecycle, out var foldError)
            ? Result<KeyStatus>.Ok(lifecycle!.StatusNow(kid.Value))
            : Result<KeyStatus>.Fail(foldError!);
    }

    /// <summary>The keys the store holds for <paramref name="agentId"/> that the log binds to it, each with its binding.</summary>
    public async Task<Result<AgentKeySet>> KeySetAsync(AgentId agentId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentId);

        var held = await _registry.KeysForAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (held.Count == 0) return Result<AgentKeySet>.Ok(new AgentKeySet(0, []));

        var read = await ReadAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var stream, out var readError))
            return Result<AgentKeySet>.Fail(readError!);

        if (!KeyLifecycle.Of(stream!.History, agentId).TryGetValue(out var lifecycle, out var foldError))
            return Result<AgentKeySet>.Fail(foldError!);

        var bound = new List<BoundKey>();
        foreach (var registered in held)
        {
            if (stream.Binding?.For(registered.Key) is { } keyBinding && keyBinding.Holds(registered.Key))
                bound.Add(new BoundKey(registered, keyBinding, lifecycle!.StatusNow(registered.Key.Kid), lifecycle.EndedBy(registered.Key.Kid)?.EventId));
        }

        return Result<AgentKeySet>.Ok(new AgentKeySet(held.Count, bound));
    }

    /// <summary>
    /// The log could not be read, so whether it binds a key is unknown. Names the reader's refusal by
    /// its slug, never its text.
    /// </summary>
    public static Error LogUnreadable(Error readError)
    {
        ArgumentNullException.ThrowIfNull(readError);

        return new Error(LogUnreadableType, "The event log could not be read", readError.Type);
    }

    /// <summary>One read of an identity's stream: what it holds, what it binds, and the version a decision on it was made at.</summary>
    private sealed record StreamRead(IReadOnlyList<AppendedEvent> History, EnrollmentBinding? Binding, AggregateVersion Version);

    /// <summary>
    /// The identity's stream, read once, with what the log binds to <paramref name="agentId"/> (null when
    /// it records no enrollment of it); <see cref="LogUnreadable"/> when the log cannot be read.
    /// </summary>
    private async Task<Result<StreamRead>> ReadAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        if (!AggregateId.Create(agentId.Value).TryGetValue(out var aggregate, out _))
            return Result<StreamRead>.Ok(new StreamRead([], null, AggregateVersion.New));

        var read = await _events.ReadByAggregateAsync(aggregate, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var history, out var readError))
            return Result<StreamRead>.Fail(LogUnreadable(readError!));

        if (!AggregateVersion.From(history!.Count).TryGetValue(out var version, out var versionError))
            return Result<StreamRead>.Fail(LogUnreadable(versionError!));

        return Result<StreamRead>.Ok(new StreamRead(history, EnrollmentBinding.Find(history, agentId), version!));
    }
}
