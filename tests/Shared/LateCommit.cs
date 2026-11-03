using System.Diagnostics.CodeAnalysis;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Tests.Shared;

/// <summary>
/// Errata G57's seam (plan Task 0c): a lock holder. A connection as the application role (R11.6's grants apply to it; traps 84 and 86) that begins a transaction, takes
/// the store's whole-log lock by its own key and inserts events in the shape <c>PostgresEventStore</c> writes, then commits or rolls back on command. A probe puts a
/// late commit between a request's read and its append with no proxy and no sleep (trap 90): the request's own append queues behind this transaction exactly as it
/// queues behind a stalled commit in the live drill, and finds the late events when the holder commits.
/// </summary>
internal sealed class LateCommitHolder : IAsyncDisposable
{
    private readonly NpgsqlConnection _connection;
    private readonly NpgsqlTransaction _transaction;
    private readonly string _table;
    private bool _finished;

    private LateCommitHolder(NpgsqlConnection connection, NpgsqlTransaction transaction, string table)
    {
        _connection = connection;
        _transaction = transaction;
        _table = table;
    }

    /// <summary>The store's lock key for a schema, which the store reads from <c>PostgresEventStore.SerializationLockKey</c>; spelled here as the store spells it, so a drift fails the probes that use it.</summary>
    internal static string LockKey(string schema) => schema + ":acta";

    /// <summary>Opens the connection, begins the transaction and takes the whole-log lock. From here until commit or rollback every append to the log waits.</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only interpolated text is the schema name, a literal or a GUID's hex digits; every value is bound as a parameter.")]
    internal static async Task<LateCommitHolder> BeginAsync(NpgsqlDataSource applicationRole, string schema, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applicationRole);
        var connection = await applicationRole.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@key, 0));", connection, transaction))
        {
            take.Parameters.AddWithValue("key", LockKey(schema));
            await take.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        return new LateCommitHolder(connection, transaction, "\"" + schema + "\".events");
    }

    /// <summary>Inserts one event in the transaction. Returns its <c>seq</c>; nothing is visible to another session until <see cref="CommitAsync"/>.</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only interpolated text is the table name built from the schema; every value is bound as a parameter.")]
    internal async Task<long> InsertAsync(string eventId, string eventType, string aggregateId, string? actorId, string payloadJson, DateTimeOffset serverTs, CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            $"INSERT INTO {_table} (event_id, event_type, aggregate_id, actor_id, payload, server_ts) VALUES (@id, @type, @agg, @actor, @payload, @ts) RETURNING seq;",
            _connection,
            _transaction);
        insert.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Text) { Value = eventId });
        insert.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text) { Value = eventType });
        insert.Parameters.Add(new NpgsqlParameter("agg", NpgsqlDbType.Text) { Value = aggregateId });
        insert.Parameters.Add(new NpgsqlParameter("actor", NpgsqlDbType.Text) { Value = (object?)actorId ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Jsonb) { Value = payloadJson });
        insert.Parameters.Add(new NpgsqlParameter("ts", NpgsqlDbType.TimestampTz) { Value = serverTs.UtcDateTime });
        return Convert.ToInt64(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Runs one statement in the transaction (a private row, a bulk filler of events); returns the rows affected.</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every caller passes a constant statement and binds its values as parameters.")]
    internal async Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, _connection, _transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Commits: the inserted events become visible and the lock is released.</summary>
    internal async Task CommitAsync(CancellationToken cancellationToken)
    {
        _finished = true;
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rolls back: the inserted events vanish (their sequence values are not reused, which leaves a gap) and the lock is released.</summary>
    internal async Task RollbackAsync(CancellationToken cancellationToken)
    {
        _finished = true;
        await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many sessions are waiting on an advisory lock in this database right now (the append queued behind the holder).</summary>
    internal static async Task<long> WaitersAsync(NpgsqlConnection anySession, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(anySession);
        await using var probe = new NpgsqlCommand(
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database());",
            anySession);
        return Convert.ToInt64(await probe.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished)
        {
            try
            {
                await _transaction.RollbackAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // a holder abandoned by a failing probe must not mask the failure
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
