using System.Collections.Immutable;
using System.Globalization;
using Curia.Application.Ports;
using Curia.Application.Reads;
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
    public const string KeyAlreadyBoundType = "curia/keys/key-already-bound";
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

    /// <summary>Errata G67, R6.62 (it replaces <c>curia/keys/log-key-not-published</c>): the signing key is no declaration key the log has published.</summary>
    public const string DeclarationKeyNotPublishedType = "curia/keys/declaration-key-not-published";

    /// <summary>Errata G67, R6.62: the key is also a log key, which no declaration key may be.</summary>
    public const string DeclarationKeyIsLogKeyType = "curia/keys/declaration-key-is-log-key";

    /// <summary>Errata G67, R6.62: a declaration key is already published, and the first publication wins.</summary>
    public const string DeclarationKeyAlreadyPublishedType = "curia/keys/declaration-key-already-published";

    /// <summary>R6.66 (errata G70): the operator key is also a published log key, by kid or by public key. A key is published in the class it was first published in.</summary>
    public const string OperatorKeyIsLogKeyType = "curia/keys/operator-key-is-log-key";

    /// <summary>R6.66 (errata G70): the operator key is also a published declaration key, by kid or by public key.</summary>
    public const string OperatorKeyIsDeclarationKeyType = "curia/keys/operator-key-is-declaration-key";

    /// <summary>R6.66 (errata G70): an operator key is already published, and the first publication wins.</summary>
    public const string OperatorKeyAlreadyPublishedType = "curia/keys/operator-key-already-published";

    /// <summary>R6.66 (errata G70): the declaration key is also a published operator key, by kid or by public key.</summary>
    public const string DeclarationKeyIsOperatorKeyType = "curia/keys/declaration-key-is-operator-key";

    /// <summary>Amendment 7, ruling 81: the signing key is not the first declaration key the log published, and only the first declares (R6.62).</summary>
    public const string DeclarationKeyNotFirstType = "curia/keys/declaration-key-not-first";

    /// <summary>
    /// Amendment 7, ruling 81 (F-H2): the row was appended and the Forum skips it. The writer says so and never reports success for it. A fault of the Forum (its writer and its fold disagree), so a route
    /// answers it 500 and states no detail (R11.33).
    /// </summary>
    public const string NotHonouredType = "curia/keys/not-honoured";

    /// <summary>R11.97 (errata G70): the operator-acts key the tool would sign with is not the first operator key the log published. Raised by the operator tool's writers, before signing; no route reaches it.</summary>
    public const string OperatorKeyNotPublishedType = "curia/keys/operator-key-not-published";

    /// <summary>R11.97 (errata G70): the log key the tool would publish is a published operator key, by <c>kid</c> or public key. Raised by <c>sign-head</c>, before anything is appended; no route reaches it.</summary>
    public const string LogKeyIsOperatorKeyType = "curia/keys/log-key-is-operator-key";

    /// <summary>The act's <c>prev</c> is not the identity's latest lifecycle index. The detail is that index, an integer.</summary>
    public static Error LifecycleMoved(long latest) => new(
        LifecycleMovedType,
        "The identity's lifecycle has moved",
        latest.ToString(CultureInfo.InvariantCulture));

    /// <summary>The authorizing key is not live: unbound, unknown to the store, outside its window or retired.</summary>
    public static Error AuthorizingKeyNotLive() => new(AuthorizingKeyNotLiveType, "The authorizing key is not live");

    /// <summary>R4.45: the key a rotation names is one this identity's honoured prefix has bound, live or not. Names no kid and no key.</summary>
    public static Error KeyAlreadyBound() => new(
        KeyAlreadyBoundType,
        "That key is already bound to this identity",
        "A key this identity has bound before, live or not, cannot be bound again under another key identifier; make a new key.");

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

    /// <summary>R6.62: the signing key's <c>log.declaration-key</c> entry is not in the log. Names neither the key nor its kid.</summary>
    public static Error DeclarationKeyNotPublished() => new(
        DeclarationKeyNotPublishedType, "The declaration key has not been published", "run publish-declaration-key with this key first");

    /// <summary>R6.62: the key is also a log key. Names neither the key nor its kid.</summary>
    public static Error DeclarationKeyIsLogKey() => new(
        DeclarationKeyIsLogKeyType, "A declaration key is not a log key", "the key's kid is published as a log key");

    /// <summary>R6.62: a declaration key is already published and the first publication wins. Names neither key nor kid.</summary>
    public static Error DeclarationKeyAlreadyPublished() => new(
        DeclarationKeyAlreadyPublishedType, "A declaration key is already published", "the first declaration key published is the one");

    /// <summary>R6.66: the key is also a published log key, which no operator key may be. Names neither the key nor its kid.</summary>
    public static Error OperatorKeyIsLogKey() => new(
        OperatorKeyIsLogKeyType, "An operator key is not a log key", "the key is published as a log key");

    /// <summary>R6.66: the key is also a published declaration key, which no operator key may be. Names neither the key nor its kid.</summary>
    public static Error OperatorKeyIsDeclarationKey() => new(
        OperatorKeyIsDeclarationKeyType, "An operator key is not a declaration key", "the key is published as a declaration key");

    /// <summary>R6.66: an operator key is already published and the first publication wins. Names neither key nor kid.</summary>
    public static Error OperatorKeyAlreadyPublished() => new(
        OperatorKeyAlreadyPublishedType, "An operator key is already published", "the first operator key published is the one");

    /// <summary>R6.66: the key is also a published operator key, which no declaration key may be. Names neither the key nor its kid.</summary>
    public static Error DeclarationKeyIsOperatorKey() => new(
        DeclarationKeyIsOperatorKeyType, "A declaration key is not an operator key", "the key is published as an operator key");

    /// <summary>R11.97: the signing key is not the first operator key the log published, so a row signed with it would be skipped. Names neither key nor kid.</summary>
    public static Error OperatorKeyNotPublished() => new(
        OperatorKeyNotPublishedType, "The operator-acts key has not been published", "run publish-operator-key with this key first; only the first operator key published signs");

    /// <summary>R11.97: the key is a published operator key, which no log key may be. Names neither key nor kid.</summary>
    public static Error LogKeyIsOperatorKey() => new(
        LogKeyIsOperatorKeyType, "A log key is not an operator key", "the key is published as an operator key");

    /// <summary>R6.62: the signing key is a declaration key but not the first the log published. Names neither key nor kid.</summary>
    public static Error DeclarationKeyNotFirst() => new(
        DeclarationKeyNotFirstType, "A declaration key is not the first published", "only the first declaration key published declares");

    /// <summary>
    /// Ruling 81: the entry was appended at <paramref name="index"/> and the Forum skips it, by <paramref name="predicate"/> (R6.64). It is in the log, and ends, binds and marks no key. The detail names
    /// the index and the predicate and never a payload member.
    /// </summary>
    public static Error NotHonoured(long index, string predicate) => new(
        NotHonouredType,
        "The Forum does not honour the row it recorded",
        $"the row was appended at log index {index.ToString(CultureInfo.InvariantCulture)} and the Forum skips it ({predicate}); it ends, binds and marks no key")
    {
        LogIndex = index,
    };

    /// <summary>R4.38 (add.): the Forum does not enable rotation. A fixed detail, naming the kind and nothing about the caller.</summary>
    public static Error RotationNotEnabled() => new(
        LifecycleNotEnabledType, "Key lifecycle acts are not enabled", "key rotation is not enabled on this Forum");

    /// <summary>R4.38 (add.): the Forum does not enable retirement. A fixed detail, naming the kind and nothing about the caller.</summary>
    public static Error RetirementNotEnabled() => new(
        LifecycleNotEnabledType, "Key lifecycle acts are not enabled", "key retirement is not enabled on this Forum");
}

/// <summary>
/// The steps rotation and retirement share (spec section 4.4): the whole log read once for the stream's version
/// and the latest honoured lifecycle index, a signature held to its kind's header and verified, and the
/// entry appended on the version that read gave.
/// </summary>
internal static class KeyActs
{
    private static readonly Dictionary<string, IContentSigner> NoSigners = new(StringComparer.Ordinal);

    internal sealed record Stream(AggregateId Aggregate, ActorId Actor, AggregateVersion Version);

    /// <summary>
    /// What one read says of an identity (errata G87, R5.28): its honoured lifecycle, the log index of any row the read holds, the index of the latest honoured entry, and the stream (the version a conditioned
    /// append is made on). The version is counted from the same snapshot the decision is drawn from, so there is no second instant (R6.56).
    /// </summary>
    internal sealed record LifecycleRead(
        KeyLifecycle Lifecycle,
        Func<string, long?> IndexOf,
        long? LatestIndex,
        Stream Stream,
        ImmutableArray<SkippedRow> Skipped,
        EnrollmentBinding? Binding,
        Func<string, bool> AnyEntryNamesKid);

    /// <summary><see cref="LifecycleRead"/> and the log folded as an Acta, for the operator's declaration, which reads the whole log by design (R12.11: it names the next index and the key lists).</summary>
    internal sealed record WholeLifecycleRead(LifecycleRead Read, ActaLog Log);

    /// <summary>
    /// The identity's stream and the three published-key types, read once in one snapshot (errata G87, R5.28, R11.113: a statement's <c>prev</c> and a declaration's <c>from_index</c> are indices in the
    /// log, which every row of the read carries), and the identity's honoured lifecycle decided from them, with the index of its latest honoured entry and the stream's version (step 4).
    /// <paramref name="kidToCheck"/>, when a rotation names a new kid, adds the one clause that finds any identity's entry naming it. A row that does not verify is skipped, and moves nothing; a log that
    /// cannot be read is <see cref="LogBoundKeys.LogUnreadable"/>.
    /// </summary>
    internal static async Task<Result<LifecycleRead>> ReadLifecycleAsync(IPositionedEventReader events, LifecycleVerifier verifier, AgentId agent, CancellationToken ct, string? kidToCheck = null)
    {
        if (!AggregateId.Create(agent.Value).TryGetValue(out var aggregate, out var aggregateError))
            return Result<LifecycleRead>.Fail(aggregateError!);
        if (!ActorId.Create(agent.Value).TryGetValue(out var actor, out var actorError))
            return Result<LifecycleRead>.Fail(actorError!);

        AgentRead read;
        try
        {
            read = await AgentRead.ReadAsync(
                events,
                verifier,
                agent,
                kidToCheck is null ? AgentReadOptions.Stream : new AgentReadOptions(Posture: false, Extra: [ReadClause.LifecycleNaming(kidToCheck)]),
                ct).ConfigureAwait(false);
        }
        catch (LogUnreadableException e) when (e.Refusal is { } refused)
        {
            return Result<LifecycleRead>.Fail(LogBoundKeys.LogUnreadable(refused));
        }

        if (!read.StreamVersion.TryGetValue(out var version, out var versionError))
            return Result<LifecycleRead>.Fail(LogBoundKeys.LogUnreadable(versionError!));

        var stream = new Stream(aggregate!, actor!, version);
        var verified = read.Lifecycle;
        var rows = read.Slice.Rows.Select(r => r.Event).ToArray();
        long? IndexOf(string eventId) => read.Verified.PositionOf(eventId);
        bool Names(string kid) => AnyEntryNames(rows, kid);

        if (verified.Honoured.Entries.IsEmpty) return Result<LifecycleRead>.Ok(new LifecycleRead(verified.Honoured, IndexOf, null, stream, verified.Skipped, verified.Binding, Names));
        if (IndexOf(verified.Honoured.Entries[^1].EventId) is not { } latest)
            return Result<LifecycleRead>.Fail(LogBoundKeys.LogUnreadable(KeyStatementErrors.Mismatch("entry")));

        return Result<LifecycleRead>.Ok(new LifecycleRead(verified.Honoured, IndexOf, latest, stream, verified.Skipped, verified.Binding, Names));
    }

    /// <summary>
    /// The log read once and whole (errata G68, ruling 36), folded through <see cref="ActaLog"/>, with the identity's honoured lifecycle read from the same entries: the operator's declaration, whose
    /// <c>from_index</c> is bounded by the log's size and whose key lists are the log's (R12.11). A route does not call it; a fence says so.
    /// </summary>
    internal static async Task<Result<WholeLifecycleRead>> ReadWholeLifecycleAsync(IEventReader events, LifecycleVerifier verifier, AgentId agent, CancellationToken ct)
    {
        if (!AggregateId.Create(agent.Value).TryGetValue(out var aggregate, out var aggregateError))
            return Result<WholeLifecycleRead>.Fail(aggregateError!);
        if (!ActorId.Create(agent.Value).TryGetValue(out var actor, out var actorError))
            return Result<WholeLifecycleRead>.Fail(actorError!);

        var all = await events.ReadAllAsync(ct).ConfigureAwait(false);
        if (!all.TryGetValue(out var log, out var readError))
            return Result<WholeLifecycleRead>.Fail(LogBoundKeys.LogUnreadable(readError!));
        if (!AggregateVersion.From(log!.Count(e => e.AggregateId.Value == aggregate!.Value)).TryGetValue(out var version, out var versionError))
            return Result<WholeLifecycleRead>.Fail(LogBoundKeys.LogUnreadable(versionError!));
        if (!ActaLog.Fold(log!).TryGetValue(out var acta, out var foldError))
            return Result<WholeLifecycleRead>.Fail(LogBoundKeys.LogUnreadable(foldError!));

        var stream = new Stream(aggregate!, actor!, version!);
        var verified = verifier.OfLog(log!).For(agent);
        var lifecycle = verified.Honoured;
        long? IndexOf(string eventId) => acta!.IndexOf(eventId);
        bool Names(string kid) => AnyEntryNames(log!, kid);
        if (lifecycle.Entries.IsEmpty) return Result<WholeLifecycleRead>.Ok(new WholeLifecycleRead(new LifecycleRead(lifecycle, IndexOf, null, stream, verified.Skipped, verified.Binding, Names), acta!));
        if (acta!.IndexOf(lifecycle.Entries[^1].EventId) is not { } latest)
            return Result<WholeLifecycleRead>.Fail(LogBoundKeys.LogUnreadable(KeyStatementErrors.Mismatch("entry")));

        return Result<WholeLifecycleRead>.Ok(new WholeLifecycleRead(new LifecycleRead(lifecycle, IndexOf, latest, stream, verified.Skipped, verified.Binding, Names), acta));
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
    internal static async Task<Result<long>> AppendAsync(IEventStore events, LifecycleVerifier verifier, AgentId agent, Stream stream, DomainEvent entry, CancellationToken ct)
    {
        var appended = await events.AppendAsync(stream.Aggregate, stream.Version, [entry], ct).ConfigureAwait(false);
        if (!appended.TryGetValue(out _, out var appendError))
        {
            if (!string.Equals(appendError!.Type, DomainErrors.ConcurrencyConflictType, StringComparison.Ordinal))
                return Result<long>.Fail(appendError);

            var moved = await ReadLifecycleAsync(events, verifier, agent, ct).ConfigureAwait(false);
            if (!moved.TryGetValue(out var now, out var movedError)) return Result<long>.Fail(movedError!);
            return Result<long>.Fail(now!.LatestIndex is { } latest ? KeyActErrors.LifecycleMoved(latest) : appendError);
        }

        var recorded = await ReadLifecycleAsync(events, verifier, agent, ct).ConfigureAwait(false);
        if (!recorded.TryGetValue(out var after, out var recordedError)) return Result<long>.Fail(recordedError!);
        if (after!.IndexOf(entry.Id.Value) is not { } index)
            return Result<long>.Fail(LogBoundKeys.LogUnreadable(KeyStatementErrors.Mismatch("entry")));

        // Ruling 81: the row is in the log; whether the Forum honours it is the fold's to say. A writer never reports success for a row the fold skips.
        if (!after.Lifecycle.Entries.Any(e => string.Equals(e.EventId, entry.Id.Value, StringComparison.Ordinal)))
            return Result<long>.Fail(KeyActErrors.NotHonoured(index, after.Skipped.FirstOrDefault(r => r.LogIndex == index)?.Predicate ?? "unread"));

        return Result<long>.Ok(index);
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
