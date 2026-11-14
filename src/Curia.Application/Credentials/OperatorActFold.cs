using System.Buffers.Text;
using System.Collections.Immutable;
using System.Text;
using Curia.Application.Projections;
using Curia.Canon.Canonical;
using Curia.Canon.Envelope;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Credentials;
using Curia.Domain.Moderation;

namespace Curia.Application.Credentials;

/// <summary>The slugs of R6.67's skip predicates (errata G70): the Forum publishes the predicate of a skipped operator-act row by these names and no others.</summary>
public static class OperatorActPredicates
{
    public const string StatementMismatch = "act-statement-mismatch";
    public const string NotSigned = "act-unsigned";
    public const string UnknownKind = "act-unknown-kind";
    public const string BrokenLink = "act-broken-link";
    public const string TypMismatch = "act-typ-mismatch";
    public const string KeyUnpublished = "act-key-unpublished";
    public const string KeyNotFirst = "act-key-not-first";
    public const string SignatureInvalid = "act-signature-invalid";

    /// <summary>The closed set, in the order R6.67 names them.</summary>
    public static readonly ImmutableArray<string> All =
    [
        StatementMismatch, NotSigned, UnknownKind, BrokenLink, TypMismatch, KeyUnpublished, KeyNotFirst, SignatureInvalid,
    ];
}

/// <summary>One operator-act row decided (R6.67): where it sits in the log, its type, the stream it belongs to and the predicate that skipped it, null when it is honoured. No payload member.</summary>
internal sealed record ActDecision(long LogIndex, string Type, string Aggregate, string? Predicate);

/// <summary>
/// R6.67 (errata G70): every operator-act row of a log (<c>agent.owner-attested</c>, <c>moderation.applied</c>) decided against its stream's honoured prefix and the operator key the log publishes, in log order,
/// <b>the first step that applies deciding it</b>, the signature last (it is the one step that costs, ruling 11). A skipped row moves nothing a later row is decided against. The decision is the rows only: it
/// computes no owner and no verified flag (R4.1's binding stays the raw projectors', fed <see cref="HonouredEvents"/>). It never throws and never returns a failure for a payload: any JSON value in the payload
/// position decides to a row with one of <see cref="OperatorActPredicates.All"/>, or to honoured.
/// </summary>
internal static class OperatorActFold
{
    /// <summary>The header <c>typ</c> of an operator act's signature (R6.66). Written here and in <c>PublishedKeys.Classes</c>; OAS9b ties both to the errata.</summary>
    internal const string HeaderTyp = "curia-operator-act+jws";

    private const string HeaderAlg = "ES256";
    private const string AttestationTyp = "curia.owner-attestation/1";
    private const string ModerationTyp = "curia.moderation/1";
    private const string Human = "human";

    private static readonly IReadOnlyDictionary<string, IContentSigner> NoSigners = new Dictionary<string, IContentSigner>(StringComparer.Ordinal);

    private enum Member
    {
        Text,
        Flag,
        Index,
        Texts,
    }

    /// <summary>One kind of act: its row type, its statement's current <c>typ</c>, its stream's root type, its subject member and the closed form of its statement (R6.67 step 3).</summary>
    private sealed record Kind(string RowType, string StatementTyp, string RootType, string SubjectMember, (string Name, Member Type)[] Form, bool IsModeration);

    private static readonly Kind Attestation = new(
        AgentStandingProjector.OwnerAttestedType,
        AttestationTyp,
        AgentStandingProjector.EnrolledType,
        "agent_id",
        [
            ("actor_id", Member.Text), ("agent_id", Member.Text), ("method", Member.Text), ("owner_id", Member.Text), ("owner_verified", Member.Flag),
            ("prev", Member.Index), ("reason", Member.Text), ("typ", Member.Text),
        ],
        IsModeration: false);

    private static readonly Kind Moderation = new(
        FlagProjector.ModerationAppliedType,
        ModerationTyp,
        PostProjector.PostAcceptedType,
        "post_id",
        [
            ("actor_id", Member.Text), ("adjudicates", Member.Texts), ("category", Member.Text), ("digest", Member.Text), ("effect", Member.Text),
            ("moderator", Member.Text), ("post_id", Member.Text), ("prev", Member.Index), ("rationale", Member.Text), ("typ", Member.Text),
        ],
        IsModeration: true);

    /// <summary>What the fold knows of one stream (the events of one aggregate) as it walks the log: its first event, which is its root if it is of the root's type, and the last act row it honoured.</summary>
    private sealed class Stream(long firstIndex, string firstType)
    {
        internal long FirstIndex { get; } = firstIndex;

        internal string FirstType { get; } = firstType;

        internal long? LastHonoured { get; set; }
    }

    /// <summary>
    /// Decides every operator-act row of <paramref name="log"/>, which is the whole log from its start (R6.47: a log index is a position in it) or a prefix of it, in log order. <paramref name="published"/> is the
    /// keys the same log publishes; <paramref name="verifiers"/> the primitives, by algorithm.
    /// </summary>
    internal static ImmutableArray<ActDecision> Decide(IReadOnlyList<AppendedEvent> log, PublishedKeys published, IReadOnlyDictionary<string, IContentVerifier> verifiers)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(verifiers);

        var decisions = ImmutableArray.CreateBuilder<ActDecision>();
        var streams = new Dictionary<string, Stream>(StringComparer.Ordinal);

        for (var i = 0; i < log.Count; i++)
        {
            var appended = log[i];
            var type = appended.Event.Type.Value;
            var aggregate = appended.AggregateId.Value;
            if (!streams.TryGetValue(aggregate, out var stream)) streams[aggregate] = stream = new Stream(i, type);

            var kind = KindOf(type);
            if (kind is null) continue;

            var predicate = Decide(i, appended, kind, stream, published, verifiers);
            if (predicate is null) stream.LastHonoured = i;
            decisions.Add(new ActDecision(i, type, aggregate, predicate));
        }

        return decisions.ToImmutable();
    }

    private static Kind? KindOf(string type) =>
        string.Equals(type, Attestation.RowType, StringComparison.Ordinal) ? Attestation
        : string.Equals(type, Moderation.RowType, StringComparison.Ordinal) ? Moderation
        : null;

    /// <summary>R6.67's seven steps, in order: the slug of the first predicate that applies, or null when the row is honoured.</summary>
    private static string? Decide(long index, AppendedEvent appended, Kind kind, Stream stream, PublishedKeys published, IReadOnlyDictionary<string, IContentVerifier> verifiers)
    {
        // 1. The payload is not an object; or it has no statement member that is a string.
        if (appended.Event.Payload is not JsonValue.Object row) return OperatorActPredicates.StatementMismatch;
        if (Find(row, "statement") is not JsonValue.String statementText) return OperatorActPredicates.NotSigned;

        // 2. The statement is a JSON object with a string typ that is not this type's current one.
        var statement = ParseObject(statementText.Value);
        if (statement is not null && Find(statement, "typ") is JsonValue.String typ && !string.Equals(typ.Value, kind.StatementTyp, StringComparison.Ordinal))
        {
            var other = ReferenceEquals(kind, Attestation) ? ModerationTyp : AttestationTyp;
            return string.Equals(typ.Value, other, StringComparison.Ordinal) ? OperatorActPredicates.StatementMismatch : OperatorActPredicates.UnknownKind;
        }

        // 3. The statement is not its own canonical form, or not the closed form of its kind, or the row disagrees with it, or its subject or actor is not the stream's, or the moderator is not human.
        if (statement is null
            || !IsOwnCanonicalForm(statement, statementText.Value)
            || !IsClosedForm(statement, kind)
            || !RowAgrees(row, statement)
            || !string.Equals(Text(statement, kind.SubjectMember), appended.AggregateId.Value, StringComparison.Ordinal)
            || !string.Equals(Text(statement, "actor_id"), appended.Event.Actor?.Value, StringComparison.Ordinal)
            || (kind.IsModeration && !string.Equals(Text(statement, "moderator"), Human, StringComparison.Ordinal)))
            return OperatorActPredicates.StatementMismatch;

        // 4. prev is not the log index of the previous honoured act row of the stream or, when there is none, of the stream's root (its first event, of the root's type); or the stream has no root.
        var root = string.Equals(stream.FirstType, kind.RootType, StringComparison.Ordinal) ? stream.FirstIndex : (long?)null;
        var expectedPrev = stream.LastHonoured ?? root;
        if (expectedPrev is null || Find(statement, "prev") is not JsonValue.Number prev || prev.Value != expectedPrev.Value) return OperatorActPredicates.BrokenLink;

        // 5. No authorization that is a detached JWS (a compact JWS whose payload part is empty, R6.67 step 5) whose protected header is a JSON object the ADMIT profile accepts (a repeated member refuses it), whose typ is the act's and whose alg is ES256.
        if (Find(row, "authorization") is not JsonValue.String authorization
            || authorization.Value.Split('.') is not [var headerSegment, "", _]
            || !IsAdmittedObject(headerSegment)
            || !DetachedJws.ReadProtectedHeader(new JwsSignature(authorization.Value)).TryGetValue(out var header, out _)
            || !string.Equals(header!.Typ, HeaderTyp, StringComparison.Ordinal)
            || !string.Equals(header.Alg, HeaderAlg, StringComparison.Ordinal))
            return OperatorActPredicates.TypMismatch;

        // 6. The header kid names no operator key published at an earlier log index than the row; or names one that is not the first. "The" key is the first the log holds (R6.66).
        var keys = published.OperatorKeys;
        var named = keys.FirstOrDefault(k => string.Equals(k.Kid, header.Kid, StringComparison.Ordinal) && k.LogIndex < index);
        if (named is null) return OperatorActPredicates.KeyUnpublished;
        if (named.LogIndex != keys[0].LogIndex) return OperatorActPredicates.KeyNotFirst;

        // 7. The authorization does not verify over the statement's canonical form under that key. The one step that calls the primitive, so the last.
        if (ClosedJwk.Read(named.Jwk, named.Kid)?.Material(named.Kid) is not { } material
            || !KeyActs.CanonicalOf(statementText.Value).TryGetValue(out var canonical, out _)
            || !new DetachedJws(NoSigners, verifiers, HeaderTyp).Verify(canonical, new JwsSignature(authorization.Value), material).IsOk)
            return OperatorActPredicates.SignatureInvalid;

        return null;
    }

    /// <summary>The protected header's base64url segment decodes to text the ADMIT profile accepts as a JSON object: a repeated member name, at any depth, is no object (R6.67 step 5).</summary>
    private static bool IsAdmittedObject(string headerSegment) =>
        Base64Url.IsValid(headerSegment)
        && JsonReader.Parse(Base64Url.DecodeFromChars(headerSegment), AdmitLimits.Default).TryGetValue(out var root, out _)
        && root is JsonValue.Object;

    /// <summary>The statement as a JSON object, or null when it is not text that parses to one.</summary>
    private static JsonValue.Object? ParseObject(string text) =>
        JsonReader.Parse(Encoding.UTF8.GetBytes(text), AdmitLimits.Default).TryGetValue(out var root, out _) && root is JsonValue.Object o ? o : null;

    private static bool IsOwnCanonicalForm(JsonValue statement, string text) =>
        CanonicalJson.CanonicalizeWithNfc(statement).TryGetValue(out var canonical, out _)
        && string.Equals(Encoding.UTF8.GetString(canonical.Span), text, StringComparison.Ordinal);

    /// <summary>The statement holds exactly the members of its kind's closed form, each of the form's type, and its <c>typ</c> is the kind's.</summary>
    private static bool IsClosedForm(JsonValue.Object statement, Kind kind)
    {
        if (statement.Members.Length != kind.Form.Length) return false;

        foreach (var (name, type) in kind.Form)
        {
            var ok = Find(statement, name) switch
            {
                JsonValue.String => type is Member.Text,
                JsonValue.Bool => type is Member.Flag,
                JsonValue.Number n => type is Member.Index && n.Value >= 0 && n.Value == Math.Floor(n.Value),
                JsonValue.Array a => type is Member.Texts && a.Items.All(item => item is JsonValue.String),
                _ => false,
            };
            if (!ok) return false;
        }

        // The closed form closes the values of the members a consumer reads (R6.67 step 3): R4.24's four proofs, the four effects, R10.35's seven flag types.
        var valuesOk = kind.IsModeration
            ? ModerationEffects.Parse(Text(statement, "effect") ?? string.Empty).IsOk && FlagKinds.Parse(Text(statement, "category") ?? string.Empty).IsOk
            : OwnerVerificationMethods.Parse(Text(statement, "method") ?? string.Empty).IsOk;
        return valuesOk && string.Equals(Text(statement, "typ"), kind.StatementTyp, StringComparison.Ordinal);
    }

    /// <summary>Every member the row states beside <c>statement</c> and <c>authorization</c> is the statement's member of the same name and equals it; a row member that is neither is a disagreement.</summary>
    private static bool RowAgrees(JsonValue.Object row, JsonValue.Object statement)
    {
        foreach (var (name, value) in row.Members)
        {
            if (string.Equals(name, "statement", StringComparison.Ordinal) || string.Equals(name, "authorization", StringComparison.Ordinal)) continue;
            if (Find(statement, name) is not { } stated || !SameValue(value, stated)) return false;
        }

        return true;
    }

    /// <summary>The same JSON value (R6.67 step 3): equal under RFC 8785, no normalization, so a string is compared code point for code point.</summary>
    private static bool SameValue(JsonValue a, JsonValue b) =>
        CanonicalJson.Canonicalize(a).TryGetValue(out var left, out _)
        && CanonicalJson.Canonicalize(b).TryGetValue(out var right, out _)
        && left.Span.SequenceEqual(right.Span);

    private static string? Text(JsonValue.Object o, string name) => Find(o, name) is JsonValue.String s ? s.Value : null;

    private static JsonValue? Find(JsonValue.Object o, string name)
    {
        foreach (var m in o.Members)
            if (string.Equals(m.Key, name, StringComparison.Ordinal))
                return m.Value;
        return null;
    }
}
