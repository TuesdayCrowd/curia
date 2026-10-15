using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Credentials;

/// <summary>
/// R4.18 (rev.): an identity binds a new key by a rotation a live key of its own authorizes and the new
/// key countersigns (spec sections 4.4 to 4.7). Each step is a named refusal and nothing is written
/// before step 9, so a refused rotation leaves the store and the log as they were.
/// <list type="number">
/// <item>Read the statement: from its exact bytes, its <c>typ</c> first (KF4), its canonical form, its closed member set, and KF5.</item>
/// <item>Its <c>agent_id</c> is the route's agent.</item>
/// <item>Its <c>kid</c> is a <see cref="Kid"/> that holds no control or separator character (R4.37), and its <c>jwk</c> renders exactly (spec section 4.5).</item>
/// <item>Read the identity's stream, for its version. The version is read before the log, so an entry that joins between is seen at step 5 or by step 9's check, never bound over.</item>
/// <item>Fold the log for the identity's latest lifecycle index, which must equal <c>prev</c>.</item>
/// <item>The authorizing key is live.</item>
/// <item>The authorization verifies under the authorizing key and the possession under the new one, each under the rotation's header <c>typ</c> and its role's <c>kid</c> (KF1, KF2).</item>
/// <item>The <c>kid</c> is not taken, then R7.23's live-key cap, daily budget and lifetime ceiling.</item>
/// <item>Register the key (never overwriting, R4.32), then append <c>agent.key-bound</c> at the version read.</item>
/// <item>A concurrency conflict is the lifecycle moving, and answers with the latest index.</item>
/// </list>
/// </summary>
public sealed class RotateKey
{
    private readonly IEventStore _events;
    private readonly IAuthorKeyRegistry _registry;
    private readonly LogBoundKeys _keys;
    private readonly IReadOnlyDictionary<string, IContentVerifier> _verifiers;
    private readonly TimeProvider _clock;
    private readonly UlidGenerator _ids;

    public RotateKey(
        IEventStore events,
        IAuthorKeyRegistry registry,
        LogBoundKeys keys,
        IReadOnlyDictionary<string, IContentVerifier> verifiers,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(verifiers);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _registry = registry;
        _keys = keys;
        _verifiers = verifiers;
        _clock = clock;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>Binds the key <paramref name="statement"/> names, or reports the first step that refuses.</summary>
    public async Task<Result<KeyActRecorded>> RotateAsync(
        AgentId agent,
        StatementText statement,
        DetachedSignature authorization,
        DetachedSignature possession,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(possession);

        // 1. Read the statement.
        if (!KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(statement.Value)).TryGetValue(out var rotation, out var readError))
            return Result<KeyActRecorded>.Fail(readError!);

        // 2. It is the route's agent's.
        if (!string.Equals(rotation!.AgentId, agent.Value, StringComparison.Ordinal))
            return Result<KeyActRecorded>.Fail(KeyStatementErrors.Mismatch("agent_id"));

        // 3. The new kid, as enrollment holds one (R11.34, R4.37), and its key as its jwk renders it.
        if (!Kid.Parse(rotation.Kid, "kid").TryGetValue(out _, out var kidError))
            return Result<KeyActRecorded>.Fail(kidError!);
        if (IdentifierRules.ControlCharacter(rotation.Kid, "kid") is { } controlError)
            return Result<KeyActRecorded>.Fail(controlError);
        if (!KeyStatements.MaterialOf(rotation).TryGetValue(out var newKey, out var materialError))
            return Result<KeyActRecorded>.Fail(materialError!);
        if (!Kid.Parse(rotation.AuthorizedBy, "authorized_by").TryGetValue(out var authorizedBy, out var authorizedByError))
            return Result<KeyActRecorded>.Fail(authorizedByError!);

        // 4. The stream's version, before the log is read.
        if (!(await KeyActs.ReadStreamAsync(_events, agent, ct).ConfigureAwait(false)).TryGetValue(out var stream, out var streamError))
            return Result<KeyActRecorded>.Fail(streamError!);

        // 5. The latest lifecycle index, which prev must equal.
        if (!(await KeyActs.ReadLifecycleAsync(_events, agent, ct).ConfigureAwait(false)).TryGetValue(out var read, out var lifecycleError))
            return Result<KeyActRecorded>.Fail(lifecycleError!);
        if (read!.LatestIndex is not { } latest)
            return Result<KeyActRecorded>.Fail(KeyActErrors.AuthorizingKeyNotLive());
        if (rotation.Prev != latest)
            return Result<KeyActRecorded>.Fail(KeyActErrors.LifecycleMoved(latest));

        // 6. The authorizing key is live.
        var now = _clock.GetUtcNow();
        var resolved = await _keys.ResolveLiveAsync(agent, authorizedBy!, ServerTimestamp.At(now), ct).ConfigureAwait(false);
        if (!resolved.TryGetValue(out var live, out var liveError))
            return Result<KeyActRecorded>.Fail(KeyActs.IsNotLive(liveError!) ? KeyActErrors.AuthorizingKeyNotLive() : liveError!);

        // 7. Both signatures, the authorization first.
        if (!KeyActs.CanonicalOf(statement.Value).TryGetValue(out var canonical, out var canonicalError))
            return Result<KeyActRecorded>.Fail(canonicalError!);
        if (KeyActs.VerifyRole(_verifiers, KeyStatements.RotationHeaderTyp, "authorization", authorization, rotation.AuthorizedBy, canonical, live!.Key) is { } authorizationError)
            return Result<KeyActRecorded>.Fail(authorizationError);
        if (KeyActs.VerifyRole(_verifiers, KeyStatements.RotationHeaderTyp, "possession", possession, rotation.Kid, canonical, newKey!) is { } possessionError)
            return Result<KeyActRecorded>.Fail(possessionError);

        // 8. The kid is not taken; then R7.23's cap, budget and ceiling.
        var all = read.Log.Events;
        var held = (await _registry.KeysForAsync(agent, ct).ConfigureAwait(false))
            .FirstOrDefault(k => string.Equals(k.Key.Kid, rotation.Kid, StringComparison.Ordinal));
        var orphan = held is not null && KeyEnrollment.SameMaterial(held.Key, newKey!);
        if (KeyActs.AnyEntryNames(all, rotation.Kid) || (held is not null && !orphan))
            return Result<KeyActRecorded>.Fail(AuthorKeyErrors.KidTaken(rotation.Kid));
        if (read.Lifecycle.LiveCount() >= TierPolicy.MaxLiveKeys)
            return Result<KeyActRecorded>.Fail(KeyActErrors.TooManyLiveKeys());
        if (read.Lifecycle.RotationsSince(now.AddHours(-24)) >= TierPolicy.RotationsPerDay)
            return Result<KeyActRecorded>.Fail(KeyActErrors.RotationBudgetExhausted());
        if (read.Lifecycle.Entries.Count(e => e.IsRotation) >= TierPolicy.MaxLifetimeRotations)
            return Result<KeyActRecorded>.Fail(KeyActErrors.RotationCeilingReached());

        // 9. Register the key, then bind it. A row an earlier attempt left is reused (R4.32 forbids replacing it).
        if (!(await _registry.AddAsync(agent, newKey!, now, ct).ConfigureAwait(false)).TryGetValue(out _, out var addError))
            return Result<KeyActRecorded>.Fail(addError!);

        var payload = new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent.Value)),
            new("authorization", new JsonValue.String(authorization.Value)),
            new("authorized_by", new JsonValue.String(rotation.AuthorizedBy)),
            new("jwk", rotation.Jwk),
            new("kid", new JsonValue.String(rotation.Kid)),
            new("possession", new JsonValue.String(possession.Value)),
            new("prev", new JsonValue.Number(rotation.Prev)),
            new("statement", new JsonValue.String(statement.Value)),
        ]);
        if (!KeyActs.NewEvent(_ids, AgentStandingProjector.KeyBoundType, stream!.Actor, payload).TryGetValue(out var entry, out var entryError))
            return Result<KeyActRecorded>.Fail(entryError!);

        // 10. A conflict is the lifecycle moving; the store row stays for the retry.
        var index = await KeyActs.AppendAsync(_events, agent, stream, entry!, ct).ConfigureAwait(false);
        return index.Map(i => new KeyActRecorded(agent.Value, rotation.Kid, i, rotation.AuthorizedBy));
    }
}
