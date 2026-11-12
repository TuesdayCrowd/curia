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
///
/// <para><see cref="Refusal"/> is the seam's and is always null now: no request is refused because of a row's content (ruling 4). Task 4 deletes it.</para>
/// </summary>
/// <param name="Honoured">The honoured entries in stream order; empty when <paramref name="Refusal"/> is set.</param>
/// <param name="Binding">What the honoured bindings bind; null when the log records no enrolment of the identity.</param>
/// <param name="Skipped">The rows the verifier does not honour, ascending by log index.</param>
/// <param name="Refusal">The seam's refusal; null when the fold read the stream.</param>
public sealed record VerifiedLifecycle(
    KeyLifecycle Honoured,
    EnrollmentBinding? Binding,
    ImmutableArray<SkippedRow> Skipped,
    Error? Refusal = null);

/// <summary>
/// One log, verified once (errata G68, ruling 19): per identity, <see cref="For"/> decides that identity's stream by R6.64; <see cref="Skipped()"/> and
/// <see cref="SkippedPast"/> list the rows the verifier does not honour, for the audit, the start-up scan and the head signer's stop.
/// </summary>
public sealed class VerifiedLog
{
    private readonly LifecycleVerifier _verifier;
    private readonly IReadOnlyList<AppendedEvent> _log;
    private readonly Lazy<IReadOnlyDictionary<string, List<(long Index, AppendedEvent Event)>>> _streams;
    private readonly Lazy<LifecycleFold.KeyWorld> _world;
    private readonly Dictionary<string, VerifiedLifecycle> _memo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LifecycleFold.Decided> _decided = new(StringComparer.Ordinal);

    internal VerifiedLog(LifecycleVerifier verifier, IReadOnlyList<AppendedEvent> log)
    {
        _verifier = verifier;
        _log = log;
        _streams = new Lazy<IReadOnlyDictionary<string, List<(long Index, AppendedEvent Event)>>>(Streams);
        _world = new Lazy<LifecycleFold.KeyWorld>(() => new LifecycleFold.KeyWorld(PublishedKeys.OfLog(log)));
    }

    /// <summary>The verifier this log was verified by.</summary>
    public LifecycleVerifier Verifier => _verifier;

    /// <summary>The log this was made over, whole from its start (R6.47: leaf <i>i</i> is the <i>i</i>th event) or a prefix of it.</summary>
    public IReadOnlyList<AppendedEvent> Log => _log;

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

    /// <summary>The rows at or past <paramref name="treeSize"/> that the verifier does not honour: what the head signer's stop withholds on (R6.63).</summary>
    public ImmutableArray<SkippedRow> SkippedPast(long treeSize) => [.. Skipped().Where(r => r.LogIndex >= treeSize)];

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
