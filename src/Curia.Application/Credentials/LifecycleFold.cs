using System.Collections.Immutable;
using System.Text;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Keys;

namespace Curia.Application.Credentials;

/// <summary>The slugs of R6.64's skip predicates (errata G68): the Forum publishes the predicate of a skipped row by these names and no others.</summary>
public static class LifecyclePredicates
{
    public const string StatementMismatch = "statement-mismatch";
    public const string BrokenLink = "broken-link";
    public const string LifecycleTypMismatch = "lifecycle-typ-mismatch";
    public const string RotationUnauthorized = "rotation-unauthorized";
    public const string UnknownLifecycleKind = "unknown-lifecycle-kind";
    public const string UnknownReason = "unknown-reason";
    public const string DeclarationKeyUnpublished = "declaration-key-unpublished";
    public const string DeclarationKeyIsLogKey = "declaration-key-is-log-key";
    public const string DeclarationKeyNotFirst = "declaration-key-not-first";
    public const string DeclarationSignatureInvalid = "declaration-signature-invalid";
    public const string KidNotBoundToAgent = "kid-not-bound-to-agent";
    public const string DeclarationMismatch = "declaration-mismatch";

    /// <summary>The closed set, in the order R6.64 first names them.</summary>
    public static readonly ImmutableArray<string> All =
    [
        StatementMismatch, UnknownLifecycleKind, UnknownReason, BrokenLink, LifecycleTypMismatch, RotationUnauthorized,
        DeclarationKeyUnpublished, DeclarationKeyIsLogKey, DeclarationKeyNotFirst, DeclarationSignatureInvalid, KidNotBoundToAgent, DeclarationMismatch,
    ];
}

/// <summary>
/// R6.64 (errata G68): one identity's stream, decided row by row against the honoured prefix and the keys the log publishes, the first step that applies deciding each row. A skipped row moves nothing
/// a later row is decided against. The steps are the readers' predicates (<c>LifecycleCheck</c>, <c>lifecycle.rs</c>) without inclusion proofs, in the readers' order.
/// </summary>
internal sealed class LifecycleFold
{
    private static readonly IReadOnlyDictionary<string, IContentSigner> NoSigners = new Dictionary<string, IContentSigner>(StringComparer.Ordinal);

    private static readonly string[] EnrolmentPairMembers = ["agent_id", "jwk", "kid"];
    private static readonly string[] RotationMembers = ["agent_id", "authorization", "authorized_by", "jwk", "kid", "possession", "prev", "statement"];
    private static readonly string[] RetirementMembers = ["agent_id", "authorization", "kid", "prev", "statement"];
    private static readonly string[] DeclarationMembers = ["agent_id", "declaration", "from_index", "kids", "prev", "reason", "statement"];

    private readonly string _agent;
    private readonly IReadOnlyDictionary<string, IContentVerifier> _verifiers;
    private readonly KeyWorld _world;

    private readonly ImmutableArray<AppendedEvent>.Builder _honoured = ImmutableArray.CreateBuilder<AppendedEvent>();
    private readonly ImmutableArray<SkippedRow>.Builder _skipped = ImmutableArray.CreateBuilder<SkippedRow>();
    private readonly Dictionary<string, Bound> _bound = new(StringComparer.Ordinal);
    private readonly List<(long Index, string AuthorizedBy, string Kid)> _rotations = [];
    private readonly HashSet<string> _retired = new(StringComparer.Ordinal);
    private readonly HashSet<string> _compromised = new(StringComparer.Ordinal);

    private bool _enrolled;
    private string? _enrolmentKid;
    private long? _previous;
    private int _lifecycleEntriesBefore;

    private LifecycleFold(string agent, IReadOnlyDictionary<string, IContentVerifier> verifiers, KeyWorld world)
    {
        _agent = agent;
        _verifiers = verifiers;
        _world = world;
    }

    /// <summary>Where a key is bound in the honoured prefix, and the material it verifies under (null for a key whose binding states none, or whose point is not on its curve).</summary>
    private sealed record Bound(long Index, PublicKeyMaterial? Material);

    /// <summary>One published key with its closed form: where the log published it, its <c>kid</c> and the key as the entry states it.</summary>
    internal sealed record PublishedKey(long LogIndex, string Kid, ClosedJwk Key);

    /// <summary>What the log's published keys are, read once per log: each with its closed form (every key <see cref="PublishedKeys"/> holds is one).</summary>
    internal sealed class KeyWorld
    {
        internal KeyWorld(PublishedKeys published)
        {
            LogKeys = [.. published.LogKeys.Select(k => new PublishedKey(k.LogIndex, k.Kid, ClosedJwk.Read(k.Jwk, k.Kid)!))];
            DeclarationKeys = [.. published.DeclarationKeys.Select(k => new PublishedKey(k.LogIndex, k.Kid, ClosedJwk.Read(k.Jwk, k.Kid)!))];
        }

        internal ImmutableArray<PublishedKey> LogKeys { get; }

        internal ImmutableArray<PublishedKey> DeclarationKeys { get; }
    }

    /// <summary>
    /// Decides <paramref name="stream"/>, the lifecycle-typed events of <paramref name="agent"/>'s aggregate in log order, each with its index in the whole log: the events it honours, in order, and
    /// the rows it skips. What the Forum acts on is read from the honoured events alone (<see cref="VerifiedLog.For"/>).
    /// </summary>
    internal static Decided Of(
        string agent,
        IReadOnlyList<(long Index, AppendedEvent Event)> stream,
        KeyWorld world,
        IReadOnlyDictionary<string, IContentVerifier> verifiers)
    {
        var fold = new LifecycleFold(agent, verifiers, world);
        foreach (var (index, appended) in stream) fold.Apply(index, appended);
        return new Decided(fold._honoured.ToImmutable(), fold._skipped.ToImmutable());
    }

    /// <summary>What the fold decided of one stream: the events honoured, in order, and the rows skipped.</summary>
    internal sealed record Decided(ImmutableArray<AppendedEvent> Honoured, ImmutableArray<SkippedRow> Skipped);

    private void Apply(long index, AppendedEvent appended)
    {
        var type = appended.Event.Type.Value;
        var before = _lifecycleEntriesBefore++;

        if (string.Equals(type, AgentStandingProjector.EnrolledType, StringComparison.Ordinal))
        {
            // The stream's first enrolment is the root, honoured as the Forum's own record; a later one is no row and binds nothing.
            if (!_enrolled) Enrol(index, appended);
            return;
        }

        var payload = appended.Event.Payload;
        if (before == 1 && _enrolled && IsEnrolmentPair(payload, type) is { } pair)
        {
            Honour(index, appended);
            _bound[pair.Kid] = new Bound(index, pair.Key.Material(pair.Kid));
            return;
        }

        var predicate = Decide(index, appended, before);
        if (predicate is not null)
            _skipped.Add(new SkippedRow(index, type, appended.AggregateId.Value, predicate));
    }

    private void Enrol(long index, AppendedEvent appended)
    {
        _enrolled = true;
        _enrolmentKid = appended.Event.Payload is JsonValue.Object o ? Text(o, AgentStandingProjector.KeyIdField) : null;

        // The enrolment alone binds no kid and no key, and is the previous honoured entry (R6.64, ruling 55): the pair or a rotation binds.
        Honour(index, appended);
    }

    /// <summary>R4.38: the enrolment's pair, or null when <paramref name="payload"/> is not it (the caller has established that the enrolment is the only lifecycle entry before it).</summary>
    private (string Kid, JsonValue.Object Jwk, ClosedJwk Key)? IsEnrolmentPair(JsonValue payload, string type)
    {
        if (!string.Equals(type, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal)
            || payload is not JsonValue.Object o
            || Member(o, "statement") is not null
            || _enrolmentKid is null
            || !string.Equals(Text(o, AgentStandingProjector.KeyIdField), _enrolmentKid, StringComparison.Ordinal)
            || !HasExactly(o, EnrolmentPairMembers)
            || !string.Equals(Text(o, AgentStandingProjector.AgentIdField), _agent, StringComparison.Ordinal)
            || Member(o, AgentStandingProjector.JwkField) is not JsonValue.Object jwk
            || ClosedJwk.Read(jwk, _enrolmentKid) is not { } key)
            return null;

        return (_enrolmentKid, jwk, key);
    }

    private void Honour(long index, AppendedEvent appended)
    {
        _honoured.Add(appended);
        _previous = index;
    }

    /// <summary>R6.64's steps in order: the slug of the predicate that skips the row, or null when the row is honoured (and has then been applied).</summary>
    private string? Decide(long index, AppendedEvent appended, int lifecycleEntriesBefore)
    {
        var type = appended.Event.Type.Value;

        // 1. The payload is not an object.
        if (appended.Event.Payload is not JsonValue.Object o) return LifecyclePredicates.StatementMismatch;

        var kind = KindOf(type);
        var statementText = Member(o, "statement") is JsonValue.String s ? s.Value : null;
        var statementObject = statementText is null ? null : ParseObject(statementText);

        // 2. A statement that is a JSON object with a string typ, and the typ is not the entry's kind's current one.
        if (statementObject is not null && Member(statementObject, "typ") is JsonValue.String typ && !string.Equals(typ.Value, kind.StatementTyp, StringComparison.Ordinal))
            return KnownStatementTyps.Contains(typ.Value, StringComparer.Ordinal) ? LifecyclePredicates.StatementMismatch : LifecyclePredicates.UnknownLifecycleKind;

        // 3. A declaration whose statement is a JSON object and whose reason is a string outside the closed set.
        if (kind.IsDeclaration && statementObject is not null && Member(statementObject, "reason") is JsonValue.String reason && !CompromiseReasons.Parse(reason.Value).IsOk)
            return LifecyclePredicates.UnknownReason;

        // 4. The payload's agent_id is not the stream's agent.
        if (!string.Equals(Text(o, AgentStandingProjector.AgentIdField), _agent, StringComparison.Ordinal)) return LifecyclePredicates.StatementMismatch;

        // 5. An agent.key-bound whose statement is absent or not a string (the pair, if it is, was honoured as the root).
        if (kind.IsBinding && statementText is null)
        {
            var inPairPosition = _enrolled && lifecycleEntriesBefore == 1 && _enrolmentKid is not null
                && string.Equals(Text(o, AgentStandingProjector.KeyIdField), _enrolmentKid, StringComparison.Ordinal);
            return inPairPosition ? LifecyclePredicates.StatementMismatch : LifecyclePredicates.BrokenLink;
        }

        // 6. The steps of the entry's kind.
        return kind.IsBinding ? Rotation(index, appended, o, statementText!)
            : kind.IsDeclaration ? Declaration(index, appended, o, statementText)
            : Retirement(index, appended, o, statementText);
    }

    private string? Rotation(long index, AppendedEvent appended, JsonValue.Object o, string statementText)
    {
        if (!HasExactly(o, RotationMembers)) return LifecyclePredicates.StatementMismatch;
        if (!KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(statementText)).TryGetValue(out var statement, out _)) return LifecyclePredicates.StatementMismatch;
        if (!KeyStatements.MaterialOf(statement!).TryGetValue(out var newKey, out _)) return LifecyclePredicates.StatementMismatch;

        if (!string.Equals(Text(o, "agent_id"), statement!.AgentId, StringComparison.Ordinal)
            || !string.Equals(Text(o, "authorized_by"), statement.AuthorizedBy, StringComparison.Ordinal)
            || !string.Equals(Text(o, "kid"), statement.Kid, StringComparison.Ordinal)
            || !IsInteger(Member(o, "prev"), statement.Prev)
            || Member(o, "jwk") is not JsonValue.Object entryJwk
            || !PublicJwk.SameKey(entryJwk, statement.Jwk))
            return LifecyclePredicates.StatementMismatch;

        if (statement.Prev != _previous) return LifecyclePredicates.BrokenLink;

        var authorization = Signature(o, "authorization");
        var possession = Signature(o, "possession");
        if (!HasHeaderTyp(authorization, KeyStatements.RotationHeaderTyp) || !HasHeaderTyp(possession, KeyStatements.RotationHeaderTyp)) return LifecyclePredicates.LifecycleTypMismatch;
        if (!HasHeaderKid(authorization, statement.AuthorizedBy) || !HasHeaderKid(possession, statement.Kid)) return LifecyclePredicates.RotationUnauthorized;

        if (!IsLive(statement.AuthorizedBy, out var authorizer) || authorizer.Material is null) return LifecyclePredicates.RotationUnauthorized;
        if (!KeyActs.CanonicalOf(statementText).TryGetValue(out var canonical, out _)) return LifecyclePredicates.StatementMismatch;
        if (!Signed(authorization, canonical, KeyStatements.RotationHeaderTyp, authorizer.Material)
            || !Signed(possession, canonical, KeyStatements.RotationHeaderTyp, newKey!))
            return LifecyclePredicates.RotationUnauthorized;

        if (_bound.ContainsKey(statement.Kid)) return LifecyclePredicates.StatementMismatch;

        Honour(index, appended);
        _bound[statement.Kid] = new Bound(index, newKey);
        _rotations.Add((index, statement.AuthorizedBy, statement.Kid));
        return null;
    }

    private string? Retirement(long index, AppendedEvent appended, JsonValue.Object o, string? statementText)
    {
        if (!HasExactly(o, RetirementMembers) || statementText is null) return LifecyclePredicates.StatementMismatch;
        if (!KeyStatements.ReadRetirement(Encoding.UTF8.GetBytes(statementText)).TryGetValue(out var statement, out _)) return LifecyclePredicates.StatementMismatch;

        if (!string.Equals(Text(o, "agent_id"), statement!.AgentId, StringComparison.Ordinal)
            || !string.Equals(Text(o, "kid"), statement.Kid, StringComparison.Ordinal)
            || !IsInteger(Member(o, "prev"), statement.Prev))
            return LifecyclePredicates.StatementMismatch;

        if (statement.Prev != _previous) return LifecyclePredicates.BrokenLink;

        var authorization = Signature(o, "authorization");
        if (!HasHeaderTyp(authorization, KeyStatements.RetirementHeaderTyp)) return LifecyclePredicates.LifecycleTypMismatch;
        if (!HasHeaderKid(authorization, statement.Kid)) return LifecyclePredicates.RotationUnauthorized;

        if (!IsLive(statement.Kid, out var key) || key.Material is null) return LifecyclePredicates.RotationUnauthorized;
        if (!KeyActs.CanonicalOf(statementText).TryGetValue(out var canonical, out _)) return LifecyclePredicates.StatementMismatch;
        if (!Signed(authorization, canonical, KeyStatements.RetirementHeaderTyp, key.Material)) return LifecyclePredicates.RotationUnauthorized;

        Honour(index, appended);
        _retired.Add(statement.Kid);
        return null;
    }

    private string? Declaration(long index, AppendedEvent appended, JsonValue.Object o, string? statementText)
    {
        if (!HasExactly(o, DeclarationMembers) || statementText is null) return LifecyclePredicates.StatementMismatch;
        if (!KeyStatements.ReadCompromise(Encoding.UTF8.GetBytes(statementText)).TryGetValue(out var statement, out _)) return LifecyclePredicates.StatementMismatch;

        if (!string.Equals(Text(o, "agent_id"), statement!.AgentId, StringComparison.Ordinal)
            || !IsInteger(Member(o, "from_index"), statement.FromIndex)
            || !IsInteger(Member(o, "prev"), statement.Prev)
            || !string.Equals(Text(o, "reason"), CompromiseReasons.Wire(statement.Reason), StringComparison.Ordinal)
            || Member(o, "kids") is not JsonValue.Array kidItems
            || !kidItems.Items.Select(k => (k as JsonValue.String)?.Value).SequenceEqual(statement.Kids, StringComparer.Ordinal))
            return LifecyclePredicates.StatementMismatch;

        if (statement.Prev != _previous) return LifecyclePredicates.BrokenLink;

        var declaration = Signature(o, "declaration");
        if (!HasHeaderTyp(declaration, KeyStatements.CompromiseHeaderTyp)) return LifecyclePredicates.LifecycleTypMismatch;
        if (!DetachedJws.ReadProtectedHeader(declaration).TryGetValue(out var header, out _) || string.IsNullOrEmpty(header!.Kid))
            return LifecyclePredicates.DeclarationKeyUnpublished;

        // R6.62's checks, in R6.62's order, over the log's own key entries: the key set is the whole log's.
        var named = _world.DeclarationKeys.FirstOrDefault(k => string.Equals(k.Kid, header.Kid, StringComparison.Ordinal));
        if (named is null) return LifecyclePredicates.DeclarationKeyUnpublished;
        if (_world.LogKeys.Any(k => string.Equals(k.Kid, named.Kid, StringComparison.Ordinal) || k.Key.SamePublicKey(named.Key))) return LifecyclePredicates.DeclarationKeyIsLogKey;
        if (_world.DeclarationKeys.Any(k => k.LogIndex <= named.LogIndex && !string.Equals(k.Kid, named.Kid, StringComparison.Ordinal))) return LifecyclePredicates.DeclarationKeyNotFirst;
        if (named.LogIndex >= index) return LifecyclePredicates.DeclarationKeyUnpublished;

        if (named.Key.Material(named.Kid) is not { } material
            || !KeyActs.CanonicalOf(statementText).TryGetValue(out var canonical, out _)
            || !Signed(declaration, canonical, KeyStatements.CompromiseHeaderTyp, material))
            return LifecyclePredicates.DeclarationSignatureInvalid;

        // KF3: every kid is bound by the honoured prefix; the binding of kids[0] is at or below from_index, which is below the declaration's own index; kids is the cascade.
        var kids = statement.Kids;
        if (kids.Any(k => !_bound.ContainsKey(k))) return LifecyclePredicates.KidNotBoundToAgent;
        if (_bound[kids[0]].Index > statement.FromIndex || statement.FromIndex >= index) return LifecyclePredicates.DeclarationMismatch;

        var cascade = new List<string> { kids[0] };
        foreach (var rotation in _rotations)
            if (rotation.Index >= statement.FromIndex && cascade.Contains(rotation.AuthorizedBy, StringComparer.Ordinal) && !cascade.Contains(rotation.Kid, StringComparer.Ordinal))
                cascade.Add(rotation.Kid);
        if (!kids.SequenceEqual(cascade, StringComparer.Ordinal)) return LifecyclePredicates.DeclarationMismatch;

        Honour(index, appended);
        foreach (var kid in kids) _compromised.Add(kid);
        return null;
    }

    /// <summary>The key is bound by the honoured prefix and neither retired nor compromised in it.</summary>
    private bool IsLive(string kid, out Bound bound)
    {
        if (_bound.TryGetValue(kid, out var held) && !_retired.Contains(kid) && !_compromised.Contains(kid))
        {
            bound = held;
            return true;
        }

        bound = new Bound(-1, null);
        return false;
    }

    private bool Signed(JwsSignature signature, CanonicalBytes canonical, string headerTyp, PublicKeyMaterial key) =>
        new DetachedJws(NoSigners, _verifiers, headerTyp).Verify(canonical, signature, key).IsOk;

    private static readonly string[] KnownStatementTyps =
        [KeyStatements.RotationStatementTyp, KeyStatements.RetirementStatementTyp, KeyStatements.CompromiseStatementTyp];

    private sealed record Kind(string StatementTyp, bool IsBinding, bool IsDeclaration);

    private static Kind KindOf(string type) =>
        string.Equals(type, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal) ? new Kind(KeyStatements.RotationStatementTyp, true, false)
        : string.Equals(type, LifecycleAppendGuard.KeyCompromisedType, StringComparison.Ordinal) ? new Kind(KeyStatements.CompromiseStatementTyp, false, true)
        : new Kind(KeyStatements.RetirementStatementTyp, false, false);

    private static JsonValue.Object? ParseObject(string text) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(text), AdmitLimits.Default).TryGetValue(out var root, out _) && root is JsonValue.Object o ? o : null;

    /// <summary>A signature member that is not a string is a signature with no header, which carries no typ and no kid.</summary>
    private static JwsSignature Signature(JsonValue.Object o, string name) => new(Text(o, name) ?? string.Empty);

    private static bool HasHeaderTyp(JwsSignature signature, string typ) =>
        DetachedJws.ReadProtectedHeader(signature).TryGetValue(out var header, out _) && string.Equals(header!.Typ, typ, StringComparison.Ordinal);

    private static bool HasHeaderKid(JwsSignature signature, string kid) =>
        DetachedJws.ReadProtectedHeader(signature).TryGetValue(out var header, out _) && string.Equals(header!.Kid, kid, StringComparison.Ordinal);

    private static bool IsInteger(JsonValue? value, long expected) => value is JsonValue.Number n && n.Value == expected;

    private static bool HasExactly(JsonValue.Object o, string[] names) =>
        o.Members.Length == names.Length
        && o.Members.Select(m => m.Key).Order(StringComparer.Ordinal).SequenceEqual(names, StringComparer.Ordinal);

    private static string? Text(JsonValue.Object o, string name) => Member(o, name) is JsonValue.String s ? s.Value : null;

    private static JsonValue? Member(JsonValue.Object o, string name)
    {
        foreach (var m in o.Members)
            if (string.Equals(m.Key, name, StringComparison.Ordinal))
                return m.Value;
        return null;
    }
}
