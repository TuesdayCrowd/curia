namespace Curia.Application.Retrieval;

/// <summary>A committed post whose vector is not stored (R11.69): its digest, its post id and its sequence, and never its text.</summary>
public sealed record PendingEntry(string Digest, string PostId, long Sequence);

/// <summary>
/// The posts this process committed and could not index (errata G51, R11.69). It holds a digest, a post id and a sequence per post and no text: the retry
/// reads the event from the log, the only source of the text, so a copy cannot outlive a withholding or differ from what was signed. It is bounded; an add
/// refused for the bound sets <see cref="Overflowed"/> and the catch-up then repairs by absence of the digest. Every instant is handed in: the class reads no clock.
/// </summary>
public sealed class IndexBacklog
{
    /// <summary>The most entries the backlog holds (provisional; R11.69 publishes it).</summary>
    public const int Bound = 4_096;

    /// <summary>The wait before a post's first retry (provisional; R11.69 publishes it).</summary>
    public static readonly TimeSpan FirstWait = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait between two retries of one post (provisional; R11.69 publishes it).</summary>
    public static readonly TimeSpan WaitCap = TimeSpan.FromMinutes(5);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private bool _overflowed;
    private DateTimeOffset _lastSuccess = DateTimeOffset.MinValue;

    private sealed class Slot(PendingEntry entry, DateTimeOffset due)
    {
        internal PendingEntry Entry { get; } = entry;

        internal DateTimeOffset Due { get; set; } = due;

        internal int Failures { get; set; }
    }

    /// <summary>How many entries the backlog holds now.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _slots.Count;
        }
    }

    /// <summary>Whether an add has been refused for the bound since the last by-absence pass.</summary>
    public bool Overflowed
    {
        get
        {
            lock (_gate) return _overflowed;
        }
    }

    /// <summary>
    /// Holds <paramref name="entry"/>, first due at <paramref name="due"/>. A second add of one digest is the same entry. False where it is refused for the
    /// bound, which also sets <see cref="Overflowed"/>.
    /// </summary>
    public bool Add(PendingEntry entry, DateTimeOffset due)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_gate)
        {
            if (_slots.ContainsKey(entry.Digest)) return true;
            if (_slots.Count >= Bound)
            {
                _overflowed = true;
                return false;
            }

            _slots[entry.Digest] = new Slot(entry, due);
            return true;
        }
    }

    /// <summary>The entries due at <paramref name="now"/>, in log order.</summary>
    public IReadOnlyList<PendingEntry> Due(DateTimeOffset now)
    {
        lock (_gate)
            return [.. _slots.Values.Where(s => s.Due <= now).Select(s => s.Entry).OrderBy(e => e.Sequence)];
    }

    /// <summary>Removes the entry whose vector was stored.</summary>
    public void Succeeded(string digest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        lock (_gate) _slots.Remove(digest);
    }

    /// <summary>Keeps the entry after a failed retry made at <paramref name="now"/>, and schedules the next: twice the last wait, to the cap, from <paramref name="now"/>.</summary>
    public void Failed(string digest, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        lock (_gate)
        {
            if (!_slots.TryGetValue(digest, out var slot)) return;

            slot.Failures++;
            var ticks = FirstWait.Ticks;
            for (var i = 0; i < slot.Failures && ticks < WaitCap.Ticks; i++)
                ticks = Math.Min(ticks * 2, WaitCap.Ticks);

            slot.Due = now + TimeSpan.FromTicks(ticks);
        }
    }

    /// <summary>
    /// A success of the vector store at <paramref name="now"/> (errata G54, R11.69 (rev.)): every entry becomes due now and its wait returns to the first, at most
    /// once in <see cref="FirstWait"/>. Nothing calls it at the seam of the "A committed post is found" PR.
    /// </summary>
    public void NoteSuccess(DateTimeOffset now)
    {
        lock (_gate)
        {
            if (now - _lastSuccess < FirstWait) return;

            _lastSuccess = now;
            foreach (var slot in _slots.Values)
            {
                slot.Due = now;

                // The retry that is now due is the "first retry" again: if it fails, the wait is the first wait, then the doubling from there.
                slot.Failures = -1;
            }
        }
    }

    /// <summary>Clears <see cref="Overflowed"/> once the by-absence pass has run.</summary>
    public void ClearOverflow()
    {
        lock (_gate) _overflowed = false;
    }

    /// <summary>Sets <see cref="Overflowed"/> again where the by-absence pass that was to clear it could not complete.</summary>
    public void MarkOverflowed()
    {
        lock (_gate) _overflowed = true;
    }
}
