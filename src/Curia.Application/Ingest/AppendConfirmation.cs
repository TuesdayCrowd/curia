using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Ingest;

/// <summary>
/// What the confirm of a faulted append found (errata G55, R8.84): the post (<see cref="Found"/>), nothing (<see cref="Absent"/>), a log that could not be read
/// (<see cref="Unreadable"/>), a read that outlasted its bound (<see cref="Bound"/>), or the host stopping (<see cref="Stopped"/>). A closed hierarchy (CS-11): a
/// sixth outcome breaks every <see cref="Match{T}"/> call site.
/// </summary>
public abstract record ConfirmOutcome
{
    private protected ConfirmOutcome()
    {
    }

    /// <summary>The post is in the log: its id, its digest, the stored instant (the column's, R8.84) and the sequence of its event.</summary>
    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "The cases of a closed hierarchy (CS-11) belong nested under ConfirmOutcome: they have no meaning standing alone, and private protected construction confines the hierarchy to this assembly.")]
    public sealed record Found(string PostId, string Digest, ServerTimestamp ServerTimestamp, EventSequence Sequence) : ConfirmOutcome;

    /// <summary>The read completed and the log, as of the read, does not hold the post.</summary>
    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "The cases of a closed hierarchy (CS-11) belong nested under ConfirmOutcome: they have no meaning standing alone, and private protected construction confines the hierarchy to this assembly.")]
    public sealed record Absent : ConfirmOutcome;

    /// <summary>The read failed: the confirm could not learn.</summary>
    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "The cases of a closed hierarchy (CS-11) belong nested under ConfirmOutcome: they have no meaning standing alone, and private protected construction confines the hierarchy to this assembly.")]
    public sealed record Unreadable : ConfirmOutcome;

    /// <summary>The read did not finish within the confirm's bound.</summary>
    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "The cases of a closed hierarchy (CS-11) belong nested under ConfirmOutcome: they have no meaning standing alone, and private protected construction confines the hierarchy to this assembly.")]
    public sealed record Bound : ConfirmOutcome;

    /// <summary>The host was stopping.</summary>
    [SuppressMessage(
        "Design",
        "CA1034:Nested types should not be visible",
        Justification = "The cases of a closed hierarchy (CS-11) belong nested under ConfirmOutcome: they have no meaning standing alone, and private protected construction confines the hierarchy to this assembly.")]
    public sealed record Stopped : ConfirmOutcome;

    /// <summary>The outcome's name as the Forum's log writes it (R8.84 words): <c>found</c>, <c>absent</c>, <c>unreadable</c>, <c>bound</c> or <c>stopped</c>.</summary>
    public string Name => Match(_ => "found", () => "absent", () => "unreadable", () => "bound", () => "stopped");

    public T Match<T>(Func<Found, T> found, Func<T> absent, Func<T> unreadable, Func<T> bound, Func<T> stopped)
    {
        ArgumentNullException.ThrowIfNull(found);
        ArgumentNullException.ThrowIfNull(absent);
        ArgumentNullException.ThrowIfNull(unreadable);
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentNullException.ThrowIfNull(stopped);

        return this switch
        {
            Found f => found(f),
            Absent => absent(),
            Unreadable => unreadable(),
            Bound => bound(),
            Stopped => stopped(),
            _ => throw new InvalidOperationException("ConfirmOutcome is closed."),
        };
    }
}

/// <summary>
/// The confirm of a faulted append (errata G55, R8.84): one read of the events appended after the sequence the route's own read held, for the act's digest.
/// <b>The seam of the "A faulted append is confirmed" PR: declared, called by nothing, and it always reports <see cref="ConfirmOutcome.Absent"/>.</b> The
/// behaviour is the PR's next commit; this one moves no word and no status.
/// </summary>
public static class AppendConfirmation
{
    /// <summary>
    /// Reads the log after <paramref name="afterSeq"/> for <paramref name="digest"/>, bounded by <paramref name="bound"/> on <paramref name="clock"/> and stopped
    /// only by <paramref name="host"/>. Total once built: it returns an outcome and never throws.
    /// </summary>
    public static Task<ConfirmOutcome> ConfirmAsync(IEventReader events, EventSequence afterSeq, string digest, TimeProvider clock, TimeSpan bound, CancellationToken host)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        ArgumentNullException.ThrowIfNull(clock);
        _ = afterSeq;
        _ = host;
        _ = bound;
        return Task.FromResult<ConfirmOutcome>(new ConfirmOutcome.Absent());
    }
}
