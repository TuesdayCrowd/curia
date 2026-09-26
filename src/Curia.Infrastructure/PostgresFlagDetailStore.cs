using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// <see cref="IFlagDetailStore"/> over db/0004's <c>flag_details</c>: append-only by grant (R11.6),
/// ordered by event id under the "C" collation so the order is the in-memory adapter's ordinal one.
/// </summary>
public sealed class PostgresFlagDetailStore : IFlagDetailStore
{
    /// <summary>Postgres's <c>unique_violation</c>: a second row for one event.</summary>
    private const string UniqueViolation = "23505";

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _qualifiedTable;

    public PostgresFlagDetailStore(NpgsqlDataSource dataSource, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        _dataSource = dataSource;
        _qualifiedTable = SqlIdentifier.Quote(schema) + ".flag_details";
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Every awaited call carries ConfigureAwait(false); what the analyzer flags is the `await using` disposal of the connection and command, as in PostgresEventStore.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The only interpolated text is the quoted table name built in the constructor from a schema the composition root supplies; every value is a parameter.")]
    public async Task<Result<FlagDetail>> AppendAsync(FlagDetail detail, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(detail);

        if (!FlagDetailRules.Admit(detail).TryGetValue(out _, out var refusal))
            return Result<FlagDetail>.Fail(refusal!);

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"INSERT INTO {_qualifiedTable} (event_id, post_id, raised_by, rationale, salt) " +
                "VALUES (@event, @post, @raiser, @rationale, @salt);",
                connection);
            command.Parameters.Add(new NpgsqlParameter("event", NpgsqlDbType.Text) { Value = detail.EventId });
            command.Parameters.Add(new NpgsqlParameter("post", NpgsqlDbType.Text) { Value = detail.PostId });
            command.Parameters.Add(new NpgsqlParameter("raiser", NpgsqlDbType.Text) { Value = detail.RaisedBy });
            command.Parameters.Add(new NpgsqlParameter("rationale", NpgsqlDbType.Text) { Value = detail.Rationale });
            command.Parameters.Add(new NpgsqlParameter("salt", NpgsqlDbType.Text) { Value = detail.Salt });
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            return Result<FlagDetail>.Ok(detail);
        }
        catch (PostgresException e) when (e.SqlState == UniqueViolation)
        {
            return Result<FlagDetail>.Fail(FlagDetailRules.Exists(detail.EventId));
        }
        catch (PostgresException e)
        {
            // The state code only: a Postgres message can quote the value it refused.
            return Result<FlagDetail>.Fail(FlagDetailRules.Unavailable($"sqlstate={e.SqlState}"));
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "See AppendAsync.")]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "See AppendAsync.")]
    public async Task<Result<IReadOnlyList<FlagDetail>>> ReadAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"SELECT event_id, post_id, raised_by, rationale, salt FROM {_qualifiedTable} ORDER BY event_id COLLATE \"C\";",
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var rows = new List<FlagDetail>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new FlagDetail(
                    reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4)));
            }

            return Result<IReadOnlyList<FlagDetail>>.Ok(rows);
        }
        catch (PostgresException e)
        {
            return Result<IReadOnlyList<FlagDetail>>.Fail(FlagDetailRules.Unavailable($"sqlstate={e.SqlState}"));
        }
    }
}
