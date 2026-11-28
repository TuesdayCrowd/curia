using Curia.Application.Ports;
using Curia.Application.Retrieval;

namespace Curia.Api.Adapters;

/// <summary>
/// Errata G88, R11.114, R11.116: before the Forum listens it settles that the keyword channel's index is applied, written under this runtime's tokenizer, and complete, in the order
/// <see cref="SearchIndexStartCheck"/> states, and <b>does not start</b> on any refusal. A Forum started over a partial index would drop posts from search and say nothing (the failure the embedding
/// reconcile exists for), and a Forum without the tables would refuse every post it was asked to store. The messages carry no connection string and no role name.
/// </summary>
public sealed partial class SearchIndexStartService(IEventReader events, ISearchIndex index, SearchIndexRules rules, ILogger<SearchIndexStartService> log) : IHostedService
{
    /// <summary>How many posts a reconcile writes between two progress lines.</summary>
    private const int ProgressEvery = 1000;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        long last = 0;
        var answer = await SearchIndexStartCheck.RunAsync(
            events,
            index,
            rules,
            inserted =>
            {
                if (inserted - last >= ProgressEvery) { last = inserted; Reconciling(log, inserted); }
            },
            cancellationToken).ConfigureAwait(false);

        if (!answer.TryGetValue(out var report, out var error))
        {
            Refused(log, error!.Type);
            throw new InvalidOperationException(
                $"The keyword channel's search index is not usable ({error.Type}: {error.Title}{(error.Detail is { Length: > 0 } d ? "; " + d : string.Empty)}). The Forum does not start without it (R11.114, R11.116).");
        }

        if (report!.Schema.CanUpdateOrDelete) UpdateGranted(log);
        Reconciled(log, report.Reconcile.Posts, report.Reconcile.Inserted);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 7016,
        EventName = "SearchIndexReconciled",
        Level = LogLevel.Information,
        Message = "The search index holds rows for every discussion post: {Posts} post(s), {Inserted} written by this start's reconcile.")]
    private static partial void Reconciled(ILogger logger, long posts, long inserted);

    [LoggerMessage(
        EventId = 7017,
        EventName = "SearchIndexUpdatable",
        Level = LogLevel.Error,
        Message = "The role this Forum connects as can UPDATE, DELETE or TRUNCATE a table of the search index: db/0008 is not applied as written, or the role is a superuser or the tables' owner. A derived row the Forum cannot change is a row the application role could not have planted; this one could (R11.114, errata G88). The Forum starts anyway.")]
    private static partial void UpdateGranted(ILogger logger);

    [LoggerMessage(
        EventId = 7018,
        EventName = "SearchIndexRefused",
        Level = LogLevel.Error,
        Message = "The search index is not usable ({Type}). The Forum does not start (R11.114, R11.116, errata G88): the database's owner applies db/0008, or the index version's rules are written again as a new version.")]
    private static partial void Refused(ILogger logger, string type);

    [LoggerMessage(
        EventId = 7019,
        EventName = "SearchIndexReconciling",
        Level = LogLevel.Information,
        Message = "Writing the search index from the log: {Inserted} post(s) so far.")]
    private static partial void Reconciling(ILogger logger, long inserted);
}
