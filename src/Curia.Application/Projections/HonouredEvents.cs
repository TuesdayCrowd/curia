using System.Collections.Immutable;
using Curia.Domain;

namespace Curia.Application.Projections;

/// <summary>
/// The events of a log less the operator-act rows the Forum does not honour (errata G70, R4.43, R6.67), each with its own <c>Seq</c>, in log order: what the raw projectors are fed so that a skipped act is
/// never read. <b>It is not a log</b>: a position in <see cref="Events"/> is not a log index (R6.47), so it implements no list, and the one thing that holds the log is <see cref="Log"/>. A fold that reads
/// <see cref="Events"/> is a fold over events in sequence order, which is all the raw folds check (that <c>Seq</c> ascends). Only <see cref="Credentials.VerifiedLog"/> makes one.
/// </summary>
public sealed class HonouredEvents
{
    internal HonouredEvents(ImmutableArray<AppendedEvent> events, IReadOnlyList<AppendedEvent>? log)
    {
        Events = events;
        _log = log;
    }

    /// <summary>The log less the skipped act rows, each event unchanged, in log order. For the raw folds only.</summary>
    public ImmutableArray<AppendedEvent> Events { get; }

    /// <summary>The whole log these events were taken from (R6.47), skipped rows included: what a log index is a position in.</summary>
    public IReadOnlyList<AppendedEvent> Log => _log ?? throw new Reads.OutOfScopeException("the whole log");

    private readonly IReadOnlyList<AppendedEvent>? _log;
}
