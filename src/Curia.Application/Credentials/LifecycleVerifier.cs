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
/// <para><b>The seam.</b> Until the verifier's real fold is written, <see cref="Skipped"/> is empty (every row is honoured by shape) and <see cref="Refusal"/> carries today's
/// refusal: the fold's error where <c>KeyLifecycle.Of</c> failed, which each consumer returns where it returned that error. The final form has no <see cref="Refusal"/>:
/// no request is refused because of a row's content (ruling 4).</para>
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
/// One log, verified once (errata G68, ruling 19): per identity, <see cref="For"/> decides that identity's stream; <see cref="Skipped()"/> and
/// <see cref="SkippedPast"/> list the rows the verifier does not honour, for the audit, the start-up scan and the head signer's stop.
/// </summary>
public sealed class VerifiedLog
{
    /// <summary>The seam's predicate for a row the stop withholds on today's structural test (<see cref="LifecycleRows"/>); the final verifier names R6.64's predicates instead.</summary>
    internal const string StructuralPredicate = "structural-row";

    private readonly LifecycleVerifier _verifier;
    private readonly IReadOnlyList<AppendedEvent> _log;
    private readonly ImmutableArray<SkippedRow> _skipped = [];
    private readonly Lazy<IReadOnlyDictionary<string, List<AppendedEvent>>> _streams;
    private readonly Dictionary<string, VerifiedLifecycle> _memo = new(StringComparer.Ordinal);

    internal VerifiedLog(LifecycleVerifier verifier, IReadOnlyList<AppendedEvent> log)
    {
        _verifier = verifier;
        _log = log;
        _streams = new Lazy<IReadOnlyDictionary<string, List<AppendedEvent>>>(Streams);
    }

    /// <summary>The verifier this log was verified by.</summary>
    public LifecycleVerifier Verifier => _verifier;

    /// <summary>The log this was made over, whole from its start (R6.47: leaf <i>i</i> is the <i>i</i>th event) or a prefix of it.</summary>
    public IReadOnlyList<AppendedEvent> Log => _log;

    /// <summary>What the verifier decides of <paramref name="agent"/>'s stream. Memoised: asking twice is one decision.</summary>
    public VerifiedLifecycle For(AgentId agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        lock (_memo)
        {
            if (_memo.TryGetValue(agent.Value, out var known)) return known;

            var stream = _streams.Value.TryGetValue(agent.Value, out var own) ? own : [];
            var decided = KeyLifecycle.Of(stream, agent).TryGetValue(out var lifecycle, out var refusal)
                ? new VerifiedLifecycle(lifecycle!, EnrollmentBinding.Find(stream, agent), [])
                : new VerifiedLifecycle(new KeyLifecycle([]), EnrollmentBinding.Find(stream, agent), [], refusal);
            _memo[agent.Value] = decided;
            return decided;
        }
    }

    /// <summary>Every row the verifier does not honour, in log order. Empty on the seam, which honours every row by shape.</summary>
    public ImmutableArray<SkippedRow> Skipped() => _skipped;

    /// <summary>
    /// The rows at or past <paramref name="treeSize"/> the head signer's stop withholds on. On the seam this is today's structural test, every lifecycle row beyond an enrolment
    /// pair (<see cref="LifecycleRows"/>), so that the stop behaves as it did; the final verifier lists the rows it skips.
    /// </summary>
    public ImmutableArray<SkippedRow> SkippedPast(long treeSize) =>
        [.. LifecycleRows.Find(_log)
            .Where(r => r.LogIndex >= treeSize)
            .Select(r => new SkippedRow(r.LogIndex, r.Type.Value, r.Aggregate.Value, StructuralPredicate))];

    private Dictionary<string, List<AppendedEvent>> Streams()
    {
        var streams = new Dictionary<string, List<AppendedEvent>>(StringComparer.Ordinal);
        foreach (var appended in _log)
        {
            if (!KeyLifecycle.IsLifecycleKind(appended.Event.Type.Value)
                && !string.Equals(appended.Event.Type.Value, AgentStandingProjector.EnrolledType, StringComparison.Ordinal))
                continue;

            var key = appended.AggregateId.Value;
            if (!streams.TryGetValue(key, out var stream)) streams[key] = stream = [];
            stream.Add(appended);
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
