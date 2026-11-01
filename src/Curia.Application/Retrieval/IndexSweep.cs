namespace Curia.Application.Retrieval;

/// <summary>
/// The state of the check that compares the log with the vector index (errata G54, R8.83), as the seam of the "A committed post is found" PR leaves it: the
/// type exists, is wired and is served, and does no work. <see cref="Owe"/> is ignored, <see cref="IsDue"/> is false, <see cref="CheckedAt"/> is the instant
/// the process started and <see cref="DueAt"/> is always <see langword="null"/>; the in-flight set and the memory of posts the model cannot place are
/// kept, and nothing reads them yet. Every instant is handed in: the class reads no clock (as <see cref="IndexBacklog"/> does not).
/// </summary>
public sealed class IndexSweep
{
    /// <summary>The most posts the memory of unplaceable digests holds (provisional; R8.83 publishes it).</summary>
    public const int UnplaceableBound = 4_096;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unplaceable = new(StringComparer.Ordinal);

    public IndexSweep(DateTimeOffset startedAt) => CheckedAt = startedAt;

    /// <summary>The instant the last completed check began.</summary>
    public DateTimeOffset CheckedAt { get; }

#pragma warning disable CA1822 // The seam: these are instance members because the check will keep state in them; at the seam they answer from none.
    /// <summary>The instant of the next attempt where a check is owed, otherwise <see langword="null"/>.</summary>
    public DateTimeOffset? DueAt => null;

    /// <summary>Records that a discussion post's <c>PersistAsync</c> left by an exception at <paramref name="now"/>. Ignored at the seam.</summary>
    public void Owe(DateTimeOffset now)
    {
    }

    /// <summary>Whether a check is to run at <paramref name="now"/>. False at the seam.</summary>
    public bool IsDue(DateTimeOffset now) => false;
#pragma warning restore CA1822

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
