using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain;

namespace Curia.Api.Adapters;

/// <summary>
/// R4.38 (add.), guard fact GF10: at start, counts the log's entries of the three guarded lifecycle
/// shapes by event type and stream shape, and says so when there are any and the guard is on. It starts
/// the Forum either way: an entry already in a retained log is a fact the operator has to hear about,
/// not a reason to take the Forum down. The shapes are the chokepoint's (<see cref="LifecycleAppendGuard"/>)
/// with one stated undercount: a log read cannot see append boundaries, so a lone <c>agent.key-bound</c>
/// appended separately onto a stream whose only event is <c>agent.enrolled</c> is indistinguishable from an
/// enrolment pair and is not counted. The chokepoint refuses it as a Rotation; this scan cannot see it.
/// The counted shapes are <see cref="LifecycleRows"/> (errata G67: the one predicate, shared with <c>curia-operator sign-head --if-grown</c>'s stop):
/// <c>agent.key-retired</c>, <c>agent.key-compromised</c>, and an
/// <c>agent.key-bound</c> that is not the second event of a stream whose first is <c>agent.enrolled</c>.
/// </summary>
public sealed partial class LifecycleEntryScan : IHostedService
{
    private const int PageSize = 1000;

    private readonly IEventReader _events;
    private readonly ILifecycleAppendGate _gate;
    private readonly ILogger<LifecycleEntryScan> _log;

    public LifecycleEntryScan(IEventReader events, ILifecycleAppendGate gate, ILogger<LifecycleEntryScan> log)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(log);
        _events = events;
        _gate = gate;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var count = 0L;
        var rows = new LifecycleRows();
        var after = EventSequence.Zero;

        while (true)
        {
            var page = await _events.ReadForwardAsync(after, PageSize, cancellationToken).ConfigureAwait(false);
            if (!page.TryGetValue(out var read, out var error))
            {
                throw new InvalidOperationException(
                    $"The log could not be scanned for lifecycle entries ({error!.Type}: {error.Title}). " +
                    "The Forum does not start without knowing what its guard is holding back.");
            }

            if (read.Count == 0)
                break;

            foreach (var appended in read)
            {
                if (rows.Observe(appended, out _))
                    count++;
            }

            after = read[^1].Seq;
        }

        var guarded = !(_gate.Enables(LifecycleKind.Rotation) && _gate.Enables(LifecycleKind.Retirement) && _gate.Enables(LifecycleKind.Compromise));
        if (count > 0 && guarded)
            EntriesUnderGuard(_log, count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 7006,
        EventName = "LifecycleEntriesUnderGuard",
        Level = LogLevel.Error,
        Message = "The log holds {Count} lifecycle entr(ies) beyond enrolment pairs while the lifecycle guard is on (R4.38 (add.))")]
    private static partial void EntriesUnderGuard(ILogger logger, long count);
}
