using System.Collections.Immutable;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Budget;

/// <summary>
/// The one function that counts a budget (errata G49, R7.28): the policy's three callers (a post, a flag, an acceptance) and the readout count through
/// <see cref="CountIn"/>, so that no second count can differ from the policy's at the boundary, the only place a count is read.
///
/// <para><b>The window.</b> A trailing 24 hours, not a calendar day: Table 11 says "3 posts/day" without naming a boundary, and a calendar day needs a
/// timezone nobody has specified. An entry is counted while it is less than <see cref="Window"/> old, so the slot an entry held is free at the instant it is
/// exactly that old. Counted from the log rather than a counter, so it survives a restart and cannot drift from what happened.</para>
///
/// <para><b>Pure.</b> No clock, no state: <c>now</c> is a parameter. <see cref="Read"/>, <see cref="After"/> and <see cref="NextSlotAt"/> are built on
/// top of this counting, never beside it.</para>
/// </summary>
public static class BudgetReadout
{
    /// <summary>The trailing window every budget is counted over.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(86_400);

    /// <summary>How many of <paramref name="instants"/> are less than <see cref="Window"/> old at <paramref name="now"/>: the one count.</summary>
    public static int CountIn(IEnumerable<DateTimeOffset> instants, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instants);

        var since = now - Window;
        return instants.Count(at => at > since);
    }

    /// <summary>The stored instants of the posts <paramref name="agentId"/> authored (R7.15): the Table 11 posting budget's entries.</summary>
    public static IEnumerable<DateTimeOffset> PostInstants(IReadOnlyList<AppendedEvent> log, AgentId agentId)
    {
        ArgumentNullException.ThrowIfNull(log);

        return PostProjector.Fold(log)
            .Where(p => string.Equals(p.Author, agentId.Value, StringComparison.Ordinal))
            .Select(p => p.ServerTimestamp.Value);
    }

    /// <summary>The stored instants of the <c>answer.accepted</c> events <paramref name="agentId"/> recorded (R7.26): the acceptance budget's entries.</summary>
    public static IEnumerable<DateTimeOffset> AcceptInstants(IReadOnlyList<AppendedEvent> log, AgentId agentId) =>
        AcceptanceProjector.InstantsBy(log, agentId);

    /// <summary>The instants of the flags <paramref name="agentId"/> raised (R7.22), from the joined directory: the flag budget's entries.</summary>
    public static IEnumerable<DateTimeOffset> FlagInstants(ImmutableArray<RaisedFlag> flags, AgentId agentId) =>
        flags.Where(f => string.Equals(f.RaisedBy, agentId.Value, StringComparison.Ordinal)).Select(f => f.At.Value);

    /// <summary>R7.15's recent post rate.</summary>
    public static int PostsUsed(IReadOnlyList<AppendedEvent> log, AgentId agentId, DateTimeOffset now) =>
        CountIn(PostInstants(log, agentId), now);

    /// <summary>R7.26's acceptance count (errata G46).</summary>
    public static int AcceptsUsed(IReadOnlyList<AppendedEvent> log, AgentId agentId, DateTimeOffset now) =>
        CountIn(AcceptInstants(log, agentId), now);

    /// <summary>R7.22's flag count (errata G18), counted from the joined directory and not the log alone: a leaf never names its raiser (R10.62).</summary>
    public static int FlagsUsed(ImmutableArray<RaisedFlag> flags, AgentId agentId, DateTimeOffset now) =>
        CountIn(FlagInstants(flags, agentId), now);

    /// <summary>
    /// One budget's entry from the instants its events hold (R7.28): the count is <see cref="CountIn"/>'s, <c>remaining</c> the larger of 0 and
    /// <paramref name="limit"/> less used, and <c>next_slot_at</c> <see cref="NextSlotAt"/>'s over the entries counted. <see cref="int.MaxValue"/> is the
    /// policy's "negotiated" and is stated as a null limit, remaining and next slot with <c>used</c> the count, never the sentinel.
    /// </summary>
    public static BudgetEntry Entry(string name, int limit, IReadOnlyList<DateTimeOffset> instants, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(instants);

        var since = now - Window;
        var counted = instants.Where(at => at > since).ToArray();
        if (limit == int.MaxValue) return new BudgetEntry(name, null, counted.Length, null, null);

        return new BudgetEntry(name, limit, counted.Length, Math.Max(0, limit - counted.Length), NextSlotAt(counted, limit));
    }

    /// <summary>The standing of every budget at <paramref name="now"/>, in R9.50's order, each over the one counting function.</summary>
    public static IReadOnlyList<BudgetEntry> Read(
        IReadOnlyList<AppendedEvent> log, ImmutableArray<RaisedFlag> flags, AgentId agentId, int postLimit, int acceptLimit, int flagLimit, DateTimeOffset now) =>
    [
        Entry(BudgetKinds.Posting, postLimit, [.. PostInstants(log, agentId)], now),
        Entry(BudgetKinds.Acceptance, acceptLimit, [.. AcceptInstants(log, agentId)], now),
        Entry(BudgetKinds.Flag, flagLimit, [.. FlagInstants(flags, agentId)], now),
    ];

    /// <summary>
    /// One entry from the instants counted before an act and the act just made (R7.29): the same function as <see cref="Entry"/> over the union, so a
    /// receipt and a readout over the same state agree by construction.
    /// </summary>
    public static BudgetEntry After(string name, int limit, IReadOnlyList<DateTimeOffset> counted, DateTimeOffset act, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(counted);

        return Entry(name, limit, [.. counted, act], now);
    }

    /// <summary>
    /// The instant a slot next frees (R7.28): the k-th oldest of <paramref name="counted"/> plus <see cref="Window"/>, with k the larger of 1 and
    /// <c>used - limit + 1</c>; null where nothing is counted or the limit is negotiated. The instants are taken as given (the stored column's), so the
    /// answer is that instant plus the window and no rounding of its own.
    /// </summary>
    public static DateTimeOffset? NextSlotAt(IReadOnlyList<DateTimeOffset> counted, int limit)
    {
        ArgumentNullException.ThrowIfNull(counted);

        if (counted.Count == 0 || limit == int.MaxValue) return null;

        var ordered = counted.Order().ToArray();
        var k = Math.Min(ordered.Length, Math.Max(1, ordered.Length - limit + 1));
        return ordered[k - 1] + Window;
    }
}
