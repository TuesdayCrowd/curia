using Curia.Application.Ports;
using Curia.Application.Retrieval;

namespace Curia.Api.Adapters;

/// <summary>The row of <see cref="RetrievalRows"/> for the model this Forum serves, or none (errata G66, R11.102), resolved once.</summary>
public sealed record ServedRetrievalRow(RetrievalRow? Row);

/// <summary>
/// Errata G66, R11.102, R8.91: at every start, says so once when the served model has no row of retrieval numbers (EventId 7012: the near-duplicate check and the vector floor are off), and once when
/// its row carries thresholds that are the hashed era's and not derived under that model (EventId 7013). A row whose thresholds are derived logs neither. Neither stops the Forum: an id with
/// no row refuses nothing and removes nothing, and a carried row is the numbers the Forum applied before. The messages name the model id the Forum itself chose and carry no path or credential.
/// </summary>
public sealed partial class RetrievalRowNote(ServedRetrievalRow served, ITextEmbedder embedder, ILogger<RetrievalRowNote> log) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (served.Row is null) NoRow(log, embedder.Model.Id);
        else if (!served.Row.ThresholdsDerived) CarriedThresholds(log, served.Row.ModelId);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 7012,
        EventName = "RetrievalModelHasNoRow",
        Level = LogLevel.Warning,
        Message = "The embedding model {ModelId} has no row of retrieval numbers (errata G66, R11.102): the near-duplicate check refuses and annotates nothing and the vector floor removes nothing, until a row is added")]
    private static partial void NoRow(ILogger logger, string modelId);

    [LoggerMessage(
        EventId = 7013,
        EventName = "RetrievalThresholdsCarried",
        Level = LogLevel.Warning,
        Message = "The near-duplicate thresholds for {ModelId} are carried from the hashed era and not derived under that model (errata G66, R8.91)")]
    private static partial void CarriedThresholds(ILogger logger, string modelId);
}
