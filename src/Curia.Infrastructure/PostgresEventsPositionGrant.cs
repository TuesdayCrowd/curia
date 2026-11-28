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

    private static string Writable(string column) =>
        $"(CASE WHEN EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = @table::regclass AND attname = '{column}' AND NOT attisdropped) " +
        $"THEN has_column_privilege(current_user, @table::regclass, '{column}', 'INSERT') ELSE false END)";

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "Every awaited call carries ConfigureAwait(false); the analyzer flags the `await using` disposal.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only concatenated text is Writable's two column names, string literals of this file; the table is a bound parameter.")]
    public async Task<Result<bool>> RoleCanWriteLeafAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            // Each column is looked up first and its privilege asked only where it exists: has_column_privilege raises on a column that is not there, and "not there" is the answer.
            // Both columns db/0007 adds are written by every append (the leaf, and the signal target of a vote or verification, R11.6 (add.)).
            await using var command = new NpgsqlCommand(
                "SELECT " + Writable("leaf_hash") + " AND " + Writable("signal_target") + ";",
                connection);
            command.Parameters.AddWithValue("table", _qualifiedTable);
            return Result<bool>.Ok((bool)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!);
        }
        catch (Exception e) when (e is not OperationCanceledException && PostgresStoreFaults.Is(e))
        {
            return Result<bool>.Fail(new Error("curia/store/unavailable", "The store could not be asked", PostgresStoreFaults.Cause(e)));
        }
    }
}
