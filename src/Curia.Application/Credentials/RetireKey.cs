using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Credentials;

/// <summary>
/// R4.39: an identity ends a key by a retirement the key signs itself (spec sections 4.4 to 4.7). It
/// follows <see cref="RotateKey"/>'s order with <see cref="KeyStatements.ReadRetirement"/>: the signature
/// is verified under the key being retired, under <c>curia-key-retirement+jws</c> and with that key's
/// <c>kid</c> in its header, so a sibling key cannot retire another. It refuses
/// <see cref="KeyActErrors.LastLiveKey"/> when the key is the only live one, and it runs no budget check
/// and no lifetime ceiling (R7.23): a thief that spends the rotation budget must not stop the owner
/// retiring the key it stole.
/// </summary>
public sealed class RetireKey
{
    private readonly IEventStore _events;
    private readonly LogBoundKeys _keys;
    private readonly IReadOnlyDictionary<string, IContentVerifier> _verifiers;
    private readonly TimeProvider _clock;
    private readonly UlidGenerator _ids;

    public RetireKey(
        IEventStore events,
        LogBoundKeys keys,
        IReadOnlyDictionary<string, IContentVerifier> verifiers,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(verifiers);
        ArgumentNullException.ThrowIfNull(clock);

        _events = events;
        _keys = keys;
        _verifiers = verifiers;
        _clock = clock;
        _ids = new UlidGenerator(clock);
    }

    /// <summary>Ends <paramref name="kid"/>, or reports the first step that refuses.</summary>
    public async Task<Result<KeyActRecorded>> RetireAsync(
        AgentId agent,
        Kid kid,
        StatementText statement,
        DetachedSignature authorization,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(kid);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(authorization);

        // 1 and 2. The statement, and that it is the route's agent's and the route's key's.
        if (!KeyStatements.ReadRetirement(Encoding.UTF8.GetBytes(statement.Value)).TryGetValue(out var retirement, out var readError))
            return Result<KeyActRecorded>.Fail(readError!);
        if (!string.Equals(retirement!.AgentId, agent.Value, StringComparison.Ordinal))
            return Result<KeyActRecorded>.Fail(KeyStatementErrors.Mismatch("agent_id"));
        if (!string.Equals(retirement.Kid, kid.Value, StringComparison.Ordinal))
            return Result<KeyActRecorded>.Fail(KeyStatementErrors.Mismatch("kid"));

        // 4. The stream's version, before the log is read.
        if (!(await KeyActs.ReadStreamAsync(_events, agent, ct).ConfigureAwait(false)).TryGetValue(out var stream, out var streamError))
            return Result<KeyActRecorded>.Fail(streamError!);

        // 5. The latest lifecycle index, which prev must equal.
        if (!(await KeyActs.ReadLifecycleAsync(_events, agent, ct).ConfigureAwait(false)).TryGetValue(out var read, out var lifecycleError))
            return Result<KeyActRecorded>.Fail(lifecycleError!);
        if (read!.LatestIndex is not { } latest)
            return Result<KeyActRecorded>.Fail(KeyActErrors.UnknownKey());
        if (retirement.Prev != latest)
            return Result<KeyActRecorded>.Fail(KeyActErrors.LifecycleMoved(latest));

        // 6. The key is bound and not yet ended.
        var resolved = await _keys.ResolveLiveAsync(agent, kid, ServerTimestamp.At(_clock.GetUtcNow()), ct).ConfigureAwait(false);
        if (!resolved.TryGetValue(out var live, out var liveError))
        {
            if (liveError!.Type is AuthorKeyErrors.NotBoundByTheLogType or "curia/keys/not-registered-to-agent")
                return Result<KeyActRecorded>.Fail(KeyActErrors.UnknownKey());
            return Result<KeyActRecorded>.Fail(KeyActs.IsNotLive(liveError) ? KeyActErrors.NotLive() : liveError);
        }

        // 7. The key signs its own retirement.
        if (!KeyActs.CanonicalOf(statement.Value).TryGetValue(out var canonical, out var canonicalError))
            return Result<KeyActRecorded>.Fail(canonicalError!);
        if (KeyActs.VerifyRole(_verifiers, KeyStatements.RetirementHeaderTyp, "authorization", authorization, retirement.Kid, canonical, live!.Key) is { } signatureError)
            return Result<KeyActRecorded>.Fail(signatureError);

        // 8. Not the last live key. No budget, no ceiling.
        if (read.Lifecycle.LiveCount() <= 1)
            return Result<KeyActRecorded>.Fail(KeyActErrors.LastLiveKey());

        // 9 and 10. Append at the version read; a conflict is the lifecycle moving.
        var payload = new JsonValue.Object(
        [
            new("agent_id", new JsonValue.String(agent.Value)),
            new("authorization", new JsonValue.String(authorization.Value)),
            new("kid", new JsonValue.String(retirement.Kid)),
            new("prev", new JsonValue.Number(retirement.Prev)),
            new("statement", new JsonValue.String(statement.Value)),
        ]);
        if (!KeyActs.NewEvent(_ids, LifecycleAppendGuard.KeyRetiredType, stream!.Actor, payload).TryGetValue(out var entry, out var entryError))
            return Result<KeyActRecorded>.Fail(entryError!);

        var index = await KeyActs.AppendAsync(_events, agent, stream, entry!, ct).ConfigureAwait(false);
        return index.Map(i => new KeyActRecorded(agent.Value, retirement.Kid, i, null));
    }
}
