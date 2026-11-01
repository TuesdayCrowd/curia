namespace Curia.Application.Retrieval;

/// <summary>
/// The state and the schedule of the check that compares the log with the vector index (errata G54, R8.83). It holds whether a check is owed, the instant the
/// last completed check began, the instant of the next attempt, and the failure ladder; the catch-up asks it whether a check is due, runs the check and reports
/// the result. It also keeps the in-flight set (posts whose index step is running) and the memory of posts the model cannot place. Every instant is handed in:
/// the class reads no clock (as <see cref="IndexBacklog"/> does not).
/// </summary>
public sealed class IndexSweep
{
    /// <summary>The most posts the memory of unplaceable digests holds (provisional; R8.83 publishes it).</summary>
    public const int UnplaceableBound = 4_096;

    /// <summary>The repeat while a check is owed (<c>SweepRepeat</c>, provisional; R8.83 publishes it).</summary>
    public static readonly TimeSpan Repeat = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The settle window (<c>SweepSettle</c>, provisional; R8.83 publishes it): an obligation is cleared only by a check that began at least this long after the
    /// latest exit. A figure with margin over a measurement and not a bound (G54 non-property b).
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(60);

    /// <summary>The floor (<c>SweepFloor</c>, provisional; R8.83 publishes it): a check at least this long after the last began, owed or not.</summary>
    public static readonly TimeSpan Floor = TimeSpan.FromMinutes(5);

    /// <summary>The first wait after a check that could not complete; each further wait is doubled to the interval it would have had.</summary>
    public static readonly TimeSpan FirstFailedWait = TimeSpan.FromSeconds(5);

    /// <summary>The catch-up's tick (R11.69's one second): an obligation created at an exit is first attempted at the next tick.</summary>
    public static readonly TimeSpan NextTick = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unplaceable = new(StringComparer.Ordinal);
    private DateTimeOffset _checkedAt;
    private DateTimeOffset _nextAt;
    private DateTimeOffset _latestExit;
    private bool _owed;
    private int _failures;

    public IndexSweep(DateTimeOffset startedAt)
    {
        _checkedAt = startedAt;
        _nextAt = startedAt + Floor;
    }

    /// <summary>The instant the last completed check began (the reconcile at start included, so a serving process always has one).</summary>
    public DateTimeOffset CheckedAt
    {
        get
        {
            lock (_gate) return _checkedAt;
        }
    }

    /// <summary>The instant of the next attempt where a check is owed, otherwise <see langword="null"/>.</summary>
    public DateTimeOffset? DueAt
    {
        get
        {
            lock (_gate) return _owed ? _nextAt : null;
        }
    }

    /// <summary>
    /// Records that a discussion post's <c>PersistAsync</c> left by an exception at <paramref name="now"/> (R8.83): a check is owed, first attempted at the next
    /// tick. Where one is already owed its schedule is kept (a storm of exits does not pull the next attempt forward), and no attempt is further than the repeat.
    /// </summary>
    public void Owe(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now > _latestExit) _latestExit = now;
            _nextAt = _owed ? Min(_nextAt, now + Repeat) : now + NextTick;
            _owed = true;
        }
    }

    /// <summary>Whether a check is to run at <paramref name="now"/>.</summary>
    public bool IsDue(DateTimeOffset now)
    {
        lock (_gate) return now >= _nextAt;
    }

    /// <summary>
    /// A check that began at <paramref name="began"/> completed (R8.83): its beginning is the instant of the last check, the failure ladder starts again, and the
    /// obligation is cleared only if the check began at least <see cref="Settle"/> after the latest exit. The next attempt is the repeat after it where still owed,
    /// otherwise the floor.
    /// </summary>
    public void Completed(DateTimeOffset began)
    {
        lock (_gate)
        {
            _checkedAt = began;
            _failures = 0;
            if (_owed && began - _latestExit >= Settle) _owed = false;
            _nextAt = began + (_owed ? Repeat : Floor);
        }
    }

    /// <summary>
    /// A check attempted at <paramref name="now"/> could not complete (R8.83): the instant of the last check and the obligation stay as they were, and the next
    /// attempt is 5 seconds on, each further wait doubled to the interval it would have had (the repeat while owed, the floor otherwise).
    /// </summary>
    public void Failed(DateTimeOffset now)
    {
        lock (_gate)
        {
            var cap = _owed ? Repeat : Floor;
            var wait = FirstFailedWait.Ticks;
            for (var i = 0; i < _failures && wait < cap.Ticks; i++) wait *= 2;
            _failures++;
            _nextAt = now + TimeSpan.FromTicks(Math.Min(wait, cap.Ticks));
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    /// <summary>Registers <paramref name="digest"/> as a post whose index step is running in this process.</summary>
    public void BeginIndexing(string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        lock (_gate) _inFlight.Add(digest);
    }

    /// <summary>Removes <paramref name="digest"/> from the posts whose index step is running.</summary>
    public void EndIndexing(string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        lock (_gate) _inFlight.Remove(digest);
    }

    /// <summary>Whether the index step of <paramref name="digest"/> is running in this process.</summary>
    public bool IsIndexing(string digest)
    {
        lock (_gate) return _inFlight.Contains(digest);
    }

    /// <summary>Remembers that the model cannot place <paramref name="digest"/>. A digest past <see cref="UnplaceableBound"/> is not remembered.</summary>
    public void NoteUnplaceable(string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        lock (_gate)
            if (_unplaceable.Count < UnplaceableBound) _unplaceable.Add(digest);
    }

    /// <summary>Whether <paramref name="digest"/> was remembered as one the model cannot place.</summary>
    public bool IsUnplaceable(string digest)
    {
        lock (_gate) return _unplaceable.Contains(digest);
    }
}
