namespace Curia.Client;

/// <summary>What the Forum's key set says of a rotation or a retirement this profile sent and did not hear an answer to (errata G74, R11.99 (settling), R11.100).</summary>
public enum KeyActState
{
    /// <summary>The key set binds the kept kid with the kept key's public half (a rotation) or shows the kid retired (a retirement).</summary>
    Recorded,

    /// <summary>The key set shows neither, and the window since the act was sent has not passed.</summary>
    Unknown,

    /// <summary>The window has passed and a row the Forum skips could be the act's: the act cannot be told from a stranger's.</summary>
    Held,

    /// <summary>The window has passed, the key set shows neither, and no skipped row could be the act's.</summary>
    NotRecorded,
}

/// <summary>A pending act and what the key set says of it; <paramref name="LogIndex"/> is the index the key set names, for a recorded act.</summary>
public readonly record struct KeyActReading(PendingKeyAct Act, KeyActState State, long? LogIndex);

/// <summary>
/// The decision R11.99 (settling) makes about an act made and not known, in one place: <c>curia rotate</c>, <c>curia retire</c> (which then act on it), <c>curia keys</c> and <c>curia_keys</c> (which only say it) and
/// <see cref="LifecycleWatch.Compare"/> all read it here. The kept key's public half is compared with the key set's own members, the kid alone never deciding (a kid could be a stranger's).
/// </summary>
public static class KeyActSettling
{
    /// <summary>R11.99 (settling): the time after which an act the key set does not show is not recorded. A margin over a measurement and not a bound (G74 non-property (d)); an act is inside it for strictly less than this.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private const string BoundType = "agent.key-bound";
    private const string RetiredType = "agent.key-retired";

    /// <summary>
    /// The log index at which the key set shows the act as recorded, or null when it does not: a rotation where the key set binds the kept kid with <c>kty</c>, <c>crv</c>, <c>x</c> and <c>y</c> equal to the kept key's own
    /// members (a pending rotation whose key could not be read is never recorded), a retirement where the key set shows the kid retired.
    /// </summary>
    public static long? RecordedAt(PendingKeyAct act, KeySetDocument keySet)
    {
        ArgumentNullException.ThrowIfNull(act);
        ArgumentNullException.ThrowIfNull(keySet);

        if (act.Kind == LifecycleAct.Retirement)
            return keySet.Keys.FirstOrDefault(k => string.Equals(k.Kid, act.Kid, StringComparison.Ordinal) && k.RetiredLogIndex is not null)?.RetiredLogIndex;

        return act.Kept is { } kept
            ? keySet.Keys.FirstOrDefault(k => k.LogIndex is not null && string.Equals(k.Kid, act.Kid, StringComparison.Ordinal) && Holds(k, kept))?.LogIndex
            : null;
    }

    private static bool Holds(ForumJwk key, KeptKeyHalf kept) =>
        string.Equals(key.Kty, kept.Kty, StringComparison.Ordinal)
        && string.Equals(key.Crv, kept.Crv, StringComparison.Ordinal)
        && string.Equals(key.X, kept.X, StringComparison.Ordinal)
        && string.Equals(key.Y, kept.Y, StringComparison.Ordinal);

    /// <summary>
    /// What the key set says of <paramref name="act"/> at <paramref name="now"/>, decided in this order: recorded; inside the window (<c>now - sent_at</c> strictly less than <see cref="Window"/>); held; not recorded.
    /// </summary>
    public static KeyActReading Read(AgentProfile profile, PendingKeyAct act, KeySetDocument keySet, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(act);
        ArgumentNullException.ThrowIfNull(keySet);

        if (RecordedAt(act, keySet) is { } index) return new KeyActReading(act, KeyActState.Recorded, index);
        if (now - act.SentAt < Window) return new KeyActReading(act, KeyActState.Unknown, null);
        return new KeyActReading(act, CouldBeSkipped(profile, act, keySet) ? KeyActState.Held : KeyActState.NotRecorded, null);
    }

    /// <summary>
    /// Whether a row the key set says the Forum skips could be this act's: a skipped row of the act's kind (<c>agent.key-bound</c> for a rotation, <c>agent.key-retired</c> for a retirement) at an index the profile
    /// recorded no act at; or a total larger than the rows listed when none listed is of the kind (the list is capped, so the rows past it are unseen); or a key set that does not say which rows it skips because it
    /// does not say its lifecycle either (the Forum's log would not fold: an absence of a list is not a list of none). A key set that gives its lifecycle and no skipped list, as a Forum of before errata G68 did, is
    /// read as skipping none.
    /// </summary>
    private static bool CouldBeSkipped(AgentProfile profile, PendingKeyAct act, KeySetDocument keySet)
    {
        if (keySet.Skipped is not { } rows) return keySet.Lifecycle is null;

        var type = act.Kind == LifecycleAct.Rotation ? BoundType : RetiredType;
        var made = profile.Acts.Select(a => a.LogIndex).ToHashSet();
        if (rows.Any(r => string.Equals(r.Type, type, StringComparison.Ordinal) && !made.Contains(r.LogIndex))) return true;

        return keySet.SkippedTotal is { } total && total > rows.Length && !rows.Any(r => string.Equals(r.Type, type, StringComparison.Ordinal));
    }
}
