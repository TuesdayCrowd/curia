using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Infrastructure;

namespace Curia.Api.Adapters;

/// <summary>
/// Errata G70, R11.97: at every start, reads the whole log and says (EventId 7010, Error) how many operator-act rows the Forum skips and the first twenty as <c>log_index type predicate</c>, never a
/// payload member. It reads only the skipped list: no Forum decision reads what it honours from here. A clean log logs nothing.
///
/// <para><b>It differs from <see cref="LifecycleEntryScan"/> on purpose.</b> That scan throws when the log cannot be read, because the Forum must not start without knowing what its guard is holding
/// back; this one reports, and a report is not a guard. A store fault at start logs a Warning (7011) and the Forum starts: the same fault stops every read the Forum serves, and an operator is told of
/// it there; refusing to start over a missing report would be the report taking the Forum down.</para>
/// </summary>
public sealed partial class OperatorActEntryScan : IHostedService
{
    private const int PageSize = 1000;

    /// <summary>How many skipped rows EventId 7010 lists.</summary>
    private const int SkippedListed = 20;

    private readonly IEventReader _events;
    private readonly LifecycleVerifier _verifier;
    private readonly ILogger<OperatorActEntryScan> _log;

    public OperatorActEntryScan(IEventReader events, LifecycleVerifier verifier, ILogger<OperatorActEntryScan> log)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(verifier);
        ArgumentNullException.ThrowIfNull(log);

        _events = events;
        _verifier = verifier;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var log = new List<AppendedEvent>();
        var after = EventSequence.Zero;

        try
        {
            while (true)
            {
                var page = await _events.ReadForwardAsync(after, PageSize, cancellationToken).ConfigureAwait(false);
                if (!page.TryGetValue(out var read, out var error))
                {
                    ScanFailed(_log, error!.Type);
                    return;
                }

                if (read.Count == 0)
                    break;

                log.AddRange(read);
                after = read[^1].Seq;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException && (e is LogUnreadableException || PostgresStoreFaults.Is(e)))
        {
            // A store's fault is thrown, not reported (R11.71). The cause is the SQLSTATE and the type name, never the driver's message.
            ScanFailed(_log, e is LogUnreadableException unreadable ? unreadable.Cause : PostgresStoreFaults.Cause(e));
            return;
        }

        var skipped = _verifier.OfLog(log).ActSkipped();
        if (skipped.Length > 0)
            RowsSkipped(_log, skipped.Length, string.Join("; ", skipped.Take(SkippedListed).Select(r => $"{r.LogIndex} {r.Type} {r.Predicate}")));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 7010,
        EventName = "OperatorActsSkipped",
        Level = LogLevel.Error,
        Message = "The Forum skips {Count} operator-act row(s) that do not verify (errata G70, R6.67); the first ones, as log_index type predicate: {Rows}")]
    private static partial void RowsSkipped(ILogger logger, long count, string rows);

    [LoggerMessage(
        EventId = 7011,
        EventName = "OperatorActScanFailed",
        Level = LogLevel.Warning,
        Message = "The log could not be read to report skipped operator-act rows ({Cause}); the Forum starts without the report (R11.97)")]
    private static partial void ScanFailed(ILogger logger, string cause);
}
