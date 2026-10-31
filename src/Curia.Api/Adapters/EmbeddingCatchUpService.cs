using Curia.Application.Ports;
using Curia.Application.Retrieval;

namespace Curia.Api.Adapters;

/// <summary>
/// Brings a post whose vector is missing into the index while the Forum is up (R11.69, errata G51). A thin loop on the clock port: every second (provisional,
/// so a retry that falls due begins no later than a second after) it hands the instant to <see cref="EmbeddingCatchUp"/>, which holds the schedule and the work.
/// A fault in a pass is logged and the loop goes on; it appends no event.
/// </summary>
public sealed class EmbeddingCatchUpService : BackgroundService
{
    /// <summary>How often the loop looks at the backlog (provisional; R11.69 publishes it).</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(1);

    private readonly EmbeddingCatchUp _catchUp;
    private readonly IEventReader _events;
    private readonly TimeProvider _clock;
    private readonly ILogger<EmbeddingCatchUpService> _log;

    public EmbeddingCatchUpService(EmbeddingCatchUp catchUp, IEventReader events, TimeProvider clock, ILogger<EmbeddingCatchUpService> log)
    {
        ArgumentNullException.ThrowIfNull(catchUp);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);
        _catchUp = catchUp;
        _events = events;
        _clock = clock;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Tick, _clock, stoppingToken).ConfigureAwait(false);
                await PassAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PassAsync(CancellationToken stoppingToken)
    {
        try
        {
            var result = await _catchUp.RunOnceAsync(_events, _clock.GetUtcNow(), stoppingToken).ConfigureAwait(false);
            foreach (var digest in result.StoredDigests)
                VectorIndexLog.CaughtUp(_log, digest);
            foreach (var fault in result.FailedRetries)
                VectorIndexLog.RetryFailed(_log, fault.PostId, fault.Digest, fault.FaultType, fault.FaultDetail);
        }
#pragma warning disable CA1031 // A pass that faults must not end the loop: the next pass retries, and the backlog still holds the entries.
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
#pragma warning restore CA1031
        {
            VectorIndexLog.PassFailed(_log, ex.GetType().FullName ?? ex.GetType().Name, ex.Message);
        }
    }
}
