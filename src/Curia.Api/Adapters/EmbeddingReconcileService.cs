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
    private readonly ILogger<EmbeddingReconcileService> _log;

    public EmbeddingReconcileService(EmbeddingIndexer indexer, IEventReader events, ILogger<EmbeddingReconcileService> log)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(log);
        _indexer = indexer;
        _events = events;
        _log = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var reconciled = await _indexer.ReconcileAsync(_events, cancellationToken).ConfigureAwait(false);
        if (!reconciled.TryGetValue(out var indexed, out var error))
        {
            throw new InvalidOperationException(
                $"The vector index could not be reconciled with the log ({error!.Type}: {error.Title}" +
                $"{(error.Detail is { Length: > 0 } d ? "; " + d : string.Empty)}). The Forum does not start " +
                "with a retrieval channel it cannot keep in step with the log.");
        }

        Reconciled(_log, _indexer.Model.Id, indexed, null);
    }

    private static readonly Action<ILogger, string, long, Exception?> Reconciled = LoggerMessage.Define<string, long>(
        LogLevel.Information,
        new EventId(1, "VectorIndexReconciled"),
        "Vector index reconciled under {Model}: {Indexed} post(s) embedded at startup");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
