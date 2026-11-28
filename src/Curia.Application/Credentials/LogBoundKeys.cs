using Curia.Application.Ports;
using Curia.Application.Reads;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Credentials;

/// <summary>
/// A key the store holds and the log binds, with the binding (R4.35) and what the log says of it now
/// (R4.35 rev.): <paramref name="Status"/>, and the entry that ended it when it is retired.
/// </summary>
/// <param name="CompromisedByEventId">
/// The declaration whose <c>kids</c> name the key (R6.57), a cascaded key as well as the first; null when none does. A
/// reader goes to it for a post the key signed, so the key set names it for every kid the declaration lists.
/// </param>
/// <param name="DisputedFrom">The declaration's <c>from_index</c>; null when no declaration names the key.</param>
public sealed record BoundKey(
    RegisteredKey Key,
    KeyBinding Binding,
    KeyStatus Status,
    string? EndedByEventId,
    string? CompromisedByEventId = null,
    long? DisputedFrom = null);

/// <summary>
/// A key resolved for use now, with the version of its author's stream the decision was made on
/// (R6.56): an append conditioned on <paramref name="AuthorStream"/> lands only while no entry has
/// joined the stream since.
/// </summary>
public sealed record LiveKey(PublicKeyMaterial Key, AggregateVersion AuthorStream);

/// <summary>
/// One identity's keys as the Forum publishes them (R4.35): the keys the store holds that the log
/// binds, and how many the store holds at all, so a caller can still tell an identity the store has
/// never heard of from one whose every row the log refuses. <paramref name="Verified"/> and <paramref name="IndexOf"/> are what the same read says of the identity's lifecycle and of the log
/// index (R6.47) of any row it holds (errata G87): the key set is published from one read, never from a second.
/// </summary>
public sealed record AgentKeySet(
    int Stored,
    IReadOnlyList<BoundKey> Bound,
    VerifiedLifecycle? Verified = null,
    Func<string, long?>? IndexOf = null);

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

    /// <summary>The title of <see cref="LogUnreadable"/> and of the Api's own 503 for the same fault: written once here.</summary>
    public const string LogUnreadableTitle = "The event log could not be read";

    private readonly IAuthorKeyResolver _store;
    private readonly IAuthorKeyRegistry _registry;
    private readonly IPositionedEventReader _events;
    private readonly LifecycleVerifier _verifier;

    public LogBoundKeys(IAuthorKeyResolver store, IAuthorKeyRegistry registry, IPositionedEventReader events, LifecycleVerifier verifier)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(verifier);

        _store = store;
        _registry = registry;
        _events = events;
        _verifier = verifier;
    }

    /// <inheritdoc/>
    public async Task<Result<PublicKeyMaterial>> ResolveAsync(
        AgentId agentId, Kid kid, ServerTimestamp at, CancellationToken cancellationToken = default) =>
        (await ResolveLiveAsync(agentId, kid, at, cancellationToken).ConfigureAwait(false)).Map(live => live.Key);

    /// <summary>
    /// The key <paramref name="kid"/> of <paramref name="agentId"/> as it stands now, for an act decided
    /// at <paramref name="at"/>: the store's refusals first, unchanged (<see cref="ResolveStoredAsync"/>); then the
    /// log's binding (R4.35) and the log's lifecycle (R4.35 rev.) (<see cref="RequireBoundAsync"/>), so a key the identity
    /// retired fails with <see cref="AuthorKeyErrors.Retired"/> after the refusals above and before nothing else. One read
    /// of the author's stream yields both the fold and <see cref="LiveKey.AuthorStream"/>; a second read
    /// for the version would be a different instant from the one the decision was made on (R6.56).
    /// </summary>
    public async Task<Result<LiveKey>> ResolveLiveAsync(
        AgentId agentId, Kid kid, ServerTimestamp at, CancellationToken cancellationToken = default)
    {
        var resolved = await ResolveStoredAsync(agentId, kid, at, cancellationToken).ConfigureAwait(false);
        if (!resolved.TryGetValue(out var key, out var storeError)) return Result<LiveKey>.Fail(storeError!);

        return await RequireBoundAsync(agentId, kid, key!, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The store's lookup and nothing else (R5.25): the key the Registrar's key store holds for <paramref name="agentId"/> and <paramref name="kid"/> at <paramref name="at"/>, or the store's refusal.
    /// It makes <b>no read of the log</b>, so what it returns is a key the store holds and not yet a key the log binds: <see cref="RequireBoundAsync"/> is the other half.
    /// </summary>
    public Task<Result<PublicKeyMaterial>> ResolveStoredAsync(
        AgentId agentId, Kid kid, ServerTimestamp at, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(kid);

        return _store.ResolveAsync(agentId, kid, at, cancellationToken);
    }

    /// <summary>
    /// The other half of <see cref="ResolveLiveAsync"/> (R5.25): one read of the whole log, then whether it binds exactly <paramref name="key"/>'s bytes to <paramref name="agentId"/> under
    /// <paramref name="kid"/> (R4.35) and whether the key is live (R4.35 rev.): <see cref="AuthorKeyErrors.NotBoundByTheLog"/>, <see cref="AuthorKeyErrors.Compromised"/>, <see cref="AuthorKeyErrors.Retired"/>,
    /// or <see cref="LogUnreadable"/> when the log cannot be read.
    /// </summary>
    public async Task<Result<LiveKey>> RequireBoundAsync(
        AgentId agentId, Kid kid, PublicKeyMaterial key, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(agentId);
        ArgumentNullException.ThrowIfNull(kid);
        ArgumentNullException.ThrowIfNull(key);

        var read = await ReadAsync(agentId, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var stream, out var readError))
            return Result<LiveKey>.Fail(readError!);

        return Decide(stream!.Verified, stream.Version, agentId, kid, key);
    }

    /// <summary>
    /// <see cref="RequireBoundAsync"/> decided on a read the caller already holds (errata G87, R5.28): the token endpoint makes one read set for a request whose assertion verified, and the key's
    /// binding, the credential's status and the agent's standing are all decided from it. It reads nothing.
    /// </summary>
    public static Result<LiveKey> RequireBound(AgentRead read, Kid kid, PublicKeyMaterial key)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(kid);
        ArgumentNullException.ThrowIfNull(key);

        if (!AggregateId.Create(read.Agent.Value).TryGetValue(out _, out _))
            return Decide(read.Lifecycle, AggregateVersion.New, read.Agent, kid, key);

        if (!read.StreamVersion.TryGetValue(out var version, out var versionError))
            return Result<LiveKey>.Fail(LogUnreadable(versionError!));

        return Decide(read.Lifecycle, version, read.Agent, kid, key);
    }

    private static Result<LiveKey> Decide(VerifiedLifecycle verified, AggregateVersion version, AgentId agentId, Kid kid, PublicKeyMaterial key)
    {
        if (verified.Binding?.For(kid) is not { } bound || !bound.Holds(key))
            return Result<LiveKey>.Fail(AuthorKeyErrors.NotBoundByTheLog(agentId.Value, kid.Value));

        return verified.Honoured.StatusNow(kid.Value) switch
        {
            KeyStatus.Active => Result<LiveKey>.Ok(new LiveKey(key, version)),
            KeyStatus.Compromised => Result<LiveKey>.Fail(AuthorKeyErrors.Compromised(agentId.Value, kid.Value)),
            KeyStatus.Retired or KeyStatus.Unbound => Result<LiveKey>.Fail(AuthorKeyErrors.Retired(agentId.Value, kid.Value)),
            _ => throw new InvalidOperationException("Not a key status"),
        };
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

        if (stream!.Verified.Binding?.For(kid) is null) return Result<KeyStatus>.Ok(KeyStatus.Unbound);

        return Result<KeyStatus>.Ok(stream.Verified.Honoured.StatusNow(kid.Value));
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

        var lifecycle = stream!.Verified.Honoured;
        var bound = new List<BoundKey>();
        foreach (var registered in held)
        {
            if (stream.Verified.Binding?.For(registered.Key) is { } keyBinding && keyBinding.Holds(registered.Key))
            {
                var declaration = lifecycle.CompromisedBy(registered.Key.Kid);
                bound.Add(new BoundKey(
                    registered,
                    keyBinding,
                    lifecycle.StatusNow(registered.Key.Kid),
                    lifecycle.EndedBy(registered.Key.Kid)?.EventId,
                    declaration?.EventId,
                    declaration?.FromIndex));
            }
        }

        return Result<AgentKeySet>.Ok(new AgentKeySet(held.Count, bound, stream.Verified, stream.IndexOf));
    }

    /// <summary>
    /// The log could not be read, so whether it binds a key is unknown. Names the reader's refusal by
    /// its slug, never its text.
    /// </summary>
    public static Error LogUnreadable(Error readError)
    {
        ArgumentNullException.ThrowIfNull(readError);

        return new Error(LogUnreadableType, LogUnreadableTitle, readError.Type);
    }

    /// <summary>One read of an identity's stream: what it holds, what it binds, and the version a decision on it was made at.</summary>
    private sealed record StreamRead(VerifiedLifecycle Verified, AggregateVersion Version, Func<string, long?> IndexOf);

    /// <summary>
    /// The identity's stream and the published keys, read once in one snapshot (errata G87, R5.28, R11.113), with what the log binds to <paramref name="agentId"/> (null when
    /// it records no enrollment of it); <see cref="LogUnreadable"/> when the log cannot be read. A statement's <c>prev</c> and a declaration's <c>from_index</c> are indices in the log (R6.47, R6.64),
    /// which every row of the read carries; the stream's version is its own events' count.
    /// </summary>
    private async Task<Result<StreamRead>> ReadAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        AgentRead read;
        try
        {
            read = await AgentRead.ReadAsync(_events, _verifier, agentId, AgentReadOptions.Stream, cancellationToken).ConfigureAwait(false);
        }
        catch (LogUnreadableException e) when (e.Refusal is { } refused)
        {
            // A reader that answered with a refusal (not a fault of a store, which propagates to the host's middleware and is logged there): the log could not be read, and that is said.
            return Result<StreamRead>.Fail(LogUnreadable(refused));
        }

        if (!AggregateId.Create(agentId.Value).TryGetValue(out _, out _))
            return Result<StreamRead>.Ok(new StreamRead(read.Lifecycle, AggregateVersion.New, read.Verified.PositionOf));

        if (!read.StreamVersion.TryGetValue(out var version, out var versionError))
            return Result<StreamRead>.Fail(LogUnreadable(versionError!));

        return Result<StreamRead>.Ok(new StreamRead(read.Lifecycle, version, read.Verified.PositionOf));
    }
}
