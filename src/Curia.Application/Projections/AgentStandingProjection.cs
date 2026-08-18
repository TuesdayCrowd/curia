using System.Collections.Immutable;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Authorization;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>
/// One agent's standing, as the event log records it: the facts Table 11's entry criteria are
/// evaluated against, minus the instant they are evaluated <i>at</i>.
///
/// <para>Every member here was either recorded on an event or counted from events. Nothing was
/// read from a clock and nothing was carried in a token -- R7.7's "computed from live state,
/// never read solely from a token claim", with "live state" meaning the log rather than a
/// process's memory.</para>
/// </summary>
/// <param name="AgentId">The agent these facts are about; also the aggregate its events land in.</param>
/// <param name="CredentialHistory">
/// The Table 6 transitions this agent's events describe, in <c>seq</c> order -- exactly what
/// <see cref="CredentialLifecycle.Project"/> and <see cref="PostureProjector.Fold"/> consume.
/// </param>
/// <param name="EnrolledAt">
/// When the credential became active. Identical to what <see cref="PostureProjector.Fold"/>
/// re-derives from <paramref name="CredentialHistory"/> (the first
/// <see cref="CredentialTrigger.SuccessfulEnrollment"/>'s timestamp), because both read the same
/// event; carried here because <see cref="ReachedT1At"/> is computed from it before the posture
/// fold runs.
/// </param>
/// <param name="OwnerVerified">
/// Table 11's "owner verified", as of the most recent event that spoke to it. Unlike
/// <paramref name="EnrolledAt"/> this genuinely changes -- an owner completing verification later
/// should count, and one whose verification lapses should stop counting.
/// </param>
/// <param name="QuestionsWithoutUpheldFlags">Table 11's "≥ 3 questions with no upheld flags".</param>
/// <param name="ReachedT1At">
/// When T1 was first satisfied, per <see cref="TierPolicy.FirstSatisfiedT1At"/>. Derived, never
/// stamped: see that method's remarks for why the instant a request happened to notice the
/// promotion is the wrong answer.
/// </param>
public sealed record AgentStanding(
    string AgentId,
    ImmutableArray<CredentialTransitionedEvent> CredentialHistory,
    DateTimeOffset? EnrolledAt,
    bool OwnerVerified,
    int QuestionsWithoutUpheldFlags,
    DateTimeOffset? ReachedT1At)
{
    /// <summary>
    /// Structural equality, spelled out rather than left to the compiler because
    /// <see cref="ImmutableArray{T}"/>'s own <c>Equals</c> compares the underlying array
    /// <i>reference</i>. The generated record equality therefore reports two standings folded from
    /// the very same events as different, purely because each fold allocated its own array -- and
    /// that is not cosmetic: R11.9's rebuild-from-zero drill is asserted by comparing a projection
    /// against its own rebuild, so a type whose <c>==</c> is false for identical content makes the
    /// drill unassertable and would leave it looking like a passing test of nothing.
    /// </summary>
    public bool Equals(AgentStanding? other) =>
        other is not null
        && string.Equals(AgentId, other.AgentId, StringComparison.Ordinal)
        && EnrolledAt == other.EnrolledAt
        && OwnerVerified == other.OwnerVerified
        && QuestionsWithoutUpheldFlags == other.QuestionsWithoutUpheldFlags
        && ReachedT1At == other.ReachedT1At
        && CredentialHistory.SequenceEqual(other.CredentialHistory);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        AgentId,
        EnrolledAt,
        OwnerVerified,
        QuestionsWithoutUpheldFlags,
        ReachedT1At,

        // Length rather than the elements: a hash has only to agree with Equals on the values that
        // are equal, and hashing every transition would cost a walk of the history on a type whose
        // identity is already carried by the agent id.
        CredentialHistory.Length);
}

/// <summary>
/// Builds the per-agent standing read model purely from the event stream -- R11.9's "all read
/// models SHALL be rebuildable from [the event table] by replay", applied to the last facts that
/// were not.
///
/// <para><b>What this replaced, and why it had to move.</b> The enrollment instant, the
/// owner-verification flag and the first-reached-T1 instant lived in an in-process dictionary, so
/// a restart dropped every agent to no standing at all -- silently, and in the direction that
/// looks like policy: every agent became Anonymous/T0 and the token endpoint reported them
/// unenrolled. R4.21 already says what these are ("state transitions SHALL be append-only events
/// carrying actor, reason, and timestamp; the current state is a projection"), so this is not a
/// sixth operational table. It is the projection R4.21 always described, finally reading the log
/// it was always supposed to read.</para>
///
/// <para><b>No clock, deliberately</b>, for the reason <see cref="AggregateSummaryProjector"/> and
/// <see cref="PostureProjector"/> both record: a rebuild that consulted "now" would make R11.9's
/// replay drill tautological, because two runs would differ only by when they ran. Every instant
/// below is one an event already carries. The elapsed-time half of Table 11 stays in
/// <see cref="TierPolicy.Evaluate"/>, which takes the instant as an argument.</para>
///
/// <para><b>Ordering is by <c>seq</c>, never by <c>server_ts</c></b>, for the reason
/// <see cref="PostProjector"/> states: one append batch shares one clock read, and nothing rules
/// out a later batch's read landing earlier than an earlier batch's. It matters here because
/// <see cref="AgentStanding.OwnerVerified"/> is last-writer-wins over a sequence of events, and
/// "last" has to mean something the log can actually promise.</para>
/// </summary>
public static class AgentStandingProjector
{
    /// <summary>
    /// The event <c>EnrollAgent</c> appends when an agent first enrolls. R4.21's transition,
    /// with Table 6's trigger implied by the type rather than carried as a payload field: a
    /// <see cref="CredentialTrigger"/> read out of a payload would be a trigger any writer could
    /// invent, and the closed vocabulary CS-11/CS-12 buy would be spent on the way into the log.
    /// A future suspension or retirement is a new event type here, not a new string value there.
    /// </summary>
    public const string EnrolledType = "agent.enrolled";

    /// <summary>
    /// The event <c>EnrollAgent</c> appends when the owner-verification flag changes. Not a
    /// Table 6 transition -- owner verification is a posture fact, not a credential state -- so it
    /// contributes nothing to <see cref="AgentStanding.CredentialHistory"/> and
    /// <see cref="CredentialLifecycle.Project"/> never sees it.
    /// </summary>
    public const string OwnerVerificationRecordedType = "agent.owner-verification-recorded";

    /// <summary>The payload member both event types carry naming the agent they are about.</summary>
    public const string AgentIdField = "agent_id";

    /// <summary>The payload member carrying Table 11's "owner verified" as an I-JSON boolean.</summary>
    public const string OwnerVerifiedField = "owner_verified";

    /// <summary>
    /// The payload member carrying R4.21's "reason". Free text, because R4.21 asks for one and a
    /// projection that invented the reason would be recording its own opinion as the log's.
    /// </summary>
    public const string ReasonField = "reason";

    /// <summary>
    /// The payload member naming the key the enrollment registered. Recorded so the log is
    /// self-describing about what an enrollment actually did, and deliberately not projected: the
    /// Registrar's key store is authoritative for keys (R4.16 rev.), and a second key registry
    /// derived from this stream is a second answer to a question that already has one.
    /// </summary>
    public const string KeyIdField = "kid";

    /// <summary>
    /// Folds a seq-ordered event list into one <see cref="AgentStanding"/> per agent the log knows
    /// about.
    ///
    /// <para>Reads three event types and skips every other, for the reason
    /// <see cref="PostProjector.Fold"/> gives: the log is the system of record for everything, and
    /// a projection that failed on a type it does not model would break whenever an unrelated
    /// feature added one. An event of a type it <i>does</i> model but whose payload is missing a
    /// required member is skipped too, which denies standing rather than inventing it -- the safe
    /// direction, and the same one <see cref="PostureProjector"/> takes with the facts it cannot
    /// yet see.</para>
    ///
    /// <para><c>post.accepted</c> is folded here as well as in <see cref="PostProjector"/>, and
    /// that is not duplication of the read model: Table 11's question count is a posture fact, and
    /// the instant the count crossed its threshold is only observable by walking the same stream
    /// the credential events are in. Counting posts in one pass and merging afterwards would need
    /// the two streams re-interleaved by <c>seq</c>, which is what this pass already is.</para>
    /// </summary>
    public static ImmutableDictionary<string, AgentStanding> Fold(IReadOnlyList<AppendedEvent> eventsInSeqOrder)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);

        var builders = new Dictionary<string, Builder>(StringComparer.Ordinal);
        var lastSeq = EventSequence.Zero;

        foreach (var appended in eventsInSeqOrder)
        {
            if (appended.Seq < lastSeq)
                throw new ArgumentException(
                    "Events must arrive in ascending seq order; every IEventReader this solution " +
                    "ships already guarantees that, so a violation means the caller did not get " +
                    "these from a store's forward scan.",
                    nameof(eventsInSeqOrder));

            lastSeq = appended.Seq;

            if (appended.Event.Payload is not JsonValue.Object payload) continue;

            // The payload is indexed only for a type this projection actually models -- a log of a
            // million posts should not pay for a dictionary per moderation event.
            switch (appended.Event.Type.Value)
            {
                case EnrolledType:
                    ApplyEnrollment(builders, Members(payload), appended);
                    break;

                case OwnerVerificationRecordedType:
                    ApplyOwnerVerification(builders, Members(payload), appended);
                    break;

                case PostProjector.PostAcceptedType:
                    ApplyPost(builders, Members(payload), appended);
                    break;

                default:
                    break;
            }
        }

        return builders.Values
            .Select(b => b.Build())
            .ToImmutableDictionary(s => s.AgentId, s => s, StringComparer.Ordinal);
    }

    /// <summary>
    /// The <see cref="PostureFacts"/> for one agent, as <see cref="TierPolicy.Evaluate"/> consumes
    /// them. Delegates to <see cref="PostureProjector.Fold"/> rather than assembling a
    /// <see cref="PostureFacts"/> directly, so the credential state and the enrollment instant are
    /// re-derived from the transition table exactly once, in the one place that owns that
    /// derivation -- a second assembly of the same facts is how the two come to disagree.
    ///
    /// <para>An agent the log has never heard of is
    /// <see cref="CredentialState.Pending"/> with no enrollment instant, which
    /// <see cref="TierPolicy.Evaluate"/> reports as <see cref="PrincipalTier.Anonymous"/>. That is
    /// a real answer rather than a failure: an unknown agent has a credential that confers
    /// nothing, which is exactly what Table 11's lowest row being "entered by enrollment"
    /// means.</para>
    /// </summary>
    public static Result<PostureFacts> PostureOf(
        IReadOnlyDictionary<string, AgentStanding> standings, string agentId)
    {
        ArgumentNullException.ThrowIfNull(standings);
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);

        if (!standings.TryGetValue(agentId, out var standing))
            return Result<PostureFacts>.Ok(new PostureFacts(CredentialState.Pending));

        // CredentialState and EnrolledAt are this fold's output, not its input -- PostureProjector
        // ignores whatever is passed for them, and passing the projected values would only make it
        // look as though they came from somewhere else.
        return PostureProjector.Fold(
            standing.CredentialHistory,
            new PostureFacts(
                CredentialState.Pending,
                ReachedT1At: standing.ReachedT1At,
                OwnerVerified: standing.OwnerVerified,
                QuestionsWithoutUpheldFlags: standing.QuestionsWithoutUpheldFlags));
    }

    private static void ApplyEnrollment(
        Dictionary<string, Builder> builders,
        Dictionary<string, JsonValue> fields,
        AppendedEvent appended)
    {
        if (!Str(fields, AgentIdField, out var agentId)) return;
        if (!Bool(fields, OwnerVerifiedField, out var ownerVerified)) return;
        if (!Str(fields, ReasonField, out var reasonText)) return;
        if (!TransitionReason.Create(reasonText).TryGetValue(out var reason, out _)) return;

        var builder = For(builders, agentId);

        // The event's own recorded server_ts is the enrollment instant, not anything in the
        // payload: the store stamped it, and R6.5 makes server_ts the Forum's observation rather
        // than a claim. It is also what makes the tenure clock unforgeable by a client.
        builder.Credentials.Add(new CredentialTransitionedEvent(
            CredentialTrigger.SuccessfulEnrollment,
            appended.Event.Actor,
            reason,
            appended.ServerTimestamp.Value));

        builder.EnrolledAt ??= appended.ServerTimestamp.Value;
        builder.OwnerVerified = ownerVerified;
        builder.NoteCountableCriteria(appended.ServerTimestamp.Value);
    }

    private static void ApplyOwnerVerification(
        Dictionary<string, Builder> builders,
        Dictionary<string, JsonValue> fields,
        AppendedEvent appended)
    {
        if (!Str(fields, AgentIdField, out var agentId)) return;
        if (!Bool(fields, OwnerVerifiedField, out var ownerVerified)) return;

        var builder = For(builders, agentId);
        builder.OwnerVerified = ownerVerified;
        builder.NoteCountableCriteria(appended.ServerTimestamp.Value);
    }

    private static void ApplyPost(
        Dictionary<string, Builder> builders,
        Dictionary<string, JsonValue> fields,
        AppendedEvent appended)
    {
        if (!Str(fields, "author", out var author)) return;
        if (!Str(fields, "kind", out var kind)) return;
        if (!string.Equals(kind, QuestionKind, StringComparison.Ordinal)) return;

        // Every accepted question counts. "With no upheld flags" is the moderation outcome Table 11
        // names, and an upheld flag is a review decision (§10.10) with no event type yet -- the
        // ingest-time risk annotations on the post are a different thing entirely, and treating
        // them as upheld flags would deny promotion for content the Forum accepted. Counting all
        // of them matches what the in-memory directory this replaced did, so the move to the log
        // changes durability and nothing else about who is promoted.
        var builder = For(builders, author);
        builder.CleanQuestions++;
        builder.NoteCountableCriteria(appended.ServerTimestamp.Value);
    }

    /// <summary>
    /// Table 9's wire spelling for a question. A literal rather than
    /// <c>PostKinds.Wire(PostKind.Question)</c> because this projection reads the string the log
    /// actually holds; if the two ever diverged, the log's spelling is the one that is true.
    /// </summary>
    private const string QuestionKind = "question";

    private static Builder For(Dictionary<string, Builder> builders, string agentId)
    {
        if (!builders.TryGetValue(agentId, out var builder))
        {
            builder = new Builder(agentId);
            builders[agentId] = builder;
        }

        return builder;
    }

    private static Dictionary<string, JsonValue> Members(JsonValue.Object payload)
    {
        var fields = new Dictionary<string, JsonValue>(StringComparer.Ordinal);
        foreach (var member in payload.Members)
            fields[member.Key] = member.Value;
        return fields;
    }

    private static bool Str(Dictionary<string, JsonValue> fields, string name, out string value)
    {
        if (fields.TryGetValue(name, out var raw) && raw is JsonValue.String s)
        {
            value = s.Value;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool Bool(Dictionary<string, JsonValue> fields, string name, out bool value)
    {
        if (fields.TryGetValue(name, out var raw) && raw is JsonValue.Bool b)
        {
            value = b.Value;
            return true;
        }

        value = false;
        return false;
    }

    /// <summary>
    /// The mutable accumulator one agent's events fold into. Private, and never handed out: an
    /// <see cref="AgentStanding"/> is what leaves this type, so nothing downstream can hold a
    /// half-folded standing or mutate a projected one.
    /// </summary>
    private sealed class Builder(string agentId)
    {
        public string AgentId { get; } = agentId;

        public List<CredentialTransitionedEvent> Credentials { get; } = [];

        public DateTimeOffset? EnrolledAt { get; set; }

        public bool OwnerVerified { get; set; }

        public int CleanQuestions { get; set; }

        private DateTimeOffset? _countableCriteriaMetAt;

        /// <summary>
        /// Records <paramref name="at"/> as the instant Table 11's two log-derived T1 criteria
        /// first held together. Called after every event that could change either, and first-wins:
        /// re-stamping it would reset the T2 clock every time the agent posted, which is the same
        /// bug the in-memory directory's <c>NoteReachedT1</c> guarded against by hand.
        /// </summary>
        public void NoteCountableCriteria(DateTimeOffset at)
        {
            if (_countableCriteriaMetAt is null
                && OwnerVerified
                && CleanQuestions >= TierPolicy.T1MinimumCleanQuestions)
                _countableCriteriaMetAt = at;
        }

        public AgentStanding Build() => new(
            AgentId,
            [.. Credentials],
            EnrolledAt,
            OwnerVerified,
            CleanQuestions,
            TierPolicy.FirstSatisfiedT1At(EnrolledAt, _countableCriteriaMetAt));
    }
}
