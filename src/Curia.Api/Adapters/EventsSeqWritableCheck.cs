using Curia.Application.Ports;

namespace Curia.Api.Adapters;

/// <summary>
/// Errata G69, R11.6 (add.), and R6.69: at every start, says so when the role this Forum connects as can name an event's <c>seq</c>, and starts either way. A refusal would break a
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

        // R6.69, db/0007: unlike db/0006's grant, this one is not a property the Forum can run without. Every append writes the row's leaf in its one INSERT, so a database without the column
        // refuses the first write. The Forum says so at start and does not start, rather than serve reads and fail every write (an unchecked store, as above, still starts).
        var leaf = await grant.RoleCanWriteLeafAsync(cancellationToken).ConfigureAwait(false);
        if (!leaf.TryGetValue(out var canWrite, out var leafError)) LeafUnchecked(log, leafError!.Type);
        else if (!canWrite)
        {
            LeafColumnMissing(log);
            throw new InvalidOperationException("db/0007 is not applied to this database: events.leaf_hash is missing or the role holds no INSERT on it (R6.69). The owner applies it; see the README.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(
        EventId = 7008,
        EventName = "EventsSeqWritable",
        Level = LogLevel.Error,
        Message = "The role this Forum connects as can name an event's seq (INSERT on events.seq): db/0006 is not applied to this database, or the role is a superuser, the table's owner or inherits a table-level INSERT. A row placed at a chosen position is not a row the Forum appended (R11.6 (add.), errata G69). The Forum starts anyway.")]
    private static partial void SeqIsWritable(ILogger logger);

    [LoggerMessage(
        EventId = 7014,
        EventName = "EventsLeafColumnMissing",
        Level = LogLevel.Error,
        Message = "events.leaf_hash is missing, or the role this Forum connects as holds no INSERT on it: db/0007 is not applied to this database (R6.69, errata G87). Every append writes the row's leaf hash, so this Forum would refuse every write. The Forum does not start; the database's owner applies db/0007 and runs curia-operator backfill-leaves (README).")]
    private static partial void LeafColumnMissing(ILogger logger);

    [LoggerMessage(
        EventId = 7015,
        EventName = "EventsLeafColumnUnchecked",
        Level = LogLevel.Warning,
        Message = "Whether db/0007's events.leaf_hash is writable by the role this Forum connects as could not be checked ({Type}). The Forum starts anyway.")]
    private static partial void LeafUnchecked(ILogger logger, string type);

    [LoggerMessage(
        EventId = 7009,
        EventName = "EventsSeqWritableUnchecked",
        Level = LogLevel.Warning,
        Message = "Whether the role this Forum connects as can name an event's seq could not be checked ({Type}). The Forum starts anyway.")]
    private static partial void Unchecked(ILogger logger, string type);
}
