using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Npgsql;

namespace Curia.Infrastructure;

/// <summary><see cref="IEventsPositionGrant"/> over the catalogue: <c>has_column_privilege</c> of the connected role on the store's own <c>events.seq</c> (errata G69).</summary>
public sealed class PostgresEventsPositionGrant : IEventsPositionGrant
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _qualifiedTable;

    public PostgresEventsPositionGrant(NpgsqlDataSource dataSource, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _qualifiedTable = SqlIdentifier.Quote(schema) + ".events";
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Every awaited call carries ConfigureAwait(false); the analyzer flags the `await using` disposal.")]
    public async Task<Result<bool>> RoleCanNameSeqAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand("SELECT has_column_privilege(current_user, @table::regclass, 'seq', 'INSERT');", connection);
            command.Parameters.AddWithValue("table", _qualifiedTable);
            return Result<bool>.Ok((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
        }
        catch (Exception e) when (e is not OperationCanceledException && PostgresStoreFaults.Is(e))
        {
            // The cause is the type name and the SQLSTATE, never a message (R11.71).
            return Result<bool>.Fail(new Error("curia/store/unavailable", "The store could not be asked", PostgresStoreFaults.Cause(e)));
        }
    }
}
