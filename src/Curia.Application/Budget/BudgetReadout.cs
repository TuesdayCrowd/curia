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
/// <para><b>Pure.</b> No clock, no state: <c>now</c> is a parameter. <see cref="Read"/>, <see cref="After"/> and <see cref="NextSlotAt"/> are declared and
/// inert in the seam (empty, a zero entry, null); the standing is built on top of this counting, never beside it.</para>
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
    /// One budget's entry from the instants its events hold: counted by <see cref="CountIn"/>, limited by <paramref name="limit"/> (<see cref="int.MaxValue"/>
    /// is the policy's "negotiated", stated as null). Inert in the seam: an entry with nothing stated.
    /// </summary>
    public static BudgetEntry Entry(string name, int limit, IReadOnlyList<DateTimeOffset> instants, DateTimeOffset now) =>
        new(name, null, 0, null, null);

    /// <summary>The standing of every budget at <paramref name="now"/>. Inert in the seam: empty.</summary>
    public static IReadOnlyList<BudgetEntry> Read(
        IReadOnlyList<AppendedEvent> log, ImmutableArray<RaisedFlag> flags, AgentId agentId, int postLimit, int acceptLimit, int flagLimit, DateTimeOffset now) => [];

    /// <summary>
    /// One entry from the instants counted before an act and the act just made. Inert in the seam: an entry with nothing stated.
    /// </summary>
    public static BudgetEntry After(string name, int limit, IReadOnlyList<DateTimeOffset> counted, DateTimeOffset act, DateTimeOffset now) =>
        new(name, null, 0, null, null);

    /// <summary>The instant a slot next frees for the counted instants under <paramref name="limit"/> (R7.28's k-th oldest rule). Inert in the seam: null.</summary>
    public static DateTimeOffset? NextSlotAt(IReadOnlyList<DateTimeOffset> counted, int limit) => null;
}
