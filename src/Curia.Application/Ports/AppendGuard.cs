using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

/// <summary>
/// R6.60 (errata G57): a decision made on a log read before the append, and the pure function that decides whether it still holds against the
/// events appended since. <see cref="After"/> is the sequence of the last event of the log the caller decided on; the store reads the events after it
/// inside the log's lock, after the stream's version and condition checks and before the INSERT, and calls <see cref="Evaluate"/> once when there are
/// any (never on an empty delta, which proves only that nothing changed since the read). The function is synchronous, reads no clock and does no
/// I/O; a throw rolls the append back and leaves the exception to the caller, and is never read as <see cref="GuardVerdict.Proceed"/>.
///
/// <para>A guard is built from the list a caller's checks ran on, by one function (<c>SubmissionGuard.For</c>, <c>FlagGuard.For</c>), so the
/// position is never passed apart from the read it belongs to.</para>
/// </summary>
/// <param name="After">The sequence of the last event of the log the caller decided on (<see cref="EventSequence.Zero"/> for an empty log).</param>
/// <param name="Evaluate">The events with a sequence above <see cref="After"/>, ascending, across every aggregate, to a verdict.</param>
public sealed record AppendGuard(EventSequence After, Func<IReadOnlyList<AppendedEvent>, GuardVerdict> Evaluate)
{
    /// <summary>
    /// R6.60: the largest delta a store reads for a guard (provisional). A delta over it is refused unread with
    /// <c>DomainErrors.AppendDeltaTooLarge</c>. Equal to the page the log is read in, and defined here once.
    /// </summary>
    public const int DeltaCap = EventReaderExtensions.PageSize;
}

/// <summary>
/// What an <see cref="AppendGuard"/> says about its delta (CS-11: a closed hierarchy; a fourth verdict must break every call site).
/// <see cref="Proceed"/> lets the append go on; <see cref="Refuse"/> rolls it back and returns the caller's own error unchanged;
/// <see cref="Redecide"/> rolls it back and returns <c>curia/append/redecide</c>, which no response carries.
/// </summary>
public abstract class GuardVerdict
{
    private GuardVerdict()
    {
    }

    /// <summary>The decision still holds.</summary>
    public static GuardVerdict Proceed { get; } = new ProceedVerdict();

    /// <summary>The decision does not hold; <paramref name="error"/> is what the caller answers.</summary>
    public static GuardVerdict Refuse(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new RefuseVerdict(error);
    }

    /// <summary>The decision cannot be judged from the delta; the caller must decide again from a fresh read.</summary>
    public static GuardVerdict Redecide { get; } = new RedecideVerdict();

    /// <summary>The one way to read a verdict: all three cases, so a new one breaks every caller.</summary>
    public abstract T Match<T>(Func<T> proceed, Func<Error, T> refuse, Func<T> redecide);

    private sealed class ProceedVerdict : GuardVerdict
    {
        public override T Match<T>(Func<T> proceed, Func<Error, T> refuse, Func<T> redecide) => proceed();
    }

    private sealed class RefuseVerdict(Error error) : GuardVerdict
    {
        public override T Match<T>(Func<T> proceed, Func<Error, T> refuse, Func<T> redecide) => refuse(error);
    }

    private sealed class RedecideVerdict : GuardVerdict
    {
        public override T Match<T>(Func<T> proceed, Func<Error, T> refuse, Func<T> redecide) => redecide();
    }
}
