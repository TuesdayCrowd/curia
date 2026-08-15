using Curia.Application.Ports;
using Curia.Domain;

namespace Curia.Application.Projections;

/// <summary>
/// Builds <see cref="AggregateSummary"/> read models purely from the event stream -- the
/// mechanism R11.9 ("all read models SHALL be rebuildable from [the event table] by replay")
/// and R11.10 ("projections SHALL be rebuildable independently") require be exercised, not
/// assumed.
///
/// Neither member here accepts a <see cref="TimeProvider"/>. That is deliberate, not an
/// oversight: the one trap that would quietly turn the replay-rebuild drill into a tautology is
/// a rebuild that reads "now" from somewhere -- two runs would then differ only because they
/// happened at different times, and a test asserting they agree would pass only by the accident
/// of running close together (CS-9's whole point is to make this a structural impossibility
/// rather than a discipline). The only instant that can appear in an
/// <see cref="AggregateSummary"/> is <see cref="AppendedEvent.ServerTimestamp"/> -- data the
/// store already recorded at append time -- never a fresh clock read taken during replay.
///
/// Also never derives <see cref="AggregateSummary.LastServerTimestamp"/> by comparing timestamp
/// values (e.g. "the maximum <c>server_ts</c> seen for this aggregate"): every event in one
/// <c>AppendAsync</c> batch legitimately shares a single <c>server_ts</c> (CS-9: one clock read
/// per batch), and nothing stops a later batch's clock read from being earlier than an earlier
/// batch's (a backward wall-clock adjustment between calls is a real, if rare, possibility this
/// solution does not otherwise rule out). "Most recent" is instead always defined by <c>seq</c>
/// -- the one total order the event table actually offers, per R11.9's own framing -- so a
/// <c>server_ts</c> tie, or even a <c>server_ts</c> that runs backward relative to <c>seq</c>,
/// never changes which event's timestamp the summary reports.
/// </summary>
public static class AggregateSummaryProjector
{
    /// <summary>
    /// Folds an already seq-ordered event list into one <see cref="AggregateSummary"/> per
    /// aggregate. Requires strictly non-decreasing <c>seq</c> order and throws otherwise: every
    /// <see cref="IEventReader"/> this solution ships already returns events in ascending
    /// <c>seq</c> order (each adapter's own contract), so a violation here means a caller handed
    /// this method something that did not really come from a store's forward scan -- a bug or an
    /// infrastructure fault, not a modeled domain outcome an external caller can trigger, so this
    /// follows CS-10's "exceptions are reserved for bugs and infrastructure faults" rather than
    /// returning a <c>Result&lt;T&gt;</c>.
    /// </summary>
    public static IReadOnlyDictionary<AggregateId, AggregateSummary> Fold(IReadOnlyList<AppendedEvent> eventsInSeqOrder)
    {
        ArgumentNullException.ThrowIfNull(eventsInSeqOrder);

        var summaries = new Dictionary<AggregateId, AggregateSummary>();
        var lastSeqSeen = long.MinValue;

        foreach (var appended in eventsInSeqOrder)
        {
            EnsureAscending(lastSeqSeen, appended.Seq.Value, $"{nameof(AggregateSummaryProjector)}.{nameof(Fold)}");
            lastSeqSeen = appended.Seq.Value;
            Apply(summaries, appended);
        }

        return summaries;
    }

    /// <summary>
    /// Replays the whole event table through <see cref="IEventReader.ReadForwardAsync"/> in
    /// pages of <paramref name="pageSize"/>, folding each page as it arrives rather than
    /// materializing the entire stream in memory first -- the shape a real rebuild against a
    /// corpus too large to hold at once would need, and a genuine exercise of the port's
    /// forward-scan cursor (<c>afterSeq</c>), not just of a single unpaged call. Ordering is
    /// checked across page boundaries too (<c>seq</c> is one global counter, not one per page),
    /// via the same guard <see cref="Fold"/> uses.
    /// </summary>
    public static async Task<IReadOnlyDictionary<AggregateId, AggregateSummary>> RebuildAsync(
        IEventReader reader,
        int pageSize = 500,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (pageSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(pageSize), pageSize, "pageSize must be positive.");

        var summaries = new Dictionary<AggregateId, AggregateSummary>();
        var afterSeq = EventSequence.Zero;
        var lastSeqSeen = afterSeq.Value;

        while (true)
        {
            var page = (await reader.ReadForwardAsync(afterSeq, pageSize, cancellationToken).ConfigureAwait(false))
                .Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

            if (page.Count == 0)
                break;

            foreach (var appended in page)
            {
                EnsureAscending(lastSeqSeen, appended.Seq.Value,
                    $"{nameof(IEventReader)}.{nameof(IEventReader.ReadForwardAsync)} (via {nameof(RebuildAsync)})");
                lastSeqSeen = appended.Seq.Value;
                Apply(summaries, appended);
            }

            afterSeq = page[^1].Seq;
        }

        return summaries;
    }

    private static void EnsureAscending(long previousSeq, long candidateSeq, string source)
    {
        if (candidateSeq < previousSeq)
        {
            throw new InvalidOperationException(
                $"{source} requires events in ascending seq order; got seq {candidateSeq} after {previousSeq}.");
        }
    }

    private static void Apply(Dictionary<AggregateId, AggregateSummary> summaries, AppendedEvent appended)
    {
        summaries[appended.AggregateId] = summaries.TryGetValue(appended.AggregateId, out var existing)
            ? existing with
            {
                EventCount = existing.EventCount + 1,
                LastSeq = appended.Seq,
                LastServerTimestamp = appended.ServerTimestamp,
            }
            : new AggregateSummary(appended.AggregateId, 1, appended.Seq, appended.Seq, appended.ServerTimestamp);
    }
}
