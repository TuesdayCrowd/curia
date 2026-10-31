namespace Curia.Application.Retrieval;

// Task 0 seam: the members below are inert and touch no instance data until the repair's task gives them a body; CA1822 is expected and ends with it.
#pragma warning disable CA1822

/// <summary>A committed post whose vector is not stored (R11.69): its digest, its post id and its sequence, and never its text.</summary>
public sealed record PendingEntry(string Digest, string PostId, long Sequence);

/// <summary>
/// The posts this process committed and could not index (errata G51, R11.69). <b>Task 0 seam: inert.</b> It stores nothing, so a
/// probe that expects an entry is red by assertion; the real backlog replaces this body in the repair's task.
/// </summary>
public sealed class IndexBacklog
{
    /// <summary>The most entries the backlog holds (provisional; R11.69 publishes it).</summary>
    public const int Bound = 4_096;

    /// <summary>How many entries the backlog holds now.</summary>
    public int Count => 0;

    /// <summary>Whether an add has been refused for the bound since the last by-absence pass.</summary>
    public bool Overflowed => false;

    /// <summary>Holds <paramref name="entry"/>, first due at <paramref name="due"/>; false where it is refused for the bound.</summary>
    public bool Add(PendingEntry entry, DateTimeOffset due)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return true;
    }

    /// <summary>The entries due at <paramref name="now"/>.</summary>
    public IReadOnlyList<PendingEntry> Due(DateTimeOffset now) => [];

    /// <summary>Removes the entry whose vector was stored.</summary>
    public void Succeeded(string digest) => ArgumentException.ThrowIfNullOrWhiteSpace(digest);

    /// <summary>Keeps the entry after a failed retry made at <paramref name="now"/>, and schedules the next.</summary>
    public void Failed(string digest, DateTimeOffset now) => ArgumentException.ThrowIfNullOrWhiteSpace(digest);

    /// <summary>Clears <see cref="Overflowed"/> once the by-absence pass has run.</summary>
    public void ClearOverflow()
    {
    }
}
