using System.Collections.Immutable;
using Curia.Application.Projections;
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
/// </summary>
public sealed class VerifiedLog
{
    private readonly LifecycleVerifier _verifier;
    private readonly IReadOnlyList<AppendedEvent> _log;
    private readonly Lazy<IReadOnlyDictionary<string, List<(long Index, AppendedEvent Event)>>> _streams;
    private readonly Lazy<PublishedKeys> _published;
    private readonly Lazy<LifecycleFold.KeyWorld> _world;
    private readonly Lazy<ImmutableArray<ActDecision>> _acts;
    private readonly Lazy<HonouredEvents> _honoured;
    private readonly Lazy<ImmutableDictionary<string, AgentStanding>> _standings;
    private readonly Lazy<ImmutableDictionary<string, PostModeration>> _moderation;
    private readonly Dictionary<string, VerifiedLifecycle> _memo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LifecycleFold.Decided> _decided = new(StringComparer.Ordinal);

    internal VerifiedLog(LifecycleVerifier verifier, IReadOnlyList<AppendedEvent> log)
        : this(verifier, log, null)
    {
    }

    /// <summary>The log over <paramref name="log"/>, whose act decisions for the rows <paramref name="prior"/> was made over are prior's (nothing of them verified again) and for the rows beyond it are made now.</summary>
    private VerifiedLog(LifecycleVerifier verifier, IReadOnlyList<AppendedEvent> log, VerifiedLog? prior)
    {
        _verifier = verifier;
        _log = log;
        _streams = new Lazy<IReadOnlyDictionary<string, List<(long Index, AppendedEvent Event)>>>(Streams);
        _published = new Lazy<PublishedKeys>(() => PublishedKeys.OfLog(log));
        _world = new Lazy<LifecycleFold.KeyWorld>(() => new LifecycleFold.KeyWorld(_published.Value));

        // The operator acts (R6.67) are decided once per VerifiedLog, whichever of the three views asks first, under Lazy's own lock: each signature is verified at most once.
        _acts = new Lazy<ImmutableArray<ActDecision>>(() => prior is null
            ? OperatorActFold.Decide(log, _published.Value, verifier.Verifiers)
            : OperatorActFold.Extend(prior._acts.Value, prior._log.Count, log, _published.Value, verifier.Verifiers));
        _honoured = new Lazy<HonouredEvents>(BuildHonoured);
        _standings = new Lazy<ImmutableDictionary<string, AgentStanding>>(() => AgentStandingProjector.Fold(_honoured.Value.Events));
        _moderation = new Lazy<ImmutableDictionary<string, PostModeration>>(() => FlagProjector.Fold(_honoured.Value.Events));
    }

    /// <summary>The verifier this log was verified by.</summary>
    public LifecycleVerifier Verifier => _verifier;

    /// <summary>The log this was made over, whole from its start (R6.47: leaf <i>i</i> is the <i>i</i>th event) or a prefix of it.</summary>
    public IReadOnlyList<AppendedEvent> Log => _log;

    /// <summary>
    /// This log made over <paramref name="longer"/>, a later read of the same log (register item 286, errata G71, R9.65): the act decisions for the rows this log was made over are this log's and nothing of them
    /// is verified again; the rows <paramref name="longer"/> added are decided now. The operator-act fold is forward (a row's decision reads the rows before it only), so the decisions of a prefix are the prefix of
    /// the decisions. It returns this log when <paramref name="longer"/> is as long as this log and begins with it, and a whole <see cref="LifecycleVerifier.OfLog"/> of <paramref name="longer"/> when it does not
    /// begin with this log (it is shorter, or a <c>Seq</c> differs at an index this log holds): a prefix is never trusted without the comparison. The lifecycle decisions (<see cref="For"/>) are not carried over.
    /// </summary>
    public VerifiedLog ExtendTo(IReadOnlyList<AppendedEvent> longer)
    {
        ArgumentNullException.ThrowIfNull(longer);

        if (longer.Count < _log.Count || !BeginsWithThisLog(longer)) return _verifier.OfLog(longer);
        if (longer.Count == _log.Count) return this;
        return new VerifiedLog(_verifier, longer, this);
    }

    private bool BeginsWithThisLog(IReadOnlyList<AppendedEvent> longer)
    {
        if (ReferenceEquals(longer, _log)) return true;
        for (var i = 0; i < _log.Count; i++)
            if (longer[i].Seq.Value != _log[i].Seq.Value)
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
        [.. _streams.Value.Keys.SelectMany(aggregate => Decided(aggregate).Skipped).OrderBy(r => r.LogIndex)];

    /// <summary>
    /// The log less the operator-act rows the verifier does not honour (R4.43, R6.67), each event with its own <c>Seq</c>: what the raw projectors are fed. Computed once; asking again verifies nothing.
    /// It is not a log, and <see cref="Skipped()"/> does not list the rows it leaves out (<see cref="ActSkipped"/> does).
    /// </summary>
    public HonouredEvents HonouredEvents => _honoured.Value;

    /// <summary>Every agent's standing, folded from the honoured events (R4.43): an attestation the verifier skips binds no owner and sets no flag. Once per log; with <see cref="Moderation"/>, the only callers of the raw folds.</summary>
    public ImmutableDictionary<string, AgentStanding> Standings() => _standings.Value;

    /// <summary>Every moderated post's history, folded from the honoured events (R10.60, R10.61): a record the verifier skips holds nothing and releases nothing. Once per log.</summary>
    public ImmutableDictionary<string, PostModeration> Moderation() => _moderation.Value;

    /// <summary>
    /// One agent's standing, folded from the honoured events of that agent's own stream (R4.43): an attestation the verifier skips binds no owner and sets no flag. Null when that stream holds no honoured event
    /// that makes a standing. The acts are decided once per log, whichever view asks first, so asking for several agents verifies nothing again. With <see cref="Standings"/> and <see cref="Moderation"/>, the
    /// only callers of the raw folds (OAS12).
    /// </summary>
    public AgentStanding? StandingOf(AgentId agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var own = _honoured.Value.Events.Where(e => string.Equals(e.AggregateId.Value, agent.Value, StringComparison.Ordinal)).ToArray();
        return AgentStandingProjector.Fold(own).GetValueOrDefault(agent.Value);
    }

    /// <summary>The operator-act rows the verifier does not honour (R6.67), ascending by log index, each by the predicate that skipped it (never a payload member).</summary>
    public ImmutableArray<SkippedRow> ActSkipped() =>
        [.. _acts.Value.Where(d => d.Predicate is not null).Select(d => new SkippedRow(d.LogIndex, d.Type, d.Aggregate, d.Predicate!))];

    /// <summary>Every row the verifier does not honour, lifecycle rows and operator-act rows together, ascending by log index.</summary>
    public ImmutableArray<SkippedRow> AllSkipped() => [.. Skipped().Concat(ActSkipped()).OrderBy(r => r.LogIndex)];

    private HonouredEvents BuildHonoured()
    {
        var skipped = _acts.Value.Where(d => d.Predicate is not null).Select(d => d.LogIndex).ToHashSet();
        return new HonouredEvents([.. _log.Where((_, i) => !skipped.Contains(i))], _log);
    }

    /// <summary>How many lifecycle rows the verifier honours, over every stream: the audit's first number (errata G68, ruling 13).</summary>
    public long HonouredCount() => _streams.Value.Keys.Sum(aggregate => (long)Decided(aggregate).Honoured.Length);

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

            var stream = _streams.Value.TryGetValue(aggregate, out var own) ? own : [];
            var decided = LifecycleFold.Of(aggregate, stream, _world.Value, _verifier.Verifiers);
            _decided[aggregate] = decided;
            return decided;
        }
    }

    private Dictionary<string, List<(long Index, AppendedEvent Event)>> Streams()
    {
        var streams = new Dictionary<string, List<(long Index, AppendedEvent Event)>>(StringComparer.Ordinal);
        for (var i = 0; i < _log.Count; i++)
        {
            var appended = _log[i];
            if (!KeyLifecycle.IsLifecycleKind(appended.Event.Type.Value)
                && !string.Equals(appended.Event.Type.Value, AgentStandingProjector.EnrolledType, StringComparison.Ordinal))
                continue;

            var key = appended.AggregateId.Value;
            if (!streams.TryGetValue(key, out var stream)) streams[key] = stream = [];
            stream.Add((i, appended));
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
}
