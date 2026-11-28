using System.Collections.Immutable;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Reads;

/// <summary>The flags of the rows <paramref name="wanted"/> accepts, and the end of the log from before they were read.</summary>
/// <param name="Flags">The joined flags: those of the wanted rows, and every flag of the legacy form (a caller filters those).</param>
/// <param name="After">The greatest <c>seq</c> of the log in a snapshot taken <b>before</b> the private store was read: an append guard's <c>After</c>, so that a flag whose row and entry landed after it is in the guard's delta and not missed.</param>
public sealed record FlagsRead(ImmutableArray<RaisedFlag> Flags, EventSequence After);

/// <summary>
/// The flags a request decides on, joined from the log's commitments and the private store (R7.18, R10.62), read for the flags it names and not for the log (errata G87, R11.113). The private store
/// is a table bounded by the flags raised; the log is read for the entries those rows open and for the flags that predate R10.62 (none in a log written since). The join is
/// <see cref="FlagDirectory.Join"/>'s, so a flag is listed or skipped exactly as the whole-log join lists or skips it.
/// </summary>
public static class FlagReads
{
    /// <summary>
    /// Reads the flags whose private row <paramref name="wanted"/> accepts. The order is the guard's: the instant of the log is taken first, then the private store, then the entries those rows
    /// open, so no flag whose private row was read can have its entry missing for want of a read, and one that landed in between is after <see cref="FlagsRead.After"/>.
    /// </summary>
    public static async Task<Result<FlagsRead>> ReadAsync(
        IPositionedEventReader reader, IFlagDetailStore details, Func<FlagDetail, bool> wanted, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(wanted);

        var start = await reader.ReadAsync(ReadPlan.Of(), cancellationToken).ConfigureAwait(false);

        var rows = await details.ReadAllAsync(cancellationToken).ConfigureAwait(false);
        if (!rows.TryGetValue(out var all, out var error))
            return Result<FlagsRead>.Fail(error!);

        var picked = all!.Where(wanted).ToArray();
        var slice = await reader.ReadAsync(ReadPlan.Of(ReadClause.Entries(picked.Select(d => d.EventId)), ReadClause.LegacyFlags()), cancellationToken).ConfigureAwait(false);

        var directory = FlagDirectory.Join([.. slice.Rows.Select(r => r.Event)], picked);
        return Result<FlagsRead>.Ok(new FlagsRead(directory.Flags, start.HighWater));
    }
}
