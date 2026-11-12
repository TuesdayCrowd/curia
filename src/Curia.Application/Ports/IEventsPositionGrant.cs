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
}
