using Curia.Application.Ports;
using Curia.Application.Retrieval;

namespace Curia.Api.Adapters;

/// <summary>
/// Brings the vector index up to the log at startup (R11.10), and refuses to start without it.
///
/// <para>A Forum whose vector channel silently lagged its lexical channel would answer searches
/// with a subset of the corpus and nothing would say so; a Forum whose database has no pgvector
/// would do the same forever. Both are the failure this project's plan names -- an absence that
/// reads as a satisfied answer -- so a reconcile that cannot complete stops the host, with the
/// slug, the way a missing events database already does.</para>
/// </summary>
public sealed class EmbeddingReconcileService : IHostedService
{
    private readonly EmbeddingIndexer _indexer;
    private readonly IEventReader _events;
    private readonly IndexSweep _sweep;
    private readonly TimeProvider _clock;
    private readonly ILogger<EmbeddingReconcileService> _log;

    public EmbeddingReconcileService(EmbeddingIndexer indexer, IEventReader events, IndexSweep sweep, TimeProvider clock, ILogger<EmbeddingReconcileService> log)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(log);
        _indexer = indexer;
        _events = events;
        _sweep = sweep;
        _clock = clock;
        _log = log;
    }

    /// <summary>How many posts pass between two progress lines.</summary>
    private const int ProgressEvery = 1000;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // The reconcile is a check (errata G54, R8.83): its beginning is the instant of the last check, taken before it ran.
        var began = _clock.GetUtcNow();
        var started = _clock.GetTimestamp();

        // The first start under a model id the database holds no rows for reindexes the whole log before the Forum listens (errata G65, Amendment 1 ruling 24; Task 0 M7 and M9:
        // the port is refused for the whole reconcile, 304.7 s over 9,975 posts, 30.5 ms a post, on the build machine). It says how far it is, by count, and names no post.
        var counted = await _indexer.CountAbsentAsync(_events, cancellationToken).ConfigureAwait(false);
        var total = counted.TryGetValue(out var absent, out _) ? absent : 0;

        var reconciled = await _indexer.ReconcileAsync(
            _events,
            _sweep,
            stored =>
            {
                if (stored % ProgressEvery == 0 && stored < total) Progress(_log, stored, total, null);
            },
            cancellationToken).ConfigureAwait(false);
        if (!reconciled.TryGetValue(out var indexed, out var error))
        {
            throw new InvalidOperationException(
                $"The vector index could not be reconciled with the log ({error!.Type}: {error.Title}" +
                $"{(error.Detail is { Length: > 0 } d ? "; " + d : string.Empty)}). The Forum does not start " +
                "with a retrieval channel it cannot keep in step with the log.");
        }

        _sweep.Completed(began);
        var seconds = _clock.GetElapsedTime(started).TotalSeconds;
        var rate = seconds > 0 ? indexed / seconds : 0;
        Reconciled(_log, _indexer.Model.Id, indexed, seconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), rate.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), null);
    }

    private static readonly Action<ILogger, long, long, Exception?> Progress = LoggerMessage.Define<long, long>(
        LogLevel.Information,
        new EventId(2, "VectorIndexReconciling"),
        "Reconciling {Stored} of {Total}");

    private static readonly Action<ILogger, string, long, string, string, Exception?> Reconciled = LoggerMessage.Define<string, long, string, string>(
        LogLevel.Information,
        new EventId(1, "VectorIndexReconciled"),
        "Vector index reconciled under {Model}: {Indexed} post(s) embedded in {Seconds} s ({Rate} per second)");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
