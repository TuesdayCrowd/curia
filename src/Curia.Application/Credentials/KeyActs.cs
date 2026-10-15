using System.Globalization;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Keys;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Credentials;

/// <summary>
/// What a key act recorded (spec section 4.7): the identity, the key it bound or retired, where in the
/// log the entry sits (R6.47), and for a rotation the key that authorized it.
/// </summary>
/// <param name="AgentId">The identity.</param>
/// <param name="Kid">The key bound (rotation) or retired (retirement).</param>
/// <param name="LogIndex">The entry's index in the log, which the next act's <c>prev</c> names.</param>
/// <param name="AuthorizedBy">The authorizing key of a rotation; null for a retirement.</param>
public sealed record KeyActRecorded(string AgentId, string Kid, long LogIndex, string? AuthorizedBy);

/// <summary>The refusals of a key act (spec section 4.7), each by name. None names a key, a statement or a signature.</summary>
public static class KeyActErrors
{
    public const string LifecycleMovedType = "curia/keys/lifecycle-moved";
    public const string AuthorizingKeyNotLiveType = "curia/keys/authorizing-key-not-live";
    public const string TooManyLiveKeysType = "curia/keys/too-many-live-keys";
    public const string RotationBudgetExhaustedType = "curia/keys/rotation-budget-exhausted";
    public const string RotationCeilingReachedType = "curia/keys/rotation-ceiling-reached";
    public const string NotLiveType = "curia/keys/not-live";
    public const string LastLiveKeyType = "curia/keys/last-live-key";
    public const string UnknownKeyType = "curia/keys/unknown-key";
    public const string NotOwnType = "curia/keys/not-own";
    public const string LifecycleNotEnabledType = "curia/keys/lifecycle-not-enabled";
    public const string BeforeBindingType = "curia/keys/before-binding";
    public const string FromIndexBeyondLogType = "curia/keys/from-index-beyond-log";
    public const string LogKeyNotPublishedType = "curia/keys/log-key-not-published";

    /// <summary>The act's <c>prev</c> is not the identity's latest lifecycle index. The detail is that index, an integer.</summary>
    public static Error LifecycleMoved(long latest) => new(
        LifecycleMovedType,
        "The identity's lifecycle has moved",
        latest.ToString(CultureInfo.InvariantCulture));

    /// <summary>The authorizing key is not live: unbound, unknown to the store, outside its window or retired.</summary>
    public static Error AuthorizingKeyNotLive() => new(AuthorizingKeyNotLiveType, "The authorizing key is not live");

    /// <summary>R7.23: a rotation would make more than <see cref="Curia.Domain.Authorization.TierPolicy.MaxLiveKeys"/> live keys.</summary>
    public static Error TooManyLiveKeys() => new(
        TooManyLiveKeysType,
        "The identity holds the most live keys it may",
        "limit=" + Curia.Domain.Authorization.TierPolicy.MaxLiveKeys.ToString(CultureInfo.InvariantCulture));

    /// <summary>R7.23: the identity made its rotations for the trailing 24 hours.</summary>
    public static Error RotationBudgetExhausted() => new(
        RotationBudgetExhaustedType,
        "The identity has made the most rotations it may in a day",
        "limit=" + Curia.Domain.Authorization.TierPolicy.RotationsPerDay.ToString(CultureInfo.InvariantCulture) + " per 24 hours");

    /// <summary>R7.23: the identity made its lifetime rotations.</summary>
    public static Error RotationCeilingReached() => new(
        RotationCeilingReachedType,
        "The identity has made the most rotations it may",
        "limit=" + Curia.Domain.Authorization.TierPolicy.MaxLifetimeRotations.ToString(CultureInfo.InvariantCulture));

    /// <summary>R4.39: the key a retirement names was already retired, or is outside its window.</summary>
    public static Error NotLive() => new(NotLiveType, "The key is not live");

    /// <summary>R4.39: retiring the key would leave the identity with none.</summary>
    public static Error LastLiveKey() => new(LastLiveKeyType, "The last live key cannot be retired");

    /// <summary>The identity has no such key bound.</summary>
    public static Error UnknownKey() => new(UnknownKeyType, "The identity has no such key");

    /// <summary>Spec section 4.7: the token's subject is not the identity the route names. Names neither.</summary>
    public static Error NotOwn() => new(NotOwnType, "A key act is made by the identity that owns the key");

    /// <summary>R6.26 (rev.): <c>t_c</c> (<c>from_index</c>) is below the log index of the key's binding.</summary>
    public static Error BeforeBinding() => new(BeforeBindingType, "A declaration cannot begin before the key's binding", "field=from_index");

    /// <summary>R6.57: <c>from_index</c> is not below the index the declaration would take.</summary>
    public static Error FromIndexBeyondLog() => new(FromIndexBeyondLogType, "A declaration begins below its own index", "field=from_index");

    /// <summary>R6.26 (rev.), KF2: the signing key's <c>log.key</c> entry is not in the log. Names neither the key nor its kid.</summary>
    public static Error LogKeyNotPublished() => new(LogKeyNotPublishedType, "The log key has not been published", "run sign-head with this key first");

    /// <summary>R4.38 (add.): the Forum does not enable rotation. A fixed detail, naming the kind and nothing about the caller.</summary>
    public static Error RotationNotEnabled() => new(
        LifecycleNotEnabledType, "Key lifecycle acts are not enabled", "key rotation is not enabled on this Forum");

    /// <summary>R4.38 (add.): the Forum does not enable retirement. A fixed detail, naming the kind and nothing about the caller.</summary>
    public static Error RetirementNotEnabled() => new(
        LifecycleNotEnabledType, "Key lifecycle acts are not enabled", "key retirement is not enabled on this Forum");
}

/// <summary>
/// The steps rotation and retirement share (spec section 4.4): the stream read for the version, the log
/// folded for the latest lifecycle index, a signature held to its kind's header and verified, and the
/// entry appended on the version read first.
/// </summary>
internal static class KeyActs
{
    private static readonly Dictionary<string, IContentSigner> NoSigners = new(StringComparer.Ordinal);

    internal sealed record Stream(AggregateId Aggregate, ActorId Actor, AggregateVersion Version);

    internal sealed record LifecycleRead(KeyLifecycle Lifecycle, ActaLog Log, long? LatestIndex);

    /// <summary>The identity's stream and its version (step 4), read before the log is.</summary>
    internal static async Task<Result<Stream>> ReadStreamAsync(IEventReader events, AgentId agent, CancellationToken ct)
    {
        if (!AggregateId.Create(agent.Value).TryGetValue(out var aggregate, out var aggregateError))
            return Result<Stream>.Fail(aggregateError!);
        if (!ActorId.Create(agent.Value).TryGetValue(out var actor, out var actorError))
            return Result<Stream>.Fail(actorError!);

        var read = await events.ReadByAggregateAsync(aggregate, ct).ConfigureAwait(false);
        if (!read.TryGetValue(out var history, out var readError))
            return Result<Stream>.Fail(LogBoundKeys.LogUnreadable(readError!));
        if (!AggregateVersion.From(history!.Count).TryGetValue(out var version, out var versionError))
            return Result<Stream>.Fail(LogBoundKeys.LogUnreadable(versionError!));

        return Result<Stream>.Ok(new Stream(aggregate, actor, version!));
    }

    /// <summary>
    /// The log folded through <see cref="ActaLog"/> (step 5) and the identity's lifecycle read from the
    /// same entries, with the index of its latest entry. A log that will not fold is
    /// <see cref="LogBoundKeys.LogUnreadable"/>; a stream the lifecycle fold refuses passes its own refusal.
    /// </summary>
    internal static async Task<Result<LifecycleRead>> ReadLifecycleAsync(IEventReader events, AgentId agent, CancellationToken ct)
    {
        var all = await events.ReadAllAsync(ct).ConfigureAwait(false);
        if (!all.TryGetValue(out var log, out var readError))
            return Result<LifecycleRead>.Fail(LogBoundKeys.LogUnreadable(readError!));
        if (!ActaLog.Fold(log!).TryGetValue(out var acta, out var foldError))
            return Result<LifecycleRead>.Fail(LogBoundKeys.LogUnreadable(foldError!));
        if (!KeyLifecycle.Of(log!, agent).TryGetValue(out var lifecycle, out var lifecycleError))
            return Result<LifecycleRead>.Fail(lifecycleError!);

        if (lifecycle!.Entries.IsEmpty) return Result<LifecycleRead>.Ok(new LifecycleRead(lifecycle, acta!, null));
        if (acta!.IndexOf(lifecycle.Entries[^1].EventId) is not { } latest)
            return Result<LifecycleRead>.Fail(LogBoundKeys.LogUnreadable(KeyStatementErrors.Mismatch("entry")));

        return Result<LifecycleRead>.Ok(new LifecycleRead(lifecycle, acta, latest));
    }

    /// <summary>The canonical bytes of a statement already read as its own canonical form.</summary>
    internal static Result<CanonicalBytes> CanonicalOf(string statement)
    {
        if (!JsonReader.Parse(System.Text.Encoding.UTF8.GetBytes(statement), AdmitLimits.Default).TryGetValue(out var root, out _))
            return Result<CanonicalBytes>.Fail(KeyStatementErrors.Unreadable("statement"));

        return CanonicalJson.CanonicalizeWithNfc(root!);
    }

    /// <summary>
    /// A signature held to its kind (KF1) and its role (KF2), then verified under <paramref name="key"/>.
    /// Any failure is <see cref="KeyStatementErrors.SignatureInvalid"/> naming <paramref name="role"/>.
    /// </summary>
    internal static Error? VerifyRole(
        IReadOnlyDictionary<string, IContentVerifier> verifiers,
        string headerTyp,
        string role,
        DetachedSignature signature,
        string headerKid,
        CanonicalBytes canonical,
        PublicKeyMaterial key)
    {
        var jws = new JwsSignature(signature.Value);
        if (!KeyStatements.CheckHeader(jws, role, headerTyp, headerKid).IsOk)
            return KeyStatementErrors.SignatureInvalid(role);

        return new DetachedJws(NoSigners, verifiers, headerTyp).Verify(canonical, jws, key).IsOk
            ? null
            : KeyStatementErrors.SignatureInvalid(role);
    }

    /// <summary>Whether any lifecycle entry of any identity in <paramref name="log"/> names <paramref name="kid"/>.</summary>
    internal static bool AnyEntryNames(IReadOnlyList<AppendedEvent> log, string kid)
    {
        foreach (var appended in log)
        {
            var type = appended.Event.Type.Value;
            if (!string.Equals(type, AgentStandingProjector.EnrolledType, StringComparison.Ordinal)
                && !string.Equals(type, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal)
                && !string.Equals(type, LifecycleAppendGuard.KeyRetiredType, StringComparison.Ordinal))
                continue;

            if (appended.Event.Payload is JsonValue.Object payload
                && payload.Members.Any(m => string.Equals(m.Key, AgentStandingProjector.KeyIdField, StringComparison.Ordinal)
                    && m.Value is JsonValue.String text
                    && string.Equals(text.Value, kid, StringComparison.Ordinal)))
                return true;
        }

        return false;
    }

    /// <summary>A new event under a fresh ULID.</summary>
    internal static Result<DomainEvent> NewEvent(UlidGenerator ids, string type, ActorId actor, JsonValue.Object payload)
    {
        if (!ids.Next().TryGetValue(out var ulid, out var idError)) return Result<DomainEvent>.Fail(idError!);
        if (!EventId.Create(ulid.ToString()).TryGetValue(out var eventId, out var eventIdError)) return Result<DomainEvent>.Fail(eventIdError!);
        if (!EventType.Create(type).TryGetValue(out var eventType, out var typeError)) return Result<DomainEvent>.Fail(typeError!);

        return Result<DomainEvent>.Ok(new DomainEvent(eventId, eventType, actor, payload));
    }

    /// <summary>
    /// Appends <paramref name="entry"/> on the version step 4 read (step 9). A concurrency conflict is the
    /// lifecycle moving (step 10): it answers <see cref="KeyActErrors.LifecycleMoved"/> with the latest index
    /// the log now holds. On success, the entry's index in the log.
    /// </summary>
    internal static async Task<Result<long>> AppendAsync(IEventStore events, AgentId agent, Stream stream, DomainEvent entry, CancellationToken ct)
    {
        var appended = await events.AppendAsync(stream.Aggregate, stream.Version, [entry], ct).ConfigureAwait(false);
        if (!appended.TryGetValue(out _, out var appendError))
        {
            if (!string.Equals(appendError!.Type, DomainErrors.ConcurrencyConflictType, StringComparison.Ordinal))
                return Result<long>.Fail(appendError);

            var moved = await ReadLifecycleAsync(events, agent, ct).ConfigureAwait(false);
            if (!moved.TryGetValue(out var now, out var movedError)) return Result<long>.Fail(movedError!);
            return Result<long>.Fail(now!.LatestIndex is { } latest ? KeyActErrors.LifecycleMoved(latest) : appendError);
        }

        var recorded = await ReadLifecycleAsync(events, agent, ct).ConfigureAwait(false);
        if (!recorded.TryGetValue(out var after, out var recordedError)) return Result<long>.Fail(recordedError!);
        return after!.Log.IndexOf(entry.Id.Value) is { } index
            ? Result<long>.Ok(index)
            : Result<long>.Fail(LogBoundKeys.LogUnreadable(KeyStatementErrors.Mismatch("entry")));
    }

    /// <summary>The refusal a key that does not resolve for an act becomes, or the resolver's own refusal when the log is the problem.</summary>
    internal static bool IsNotLive(Error error) =>
        error.Type is AuthorKeyErrors.RetiredType
            or AuthorKeyErrors.CompromisedType
            or AuthorKeyErrors.NotBoundByTheLogType
            or "curia/keys/not-registered-to-agent"
            or "curia/keys/not-yet-valid"
            or "curia/keys/no-longer-valid";
}
