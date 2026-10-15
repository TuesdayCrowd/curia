using System.Collections.Immutable;
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
/// One lifecycle entry of an identity's stream (spec section 4.4): its event, its type, the key it names
/// and, for a rotation or a retirement, the <c>prev</c> its statement carries.
/// </summary>
/// <param name="EventId">The entry's event id.</param>
/// <param name="Type">One of the four lifecycle types.</param>
/// <param name="Kid">The key the entry binds or retires; null for a declaration (PR 2) and for an enrolment that names none.</param>
/// <param name="Prev">The log index the statement links from; null for an enrolment and its binding.</param>
/// <param name="At">The instant the log recorded the entry.</param>
public sealed record LifecycleEntry(string EventId, string Type, string? Kid, long? Prev, DateTimeOffset At)
{
    /// <summary>A rotation: an <c>agent.key-bound</c> entry that links from a previous entry.</summary>
    public bool IsRotation => string.Equals(Type, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal) && Prev is not null;
}

/// <summary>
/// What one identity's stream says of its keys (spec sections 4.4, 4.8; R4.35 rev., R4.38, R4.39): every
/// lifecycle entry in stream order, folded on the merits. The Forum decides every act against "now",
/// meaning every entry already in the stream, so <see cref="StatusNow"/> counts them all.
///
/// <para><b>The fold checks what an entry says of itself, never who signed it.</b> A signature was
/// verified when the entry was appended, and a reader verifies it again on its own (R6.54 add.); here an
/// entry's statement is read from its exact bytes (<see cref="KeyStatements"/>), and every member the
/// entry states beside <c>statement</c> must equal the statement's own, the entry's member set being
/// closed (KF4). An entry that fails either is refused by name, never skipped: a log that carries one
/// is not a log this Forum can decide an act against.</para>
/// </summary>
public sealed record KeyLifecycle(ImmutableArray<LifecycleEntry> Entries)
{
    private static readonly string[] BoundEnrolmentMembers = [AgentStandingProjector.AgentIdField, AgentStandingProjector.JwkField, AgentStandingProjector.KeyIdField];
    private static readonly string[] BoundRotationMembers = ["agent_id", "authorization", "authorized_by", "jwk", "kid", "possession", "prev", "statement"];
    private static readonly string[] RetiredMembers = ["agent_id", "authorization", "kid", "prev", "statement"];

    /// <summary>
    /// The lifecycle of <paramref name="agent"/> in <paramref name="stream"/>, which may be the identity's
    /// own stream or any wider read: only entries of the identity's aggregate that name it count. A
    /// refusal is the first entry the fold cannot accept.
    /// </summary>
    public static Result<KeyLifecycle> Of(IReadOnlyList<AppendedEvent> stream, AgentId agent)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(agent);

        var entries = ImmutableArray.CreateBuilder<LifecycleEntry>();
        var enrolled = false;
        foreach (var appended in stream)
        {
            // Another aggregate's entries in a wider read are not this identity's, and are skipped.
            if (!string.Equals(appended.AggregateId.Value, agent.Value, StringComparison.Ordinal)) continue;

            var type = appended.Event.Type.Value;

            // An entry of this identity's own aggregate that ends or binds a key and names no agent, or
            // another, cannot be attributed, and skipping it would let a retirement be dropped: the key
            // would stay active. Refused by name, as the class summary says (R4.35 rev., fail closed).
            if (IsLifecycleKind(type)
                && (appended.Event.Payload is not JsonValue.Object attributed
                    || !string.Equals(Text(attributed, AgentStandingProjector.AgentIdField), agent.Value, StringComparison.Ordinal)))
                return Result<KeyLifecycle>.Fail(KeyStatementErrors.Mismatch("entry"));

            // An enrolment that names no agent binds nothing, and a non-lifecycle type is no entry.
            if (appended.Event.Payload is not JsonValue.Object payload) continue;
            if (!string.Equals(Text(payload, AgentStandingProjector.AgentIdField), agent.Value, StringComparison.Ordinal)) continue;

            var id = appended.Event.Id.Value;
            var at = appended.ServerTimestamp.Value;

            if (string.Equals(type, AgentStandingProjector.EnrolledType, StringComparison.Ordinal))
            {
                if (enrolled) continue;
                enrolled = true;
                entries.Add(new LifecycleEntry(id, type, Text(payload, AgentStandingProjector.KeyIdField), null, at));
            }
            else if (string.Equals(type, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal))
            {
                var bound = BoundEntry(payload, id, at, entries);
                if (!bound.TryGetValue(out var entry, out var error)) return Result<KeyLifecycle>.Fail(error!);
                entries.Add(entry!);
            }
            else if (string.Equals(type, LifecycleAppendGuard.KeyRetiredType, StringComparison.Ordinal))
            {
                var retired = RetiredEntry(payload, id, at);
                if (!retired.TryGetValue(out var entry, out var error)) return Result<KeyLifecycle>.Fail(error!);
                entries.Add(entry!);
            }
            else if (string.Equals(type, LifecycleAppendGuard.KeyCompromisedType, StringComparison.Ordinal))
            {
                // A declaration (PR 2) names kids; until the fold reads them it counts as an entry that ends no key.
                entries.Add(new LifecycleEntry(id, type, null, null, at));
            }
        }

        return Result<KeyLifecycle>.Ok(new KeyLifecycle(entries.ToImmutable()));
    }

    /// <summary>
    /// The status of <paramref name="kid"/> now: unbound when no entry binds it; otherwise retired when a
    /// retirement names it; otherwise active.
    /// </summary>
    public KeyStatus StatusNow(string kid)
    {
        ArgumentNullException.ThrowIfNull(kid);

        if (!Entries.Any(e => IsBinding(e) && string.Equals(e.Kid, kid, StringComparison.Ordinal))) return KeyStatus.Unbound;
        return EndedBy(kid) is null ? KeyStatus.Active : KeyStatus.Retired;
    }

    /// <summary>The retirement entry that ended <paramref name="kid"/>, or null when none did.</summary>
    public LifecycleEntry? EndedBy(string kid)
    {
        ArgumentNullException.ThrowIfNull(kid);

        return Entries.FirstOrDefault(e =>
            string.Equals(e.Type, LifecycleAppendGuard.KeyRetiredType, StringComparison.Ordinal)
            && string.Equals(e.Kid, kid, StringComparison.Ordinal));
    }

    /// <summary>How many distinct keys are bound and not ended (R4.39, R7.23).</summary>
    public int LiveCount() =>
        Entries.Where(IsBinding).Select(e => e.Kid!).Distinct(StringComparer.Ordinal).Count(k => StatusNow(k) == KeyStatus.Active);

    /// <summary>How many rotations the log recorded at or after <paramref name="since"/> (R7.23's daily budget).</summary>
    public int RotationsSince(DateTimeOffset since) => Entries.Count(e => e.IsRotation && e.At >= since);

    private static bool IsBinding(LifecycleEntry e) =>
        e.Kid is not null
        && (string.Equals(e.Type, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal)
            || string.Equals(e.Type, AgentStandingProjector.EnrolledType, StringComparison.Ordinal));

    /// <summary>
    /// Whether <paramref name="type"/> is a kind that binds or ends a key (spec section 4.4), as opposed to
    /// the enrolment or an event that is no lifecycle entry at all.
    /// </summary>
    internal static bool IsLifecycleKind(string type) =>
        string.Equals(type, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal)
        || string.Equals(type, LifecycleAppendGuard.KeyRetiredType, StringComparison.Ordinal)
        || string.Equals(type, LifecycleAppendGuard.KeyCompromisedType, StringComparison.Ordinal);

    /// <summary>
    /// R4.38: the one rule for a statement-less <c>agent.key-bound</c>, the binding an enrolment appends
    /// beside <c>agent.enrolled</c>. It is that binding only when the enrolment is the single lifecycle
    /// entry before it, it names the enrolment's own <c>kid</c>, and its member set is exactly
    /// <c>{agent_id, jwk, kid}</c>. Anything else statement-less is a broken link. The fold refuses it and
    /// <see cref="EnrollmentBinding"/> does not bind by it, from this one predicate so they cannot drift.
    /// </summary>
    /// <param name="payload">The entry's payload.</param>
    /// <param name="enrolmentIsOnlyPriorEntry">Whether the identity's own aggregate holds exactly one lifecycle entry before this one, and it is the enrolment. Posts are no lifecycle entries, so a binding that comes after posts still satisfies it.</param>
    /// <param name="enrolmentKid">The <c>kid</c> the enrolment names, or null when it names none.</param>
    internal static bool IsEnrolmentBinding(JsonValue.Object payload, bool enrolmentIsOnlyPriorEntry, string? enrolmentKid)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return enrolmentIsOnlyPriorEntry
            && enrolmentKid is not null
            && string.Equals(Text(payload, AgentStandingProjector.KeyIdField), enrolmentKid, StringComparison.Ordinal)
            && payload.Members.Length == BoundEnrolmentMembers.Length
            && MembersWithin(payload, BoundEnrolmentMembers);
    }

    private static Result<LifecycleEntry> BoundEntry(JsonValue.Object payload, string id, DateTimeOffset at, ImmutableArray<LifecycleEntry>.Builder prior)
    {
        var type = AgentStandingProjector.KeyBoundType;

        // The enrolment's binding is {agent_id, kid, jwk} and nothing else (errata G16); a rotation's
        // carries its statement. The member set of each is closed (KF4). A statement-less binding is the
        // enrolment's only as the one entry that follows it, under its kid (R4.38).
        if (Member(payload, "statement") is null)
        {
            var enrolmentOnly = prior.Count == 1 && string.Equals(prior[0].Type, AgentStandingProjector.EnrolledType, StringComparison.Ordinal);
            if (!IsEnrolmentBinding(payload, enrolmentOnly, enrolmentOnly ? prior[0].Kid : null)) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Mismatch("entry"));
            return Result<LifecycleEntry>.Ok(new LifecycleEntry(id, type, Text(payload, AgentStandingProjector.KeyIdField), null, at));
        }

        if (!MembersWithin(payload, BoundRotationMembers) || payload.Members.Length != BoundRotationMembers.Length)
            return Result<LifecycleEntry>.Fail(KeyStatementErrors.Mismatch("entry"));

        if (Member(payload, "statement") is not JsonValue.String text) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Unreadable("statement"));
        if (Member(payload, "authorization") is not JsonValue.String || Member(payload, "possession") is not JsonValue.String)
            return Result<LifecycleEntry>.Fail(KeyStatementErrors.Unreadable("signature"));

        if (!KeyStatements.ReadRotation(Encoding.UTF8.GetBytes(text.Value)).TryGetValue(out var statement, out var error))
            return Result<LifecycleEntry>.Fail(error!);

        if (!string.Equals(Text(payload, "agent_id"), statement!.AgentId, StringComparison.Ordinal)) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Mismatch("agent_id"));
        if (!string.Equals(Text(payload, "authorized_by"), statement.AuthorizedBy, StringComparison.Ordinal)) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Mismatch("authorized_by"));
        if (!string.Equals(Text(payload, "kid"), statement.Kid, StringComparison.Ordinal)) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Mismatch("kid"));
        if (Member(payload, "prev") is not JsonValue.Number { } prev || prev.Value != statement.Prev) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Mismatch("prev"));
        if (Member(payload, "jwk") is not JsonValue.Object jwk || !PublicJwk.SameKey(jwk, statement.Jwk)) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Mismatch("jwk"));

        return Result<LifecycleEntry>.Ok(new LifecycleEntry(id, type, statement.Kid, statement.Prev, at));
    }

    private static Result<LifecycleEntry> RetiredEntry(JsonValue.Object payload, string id, DateTimeOffset at)
    {
        if (!MembersWithin(payload, RetiredMembers) || payload.Members.Length != RetiredMembers.Length)
            return Result<LifecycleEntry>.Fail(KeyStatementErrors.Mismatch("entry"));

        if (Member(payload, "statement") is not JsonValue.String text) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Unreadable("statement"));
        if (Member(payload, "authorization") is not JsonValue.String) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Unreadable("signature"));

        if (!KeyStatements.ReadRetirement(Encoding.UTF8.GetBytes(text.Value)).TryGetValue(out var statement, out var error))
            return Result<LifecycleEntry>.Fail(error!);

        if (!string.Equals(Text(payload, "agent_id"), statement!.AgentId, StringComparison.Ordinal)) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Mismatch("agent_id"));
        if (!string.Equals(Text(payload, "kid"), statement.Kid, StringComparison.Ordinal)) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Mismatch("kid"));
        if (Member(payload, "prev") is not JsonValue.Number { } prev || prev.Value != statement.Prev) return Result<LifecycleEntry>.Fail(KeyStatementErrors.Mismatch("prev"));

        return Result<LifecycleEntry>.Ok(new LifecycleEntry(id, LifecycleAppendGuard.KeyRetiredType, statement.Kid, statement.Prev, at));
    }

    private static bool MembersWithin(JsonValue.Object payload, string[] allowed) =>
        payload.Members.All(m => allowed.Contains(m.Key, StringComparer.Ordinal));

    private static string? Text(JsonValue.Object payload, string name) =>
        Member(payload, name) is JsonValue.String text ? text.Value : null;

    private static JsonValue? Member(JsonValue.Object payload, string name)
    {
        foreach (var member in payload.Members)
            if (string.Equals(member.Key, name, StringComparison.Ordinal))
                return member.Value;
        return null;
    }
}
