using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Projections;
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
/// The confirm of a faulted append (errata G55, R8.84): one read of the events appended after the sequence the route's own read held, for the act's digest, matched by
/// <see cref="PostProjector.FirstWithDigest"/>, the rule R8.78 refuses a repeat by. It reads and never appends, is made once by the route and never retries, polls or
/// waits for a late commit (a post not found is not found yet), and is <b>total</b>: it returns an outcome for every fault of the reader and never throws, so its own
/// failure cannot replace the exception the route is answering (trap 102).
/// </summary>
public static class AppendConfirmation
{
    /// <summary>The confirm's bound (<c>ConfirmBound</c>, provisional; R8.84 words publishes it): the same figure as the index step's, for the same reason, that the agent's hold is kept through it.</summary>
    public static readonly TimeSpan ConfirmBound = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads the log after <paramref name="afterSeq"/>, in pages, for <paramref name="digest"/>, bounded by <paramref name="bound"/> on <paramref name="clock"/> and
    /// stopped only by <paramref name="host"/> (never by the caller's disconnection: a client that has gone is exactly the case that leaves a committed post). The bound
    /// is a token, so it binds a read that honours its token, which Npgsql's does and an in-memory fake's does not (trap 80): a read that ignores it is not cut short.
    /// </summary>
    public static async Task<ConfirmOutcome> ConfirmAsync(IEventReader events, EventSequence afterSeq, string digest, TimeProvider clock, TimeSpan bound, CancellationToken host)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentException.ThrowIfNullOrWhiteSpace(digest);
        ArgumentNullException.ThrowIfNull(clock);

        CancellationTokenSource? limit = null;
        CancellationTokenSource? linked = null;
        try
        {
            limit = new CancellationTokenSource(bound, clock);
            linked = CancellationTokenSource.CreateLinkedTokenSource(host, limit.Token);
            var after = afterSeq;
            while (true)
            {
                var page = await events.ReadForwardAsync(after, EventReaderExtensions.PageSize, linked.Token).ConfigureAwait(false);
                if (!page.TryGetValue(out var rows, out _))
                    return new ConfirmOutcome.Unreadable();

                if (PostProjector.FirstWithDigest(rows!, digest) is { } match)
                    return new ConfirmOutcome.Found(match.PostId, match.Digest, match.ServerTimestamp, match.Sequence);

                if (rows!.Count < EventReaderExtensions.PageSize)
                    return new ConfirmOutcome.Absent();

                after = rows[^1].Seq;
            }
        }
#pragma warning disable CA1031 // R8.84: the confirm is total. Whatever the read throws (a cancellation, LogUnreadableException, a timeout, anything) is an outcome, and the exception the route is answering is the one it rethrows.
        catch (Exception)
#pragma warning restore CA1031
        {
            // A store may wrap its cancellation in a type of its own, so the outcome is read from which token fired and not from the exception's type.
            if (host.IsCancellationRequested) return new ConfirmOutcome.Stopped();
            return limit?.IsCancellationRequested == true ? new ConfirmOutcome.Bound() : new ConfirmOutcome.Unreadable();
        }
        finally
        {
            linked?.Dispose();
            limit?.Dispose();
        }
    }
}
