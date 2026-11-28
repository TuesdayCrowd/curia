using Curia.Domain;

namespace Curia.Application.Reads;

/// <summary>
/// One row of the log with its R6.47 index: the number of rows of the log (<c>seq &gt; 0</c>) that precede it, computed in the same snapshot as the row (errata G87, R11.113).
/// A decision that uses an index as a value (<c>prev</c>, <c>from_index</c>, a key's <c>LogIndex &lt; index</c>) takes it from here and never from a position in a list it was not given whole.
/// </summary>
public readonly record struct PositionedEvent(long LogIndex, AppendedEvent Event);

/// <summary>
/// The rows one <see cref="ReadPlan"/> returned, in ascending <c>seq</c>, each once, with the instant of the snapshot they were read in (errata G87, R11.113).
/// It carries <see cref="Scope"/>, the account of what was read, so a view built over it can refuse a question about a row it was not read for
/// (<see cref="OutOfScopeException"/>) and never answer "none" for one.
/// </summary>
public sealed class LogSlice
{
    public LogSlice(IReadOnlyList<PositionedEvent> rows, EventSequence highWater, ReadScope scope)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(scope);

        Rows = rows;
        HighWater = highWater;
        Scope = scope;
    }

    /// <summary>The rows, ascending by <c>seq</c>.</summary>
    public IReadOnlyList<PositionedEvent> Rows { get; }

    /// <summary>The greatest <c>seq</c> of the log in the snapshot (<see cref="EventSequence.Zero"/> for an empty log): what an append guard's <c>After</c> is when the decision was made on this read.</summary>
    public EventSequence HighWater { get; }

    /// <summary>What this slice was read for.</summary>
    public ReadScope Scope { get; }
}

/// <summary>
/// What a read was for: the aggregates whose every row was read, whether the three published-key types were, the actors whose posts and acceptances were, and the values the payload clauses
/// named. A row not named here may be in the slice (a clause reads whole rows) and is not thereby read for.
/// </summary>
public sealed class ReadScope
{
    private readonly HashSet<string> _aggregates;
    private readonly HashSet<string> _postActors;
    private readonly HashSet<string> _acceptanceActors;
    private readonly HashSet<string> _digests;
    private readonly HashSet<string> _signalTargets;
    private readonly HashSet<string> _flagEvents;

    internal ReadScope(
        HashSet<string> aggregates,
        bool publishedKeys,
        HashSet<string> postActors,
        HashSet<string> acceptanceActors,
        HashSet<string> digests,
        HashSet<string> signalTargets,
        HashSet<string> flagEvents,
        bool legacyFlags)
    {
        _aggregates = aggregates;
        PublishedKeys = publishedKeys;
        _postActors = postActors;
        _acceptanceActors = acceptanceActors;
        _digests = digests;
        _signalTargets = signalTargets;
        _flagEvents = flagEvents;
        LegacyFlags = legacyFlags;
    }

    /// <summary>The scope of a whole read: everything.</summary>
    public static ReadScope Whole { get; } = new([], false, [], [], [], [], [], false) { IsWhole = true };

    /// <summary>True for a read of every row of the log.</summary>
    public bool IsWhole { get; private init; }

    /// <summary>The three published-key types (<c>log.key</c>, <c>log.declaration-key</c>, <c>log.operator-key</c>) were read in full.</summary>
    public bool PublishedKeys { get; }

    /// <summary>Every <c>flag.raised</c> row (a pre-R10.62 flag, which names its raiser in the log) was read.</summary>
    public bool LegacyFlags { get; }

    /// <summary>Whether every row of aggregate <paramref name="id"/> was read.</summary>
    public bool CoversAggregate(string id) => IsWhole || _aggregates.Contains(id);

    /// <summary>Whether every post <paramref name="actor"/> authored was read.</summary>
    public bool CoversPostsBy(string actor) => IsWhole || _postActors.Contains(actor);

    /// <summary>Whether every acceptance <paramref name="actor"/> recorded was read.</summary>
    public bool CoversAcceptancesBy(string actor) => IsWhole || _acceptanceActors.Contains(actor);

    /// <summary>Whether every post whose digest is <paramref name="digest"/> was read.</summary>
    public bool CoversDigest(string digest) => IsWhole || _digests.Contains(digest);

    /// <summary>Whether every vote and verification that targets <paramref name="digest"/> was read.</summary>
    public bool CoversSignalsOn(string digest) => IsWhole || _signalTargets.Contains(digest);

    /// <summary>The digests whose signals (votes and verifications) the read named: empty for an agent that holds no finding (errata G87, RC16: no statement names the signal target then).</summary>
    public IReadOnlyCollection<string> SignalTargets => _signalTargets;

    /// <summary>Whether the flag entry with event id <paramref name="eventId"/> was read.</summary>
    public bool CoversFlagEvent(string eventId) => IsWhole || _flagEvents.Contains(eventId);
}

/// <summary>
/// A view built over a read was asked about a row that read did not include (errata G87, R11.113). Never an empty answer: "no such row" for a row nobody looked for is a deny nobody decided
/// (R5.24, R7.31), so the question fails and the request is a fault of the Forum, not a refusal of its caller.
/// </summary>
public sealed class OutOfScopeException : InvalidOperationException
{
    public OutOfScopeException()
        : base("The question names a row this read was not made for.")
    {
    }

    public OutOfScopeException(string what)
        : base("This read was not made for " + what + ".")
    {
    }

    public OutOfScopeException(string what, Exception? inner)
        : base("This read was not made for " + what + ".", inner)
    {
    }
}
