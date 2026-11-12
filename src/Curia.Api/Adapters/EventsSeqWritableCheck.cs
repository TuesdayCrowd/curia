using Curia.Application.Ports;

namespace Curia.Api.Adapters;

/// <summary>
/// Errata G69, R11.6 (add.): at every start, says so when the role this Forum connects as can name an event's <c>seq</c>, and starts either way. A refusal would break a
/// developer's superuser connection and every database that has not applied <c>db/0006</c>; the log is the class of <see cref="LifecycleEntryScan"/>'s: a fact the operator
/// has to hear. A store that cannot be asked is a Warning and also starts. The messages carry no connection string and no role name.
/// </summary>
public sealed partial class EventsSeqWritableCheck(IEventsPositionGrant grant, ILogger<EventsSeqWritableCheck> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var answer = await grant.RoleCanNameSeqAsync(cancellationToken).ConfigureAwait(false);
        if (!answer.TryGetValue(out var writable, out var error)) Unchecked(log, error!.Type);
        else if (writable) SeqIsWritable(log);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 7008,
        EventName = "EventsSeqWritable",
        Level = LogLevel.Error,
        Message = "The role this Forum connects as can name an event's seq (INSERT on events.seq): db/0006 is not applied to this database, or the role is a superuser, the table's owner or inherits a table-level INSERT. A row placed at a chosen position is not a row the Forum appended (R11.6 (add.), errata G69). The Forum starts anyway.")]
    private static partial void SeqIsWritable(ILogger logger);

    [LoggerMessage(
        EventId = 7009,
        EventName = "EventsSeqWritableUnchecked",
        Level = LogLevel.Warning,
        Message = "Whether the role this Forum connects as can name an event's seq could not be checked ({Type}). The Forum starts anyway.")]
    private static partial void Unchecked(ILogger logger, string type);
}
