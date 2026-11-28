using System.Collections.Immutable;
using Curia.Application.Projections;
using Curia.Application.Reads;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Credentials;

/// <summary>
/// A lifecycle row the verifier does not honour (errata G68, R6.64): where it sits in the log, its type, the stream it belongs to and the predicate that decided it.
/// Never a payload member (R10.27, R10.28: a logger that serialises it logs no secret).
/// </summary>
/// <param name="LogIndex">The row's index in the log (R6.47).</param>
/// <param name="Type">The row's event type.</param>
/// <param name="Aggregate">The stream the row was appended to.</param>
/// <param name="Predicate">The slug of the predicate that skipped it (R6.64's list).</param>
public sealed record SkippedRow(long LogIndex, string Type, string Aggregate, string Predicate);

/// <summary>
/// What the Forum acts on of one identity's lifecycle (errata G68, ruling 19): the honoured entries, the binding the honoured bindings make, the rows skipped.
/// No request is refused because of a row's content (ruling 4), so there is no refusal here: a row is honoured or it is in <paramref name="Skipped"/>.
/// </summary>
/// <param name="Honoured">The honoured entries in stream order.</param>
/// <param name="Binding">What the honoured bindings bind; null when the log records no enrolment of the identity.</param>
/// <param name="Skipped">The rows the verifier does not honour, ascending by log index.</param>
public sealed record VerifiedLifecycle(
    KeyLifecycle Honoured,
    EnrollmentBinding? Binding,
    ImmutableArray<SkippedRow> Skipped);

/// <summary>
/// One log, verified once (errata G68, ruling 19): per identity, <see cref="For"/> decides that identity's stream by R6.64; <see cref="Skipped()"/> and
/// <see cref="SkippedPast"/> list the rows the verifier does not honour, for the audit, the start-up scan and the head signer's stop.
///
/// <para><b>Whole, or over a read (errata G87, R11.113).</b> A log made by <see cref="LifecycleVerifier.OfLog(IReadOnlyList{AppendedEvent})"/> is the whole log and answers every question below. A log
/// made by <see cref="LifecycleVerifier.OfSlice"/> is over the rows one read plan returned, each with its own log index, and answers only what the read was made for:
/// <see cref="For"/> of an identity whose stream was read whole (with the three published-key types), <see cref="StandingOf"/>, <see cref="PostureStandingOf"/>, <see cref="ModerationOf"/> and
/// <see cref="PositionOf"/>. Every other question (every identity's standing, the whole log, the audit's counts) throws <see cref="OutOfScopeException"/>, never an empty answer.</para>
/// </summary>
public sealed class VerifiedLog
{
    private readonly LifecycleVerifier _verifier;
    private readonly IReadOnlyList<AppendedEvent>? _log;
    private readonly IReadOnlyList<PositionedEvent> _rows;
    private readonly LogSlice? _slice;
    private readonly Lazy<IReadOnlyDictionary<string, List<(long Index, AppendedEvent Event)>>> _streams;
    private readonly Lazy<PublishedKeys> _published;
    private readonly Lazy<LifecycleFold.KeyWorld> _world;
    private readonly Lazy<ImmutableArray<ActDecision>> _acts;
    private readonly Lazy<HonouredEvents> _honoured;
    private readonly Lazy<ImmutableDictionary<string, AgentStanding>> _standings;
    private readonly Lazy<ImmutableDictionary<string, PostModeration>> _moderation;
    private readonly Lazy<Dictionary<string, long>> _positions;
    private readonly Dictionary<string, VerifiedLifecycle> _memo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LifecycleFold.Decided> _decided = new(StringComparer.Ordinal);

    internal VerifiedLog(LifecycleVerifier verifier, IReadOnlyList<AppendedEvent> log)
        : this(verifier, log, null)
    {
    }

    /// <summary>The log over the rows of <paramref name="slice"/>: it decides what the slice was read for and refuses the rest (<see cref="OutOfScopeException"/>).</summary>
    internal VerifiedLog(LifecycleVerifier verifier, LogSlice slice)
    {
        _verifier = verifier;
        _slice = slice;
        _rows = slice.Rows;
        _streams = new Lazy<IReadOnlyDictionary<string, List<(long Index, AppendedEvent Event)>>>(Streams);

        // The three key types are decided only if the read named them: a lifecycle decision over a key list that was not read would decide "unpublished" for every key.
        _published = new Lazy<PublishedKeys>(() => slice.Scope.PublishedKeys || slice.Scope.IsWhole
            ? PublishedKeys.From(PublishedKeys.Decide(_rows))
            : throw new OutOfScopeException("the published keys"));
        _world = new Lazy<LifecycleFold.KeyWorld>(() => new LifecycleFold.KeyWorld(_published.Value));

        // The operator acts of the streams the read holds whole (R6.67): a stream read in part would be decided from the wrong first event.
        _acts = new Lazy<ImmutableArray<ActDecision>>(() => OperatorActFold.Decide(
            [.. _rows.Where(r => slice.Scope.CoversAggregate(r.Event.AggregateId.Value))], _published.Value, verifier.Verifiers));
        _honoured = new Lazy<HonouredEvents>(BuildHonouredOfSlice);
        _standings = new Lazy<ImmutableDictionary<string, AgentStanding>>(() => throw new OutOfScopeException("every agent's standing"));
        _moderation = new Lazy<ImmutableDictionary<string, PostModeration>>(() => throw new OutOfScopeException("every post's moderation"));
        _positions = new Lazy<Dictionary<string, long>>(PositionsOfRows);
    }

    /// <summary>The log over <paramref name="log"/>, whose act decisions for the rows <paramref name="prior"/> was made over are prior's (nothing of them verified again) and for the rows beyond it are made now.</summary>
    private VerifiedLog(LifecycleVerifier verifier, IReadOnlyList<AppendedEvent> log, VerifiedLog? prior)
    {
        _verifier = verifier;
        _log = log;
        _rows = new PositionedLog(log);
        _streams = new Lazy<IReadOnlyDictionary<string, List<(long Index, AppendedEvent Event)>>>(Streams);
        _published = new Lazy<PublishedKeys>(() => PublishedKeys.OfLog(log));
        _world = new Lazy<LifecycleFold.KeyWorld>(() => new LifecycleFold.KeyWorld(_published.Value));

        // The operator acts (R6.67) are decided once per VerifiedLog, whichever of the three views asks first, under Lazy's own lock: each signature is verified at most once.
        _acts = new Lazy<ImmutableArray<ActDecision>>(() => prior is null
            ? OperatorActFold.Decide(log, _published.Value, verifier.Verifiers)
            : OperatorActFold.Extend(prior._acts.Value, prior._log!.Count, log, _published.Value, verifier.Verifiers));
        _honoured = new Lazy<HonouredEvents>(BuildHonoured);
        _standings = new Lazy<ImmutableDictionary<string, AgentStanding>>(() => AgentStandingProjector.Fold(_honoured.Value.Events));
        _moderation = new Lazy<ImmutableDictionary<string, PostModeration>>(() => FlagProjector.Fold(_honoured.Value.Events));
        _positions = new Lazy<Dictionary<string, long>>(PositionsOfRows);
    }

    /// <summary>The verifier this log was verified by.</summary>
    public LifecycleVerifier Verifier => _verifier;

    /// <summary>The log this was made over, whole from its start (R6.47: leaf <i>i</i> is the <i>i</i>th event) or a prefix of it. A log made over a read has none, and says so.</summary>
    public IReadOnlyList<AppendedEvent> Log => _log ?? throw new OutOfScopeException("the whole log");

    /// <summary>The rows this log was made over, each with its log index: the whole log's, or the read's.</summary>
    public IReadOnlyList<PositionedEvent> Rows => _rows;

    /// <summary>
    /// The log index of the row whose event id is <paramref name="eventId"/> (R6.47), or null when the log holds no such row. For a log made over a read, "holds" is "was read": the callers ask only
    /// for rows their read named.
    /// </summary>
    public long? PositionOf(string eventId)
    {
        ArgumentNullException.ThrowIfNull(eventId);

        return _positions.Value.TryGetValue(eventId, out var index) ? index : null;
    }

    /// <summary>
    /// This log made over <paramref name="longer"/>, a later read of the same log (register item 286, errata G71, R9.65): the act decisions for the rows this log was made over are this log's and nothing of them
    /// is verified again; the rows <paramref name="longer"/> added are decided now. The operator-act fold is forward (a row's decision reads the rows before it only), so the decisions of a prefix are the prefix of
    /// the decisions. It returns this log when <paramref name="longer"/> is as long as this log and begins with it, and a whole <see cref="LifecycleVerifier.OfLog"/> of <paramref name="longer"/> when it does not
    /// begin with this log (it is shorter, or a <c>Seq</c> differs at an index this log holds): a prefix is never trusted without the comparison. The lifecycle decisions (<see cref="For"/>) are not carried over.
    /// </summary>
    public VerifiedLog ExtendTo(IReadOnlyList<AppendedEvent> longer)
    {
        ArgumentNullException.ThrowIfNull(longer);

        var held = Log;
        if (longer.Count < held.Count || !BeginsWithThisLog(longer)) return _verifier.OfLog(longer);
        if (longer.Count == held.Count) return this;
        return new VerifiedLog(_verifier, longer, this);
    }

    private bool BeginsWithThisLog(IReadOnlyList<AppendedEvent> longer)
    {
        var held = Log;
        if (ReferenceEquals(longer, held)) return true;
        for (var i = 0; i < held.Count; i++)
            if (longer[i].Seq.Value != held[i].Seq.Value)
                return false;
        return true;
    }

    /// <summary>What the verifier decides of <paramref name="agent"/>'s stream (R6.64). Memoised: asking twice is one decision.</summary>
    public VerifiedLifecycle For(AgentId agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        lock (_memo)
        {
            if (_memo.TryGetValue(agent.Value, out var known)) return known;

            var decided = Decided(agent.Value);
            var lifecycle = KeyLifecycle.Of(decided.Honoured, agent);

            // Every event the fold honoured passed the checks the old reader makes and more, so the old reader reads it; a failure here is a defect in this assembly and is said as one.
            var verified = new VerifiedLifecycle(
                lifecycle.TryGetValue(out var honoured, out var unreadable) ? honoured! : throw new InvalidOperationException($"the honoured rows of a stream do not read as a lifecycle: {unreadable!.Type}"),
                EnrollmentBinding.Find(decided.Honoured, agent),
                decided.Skipped);
            _memo[agent.Value] = verified;
            return verified;
        }
    }

    /// <summary>Every row the verifier does not honour, in log order: the rows of every stream, each by the predicate that skipped it.</summary>
    public ImmutableArray<SkippedRow> Skipped() =>
        [.. WholeOnly(nameof(Skipped))._streams.Value.Keys.SelectMany(aggregate => Decided(aggregate).Skipped).OrderBy(r => r.LogIndex)];

    /// <summary>
    /// The log less the operator-act rows the verifier does not honour (R4.43, R6.67) and the <c>agent.enrolled</c> rows that are not their stream's honoured root (R4.48), each event with its own <c>Seq</c>: what
    /// the raw projectors are fed. Computed once; asking again verifies nothing. It is not a log; the rows it leaves out are listed by <see cref="Skipped()"/> (enrolments) and <see cref="ActSkipped"/> (acts).
    /// </summary>
    public HonouredEvents HonouredEvents => _honoured.Value;

    /// <summary>Every agent's standing, folded from the honoured events (R4.43): an attestation the verifier skips binds no owner and sets no flag. Once per log; with <see cref="Moderation"/>, the only callers of the raw folds.</summary>
    public ImmutableDictionary<string, AgentStanding> Standings() => _standings.Value;

    /// <summary>Every moderated post's history, folded from the honoured events (R10.60, R10.61): a record the verifier skips holds nothing and releases nothing. Once per log.</summary>
    public ImmutableDictionary<string, PostModeration> Moderation() => _moderation.Value;

    /// <summary>
    /// One agent's standing, folded from the honoured events of that agent's own stream (R4.43): an attestation the verifier skips binds no owner and sets no flag. Null when that stream holds no honoured event
    /// that makes a standing. The acts are decided once per log, whichever view asks first, so asking for several agents verifies nothing again. With <see cref="Standings"/> and <see cref="Moderation"/>, the
    /// only callers of the raw folds (OAS12). For a log made over a read, the agent's stream must have been read whole.
    /// </summary>
    public AgentStanding? StandingOf(AgentId agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        RequireAggregate(agent.Value);

        var own = _honoured.Value.Events.Where(e => string.Equals(e.AggregateId.Value, agent.Value, StringComparison.Ordinal)).ToArray();
        return AgentStandingProjector.Fold(own).GetValueOrDefault(agent.Value);
    }

    /// <summary>
    /// The standing Table 11's facts are folded from, for <paramref name="agent"/>, from a log made over a read that holds what the fold reads for one agent: its stream, its posts, the streams of those
    /// posts (their moderation) and the streams of the threads its answers belong to (their acceptances). It is the whole-log fold's entry for the agent, restricted to the rows that bear on it, and
    /// refuses (<see cref="OutOfScopeException"/>) a read that lacks any of them.
    /// </summary>
    public AgentStanding? PostureStandingOf(AgentId agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var scope = SliceScope();
        RequireAggregate(agent.Value);
        if (!scope.CoversPostsBy(agent.Value)) throw new OutOfScopeException("the agent's posts");

        // Every post the agent wrote has its stream read (its moderation), and every answer's thread root has its stream read (its acceptances).
        foreach (var row in _rows)
        {
            if (row.Event.Event.Type.Value != PostProjector.PostAcceptedType
                || !string.Equals(row.Event.Event.Actor?.Value, agent.Value, StringComparison.Ordinal))
                continue;

            if (!scope.CoversAggregate(row.Event.AggregateId.Value)) throw new OutOfScopeException("the stream of the agent's post");
            if (row.Event.Event.Payload is JsonValue.Object payload
                && payload.Members.FirstOrDefault(m => m.Key == "kind").Value is JsonValue.String { Value: "answer" }
                && payload.Members.FirstOrDefault(m => m.Key == "parent").Value is JsonValue.String parent
                && !scope.CoversAggregate(parent.Value))
                throw new OutOfScopeException("the thread an answer belongs to");
        }

        return AgentStandingProjector.Fold(_honoured.Value.Events.ToArray()).GetValueOrDefault(agent.Value);
    }

    /// <summary>
    /// The moderation history of the post <paramref name="postId"/>, folded from the honoured events of its own stream (R10.60: a record is honoured only in the stream of the post it names); null when the stream
    /// holds none. For a log made over a read, the post's stream must have been read whole.
    /// </summary>
    public PostModeration? ModerationOf(string postId)
    {
        ArgumentNullException.ThrowIfNull(postId);
        RequireAggregate(postId);

        var own = _honoured.Value.Events.Where(e => string.Equals(e.AggregateId.Value, postId, StringComparison.Ordinal)).ToArray();
        return FlagProjector.Fold(own).GetValueOrDefault(postId);
    }

    /// <summary>The operator-act rows the verifier does not honour (R6.67), ascending by log index, each by the predicate that skipped it (never a payload member).</summary>
    public ImmutableArray<SkippedRow> ActSkipped() =>
        [.. WholeOnly(nameof(ActSkipped))._acts.Value.Where(d => d.Predicate is not null).Select(d => new SkippedRow(d.LogIndex, d.Type, d.Aggregate, d.Predicate!))];

    /// <summary>Every row the verifier does not honour, lifecycle rows and operator-act rows together, ascending by log index.</summary>
    public ImmutableArray<SkippedRow> AllSkipped() => [.. Skipped().Concat(ActSkipped()).OrderBy(r => r.LogIndex)];

    private HonouredEvents BuildHonoured()
    {
        var log = _log!;
        var skipped = _acts.Value.Where(d => d.Predicate is not null).Select(d => d.LogIndex).ToHashSet();

        // R4.48: the standing reads an agent.enrolled row only if the lifecycle fold honours it as its stream's root, by the one function both ask (a per-stream seen-set; no signature work).
        var seenRoot = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < log.Count; i++)
        {
            var row = log[i];
            if (!string.Equals(row.Event.Type.Value, AgentStandingProjector.EnrolledType, StringComparison.Ordinal)) continue;
            var aggregate = row.AggregateId.Value;
            if (EnrolmentRow.Predicate(row, aggregate, seenRoot.Add(aggregate)) is not null) skipped.Add(i);
        }

        return new HonouredEvents([.. log.Where((_, i) => !skipped.Contains(i))], log);
    }

    /// <summary>
    /// The honoured events of a read: the rows less the act rows and enrolment roots the verifier does not honour, decided as the whole log decides them (errata G87). A row whose decision needs a stream
    /// the read does not hold whole is not decided from a part of it: the read was not made for it, and the question fails.
    /// </summary>
    private HonouredEvents BuildHonouredOfSlice()
    {
        var scope = _slice!.Scope;
        var skipped = _acts.Value.Where(d => d.Predicate is not null).Select(d => d.LogIndex).ToHashSet();
        var decidedActs = _acts.Value.Select(d => d.LogIndex).ToHashSet();
        var seenRoot = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            var type = row.Event.Event.Type.Value;
            var isAct = OperatorActFold.IsActType(type);
            var isRoot = type == AgentStandingProjector.EnrolledType;
            if (!isAct && !isRoot) continue;

            var aggregate = row.Event.AggregateId.Value;
            if (!scope.CoversAggregate(aggregate)) throw new OutOfScopeException("the stream of an act or an enrolment the read holds in part");
            if (isAct && !decidedActs.Contains(row.LogIndex)) throw new OutOfScopeException("an act row the read did not decide");
            if (isRoot && EnrolmentRow.Predicate(row.Event, aggregate, seenRoot.Add(aggregate)) is not null) skipped.Add(row.LogIndex);
        }

        return new HonouredEvents([.. _rows.Where(r => !skipped.Contains(r.LogIndex)).Select(r => r.Event)], null);
    }

    /// <summary>How many lifecycle rows the verifier honours, over every stream: the audit's first number (errata G68, ruling 13).</summary>
    public long HonouredCount() => WholeOnly(nameof(HonouredCount))._streams.Value.Keys.Sum(aggregate => (long)Decided(aggregate).Honoured.Length);

    /// <summary>The rows at or past <paramref name="treeSize"/> that the verifier does not honour: what the head signer's stop withholds on (R6.63).</summary>
    public ImmutableArray<SkippedRow> SkippedPast(long treeSize) => [.. Skipped().Where(r => r.LogIndex >= treeSize)];

    /// <summary>How many rows the verifier honours of both kinds: the lifecycle rows it honours and the operator-act rows it honours (the audit's first number).</summary>
    public long AllHonouredCount() => HonouredCount() + _acts.Value.Count(d => d.Predicate is null);

    /// <summary>The rows at or past <paramref name="treeSize"/> that the verifier does not honour, lifecycle rows and operator-act rows together: what the head signer's stop reads (R6.63, R11.97).</summary>
    public ImmutableArray<SkippedRow> AllSkippedPast(long treeSize) => [.. AllSkipped().Where(r => r.LogIndex >= treeSize)];

    /// <summary>
    /// The stream's decision, once. The consumer-facing types are read from the honoured events alone, by the two readers of a lifecycle's shape that nothing else calls
    /// (<c>KeyLifecycle.Of</c>, <c>EnrollmentBinding.Find</c>): a row the verifier skips is not among the events they are given.
    /// </summary>
    private LifecycleFold.Decided Decided(string aggregate)
    {
        lock (_memo)
        {
            if (_decided.TryGetValue(aggregate, out var known)) return known;

            RequireAggregate(aggregate);
            var stream = _streams.Value.TryGetValue(aggregate, out var own) ? own : [];
            var decided = LifecycleFold.Of(aggregate, stream, _world.Value, _verifier.Verifiers);
            _decided[aggregate] = decided;
            return decided;
        }
    }

    private VerifiedLog WholeOnly(string question) => _slice is null ? this : throw new OutOfScopeException("every stream (" + question + ")");

    private ReadScope SliceScope() => _slice?.Scope ?? ReadScope.Whole;

    /// <summary>A question about <paramref name="aggregate"/> is answered only if every row of it was read.</summary>
    private void RequireAggregate(string aggregate)
    {
        if (_slice is not null && !_slice.Scope.CoversAggregate(aggregate))
            throw new OutOfScopeException("the stream of " + aggregate);
    }

    private Dictionary<string, long> PositionsOfRows()
    {
        var positions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var row in _rows)
            positions[row.Event.Event.Id.Value] = row.LogIndex;
        return positions;
    }

    private Dictionary<string, List<(long Index, AppendedEvent Event)>> Streams()
    {
        var streams = new Dictionary<string, List<(long Index, AppendedEvent Event)>>(StringComparer.Ordinal);
        foreach (var row in _rows)
        {
            var appended = row.Event;
            if (!KeyLifecycle.IsLifecycleKind(appended.Event.Type.Value)
                && !string.Equals(appended.Event.Type.Value, AgentStandingProjector.EnrolledType, StringComparison.Ordinal))
                continue;

            var key = appended.AggregateId.Value;
            if (!streams.TryGetValue(key, out var stream)) streams[key] = stream = [];
            stream.Add((row.LogIndex, appended));
        }

        return streams;
    }
}

/// <summary>
/// The one reader of an identity's lifecycle (errata G68, R6.64, ruling 2): every consumer asks it, and <c>KeyLifecycle.Of</c> and <c>EnrollmentBinding.Find</c> are
/// closed behind it. It takes the crypto primitives by algorithm, as the writers do.
/// </summary>
public sealed class LifecycleVerifier
{
    private readonly IReadOnlyDictionary<string, IContentVerifier> _verifiers;

    public LifecycleVerifier(IReadOnlyDictionary<string, IContentVerifier> verifiers)
    {
        ArgumentNullException.ThrowIfNull(verifiers);

        _verifiers = verifiers;
    }

    /// <summary>The primitives the verifier checks signatures with, by algorithm.</summary>
    internal IReadOnlyDictionary<string, IContentVerifier> Verifiers => _verifiers;

    /// <summary>
    /// Verifies <paramref name="log"/>, which is the whole log from its start (R6.47) or a prefix of it: a log index is a position in it. No overload takes a per-aggregate read.
    /// </summary>
    public VerifiedLog OfLog(IReadOnlyList<AppendedEvent> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        return new VerifiedLog(this, log);
    }

    /// <summary>
    /// Verifies the rows of <paramref name="slice"/>, which one read plan returned with each row's own log index (errata G87, R11.113). The result decides what the read was made for and refuses the
    /// rest: it is how a route decides from the rows it reads and not from the log.
    /// </summary>
    public VerifiedLog OfSlice(LogSlice slice)
    {
        ArgumentNullException.ThrowIfNull(slice);

        return new VerifiedLog(this, slice);
    }
}
