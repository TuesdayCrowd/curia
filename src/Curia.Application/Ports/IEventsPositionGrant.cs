using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

/// <summary>
/// Errata G69, R11.6 (add.): whether the role the Forum connects as can name an event's <c>seq</c>, which the database alone assigns. True means a placed row is possible
/// (the migration is not applied, the connection is a superuser's or the table owner's, or a table-level <c>INSERT</c> was restored); false means the grant holds. A
/// failed result is a store that could not be asked, never an answer about the grant.
/// </summary>
public interface IEventsPositionGrant
{
    /// <summary>True where the connected role holds <c>INSERT</c> on <c>events.seq</c>.</summary>
    Task<Result<bool>> RoleCanNameSeqAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// R6.69, db/0007: true where <c>events.leaf_hash</c> and <c>events.signal_target</c> exist and the connected role holds <c>INSERT</c> on both, which every append of this build needs: its one INSERT writes
    /// the row's leaf and a vote's or verification's signal target (R11.6 (add.)). False means db/0007 is not applied to this database (or its grant was lost); the append would fail on the first request, so the Forum does not start. A failed result is a
    /// store that could not be asked.
    /// </summary>
    Task<Result<bool>> RoleCanWriteLeafAsync(CancellationToken cancellationToken = default);
}
