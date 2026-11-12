using System.Collections.Immutable;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>A lifecycle row beyond an enrolment pair: where it sits in the sequence given, what type it is, and the stream it belongs to. Never a payload member.</summary>
public readonly record struct LifecycleRow(long LogIndex, EventType Type, AggregateId Aggregate);

/// <summary>
/// The one predicate for "a lifecycle row beyond enrolment pairs" (errata G67, R6.63, Amendment 2 ruling 28): an <c>agent.key-retired</c>, an
/// <c>agent.key-compromised</c>, or an <c>agent.key-bound</c> that is not the second event of a stream whose first is <c>agent.enrolled</c>. Extracted from
/// <c>LifecycleEntryScan</c>, so that the start-up scan and the head signer's stop cannot count differently. A log read cannot see append boundaries, so a lone
/// <c>agent.key-bound</c> appended separately onto a stream whose only event is <c>agent.enrolled</c> is indistinguishable from an enrolment pair and is not
/// counted: the scan's stated undercount, carried over. Pure and incremental: <see cref="Observe"/> takes events in log order, so a caller that pages the log
/// feeds it page by page and a caller that holds the log feeds it whole (<see cref="Find"/>).
/// </summary>
public sealed class LifecycleRows
{
    private readonly Dictionary<string, (long Length, bool BeganEnrolled)> _streams = new(StringComparer.Ordinal);
    private long _next;

    /// <summary>
    /// Takes the next event of the log, in log order. Returns <see langword="true"/> and the row when the event is a lifecycle row beyond an enrolment pair;
    /// the row's <see cref="LifecycleRow.LogIndex"/> is the position of the event among those observed.
    /// </summary>
    public bool Observe(AppendedEvent appended, out LifecycleRow row)
    {
        ArgumentNullException.ThrowIfNull(appended);

        var type = appended.Event.Type.Value;
        var aggregate = appended.AggregateId.Value;
        _streams.TryGetValue(aggregate, out var stream);

        var counted =
            string.Equals(type, LifecycleAppendGuard.KeyRetiredType, StringComparison.Ordinal)
            || string.Equals(type, LifecycleAppendGuard.KeyCompromisedType, StringComparison.Ordinal)
            || (string.Equals(type, AgentStandingProjector.KeyBoundType, StringComparison.Ordinal)
                && !(stream.Length == 1 && stream.BeganEnrolled));

        row = counted ? new LifecycleRow(_next, appended.Event.Type, appended.AggregateId) : default;

        _streams[aggregate] = (
            stream.Length + 1,
            stream.Length == 0
                ? string.Equals(type, AgentStandingProjector.EnrolledType, StringComparison.Ordinal)
                : stream.BeganEnrolled);
        _next++;
        return counted;
    }

    /// <summary>Every lifecycle row beyond an enrolment pair in <paramref name="log"/>, in order, with its position in the sequence given.</summary>
    public static ImmutableArray<LifecycleRow> Find(IReadOnlyList<AppendedEvent> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var rows = new LifecycleRows();
        var found = ImmutableArray.CreateBuilder<LifecycleRow>();
        foreach (var appended in log)
            if (rows.Observe(appended, out var row))
                found.Add(row);

        return found.ToImmutable();
    }
}
