using System.Data;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// <see cref="IFlagRaiserGate"/> as a Postgres advisory lock (R7.22, R10.70; errata G18, review of
/// 4b3e91a): one raiser's flags are counted and recorded one at a time, across every Forum process
/// sharing the database.
///
/// <para><b>Transaction-scoped.</b> The lock is <c>pg_try_advisory_xact_lock</c>, taken in a
/// transaction the hold owns and that writes nothing; it is released when that transaction ends, so a
/// process that crashes holding it releases it with its session, and no lock outlives its holder.</para>
///
/// <para><b>Per database</b>, as <see cref="PostgresAgentKeyStore"/>'s enrollment lock is: the key is
/// <see cref="FlagRaiserLockKey"/>, prefixed by the schema, so two isolated test schemas are two gates
/// and the key cannot collide with the key store's or the event store's.</para>
///
/// <para><b>Try, never wait.</b> A blocking <c>pg_advisory_xact_lock</c> would hold one pooled
/// connection for every waiting request, so one identity sending a burst of flags could drain the
/// pool for every agent. A refused entry gives its connection back at once.</para>
///
/// <para><b>A pool of its own</b> (review of 980fb0e). <see cref="PostgresAdapters"/> builds this gate
/// on a data source of its own, a quarter of the Forum's pool, because a hold keeps its connection
/// while the flag's reads and append draw on the Forum's. The try-lock bounds one raiser to one flag in
/// flight; the separate pool bounds the number of raisers in flight, and keeps the order of acquisition
/// gate before Forum, so the two pools cannot deadlock each other.</para>
/// </summary>
/// <remarks>
/// The <c>schema</c> parameter exists for per-test isolation; see <see cref="PostgresEventStore"/>'s
/// remarks. Production uses the default.
/// </remarks>
public sealed class PostgresFlagRaiserGate : IFlagRaiserGate
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;

    public PostgresFlagRaiserGate(NpgsqlDataSource dataSource, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        _dataSource = dataSource;
        _schema = schema;
    }

    /// <summary>The advisory-lock key a flag by <paramref name="raisedBy"/> holds: one key per raiser per schema.</summary>
    public static string FlagRaiserLockKey(string schema, string raisedBy) => schema + ":flag_raiser:" + raisedBy;

    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Every awaited call carries ConfigureAwait(false); what the analyzer flags is the `await using` disposal of the command, as in PostgresEventStore.")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "On success the connection and transaction are owned by the returned Hold, which disposes both; on every other path they are disposed here.")]
    public async Task<Result<IAsyncDisposable>> TryEnterAsync(string raisedBy, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(raisedBy);

        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        try
        {
            connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

            bool entered;
            await using (var command = new NpgsqlCommand(
                "SELECT pg_try_advisory_xact_lock(hashtextextended(@lockkey, 0));", connection, transaction))
            {
                command.Parameters.Add(new NpgsqlParameter("lockkey", NpgsqlDbType.Text) { Value = FlagRaiserLockKey(_schema, raisedBy) });
                entered = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
            }

            if (!entered)
            {
                await ReleaseAsync(connection, transaction).ConfigureAwait(false);
                return Result<IAsyncDisposable>.Fail(FlagErrors.RaiseInFlight());
            }

            var hold = new Hold(connection, transaction);
            connection = null;
            transaction = null;
            return Result<IAsyncDisposable>.Ok(hold);
        }
        catch (NpgsqlException)
        {
            // NpgsqlException covers PostgresException and a pool timeout, whose inner exception is a
            // TimeoutException. The message is not carried: a driver message can quote what it read.
            await ReleaseAsync(connection, transaction).ConfigureAwait(false);
            return Result<IAsyncDisposable>.Fail(FlagErrors.RaiserGateUnavailable());
        }
        catch (TimeoutException)
        {
            await ReleaseAsync(connection, transaction).ConfigureAwait(false);
            return Result<IAsyncDisposable>.Fail(FlagErrors.RaiserGateUnavailable());
        }
        catch
        {
            // OperationCanceledException and anything unforeseen propagate, with what was opened disposed.
            await ReleaseAsync(connection, transaction).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Disposes the transaction, which rolls back one still open and so releases the lock, then the
    /// connection. Takes no cancellation token: a release must happen whatever the request's state.
    /// </summary>
    private static async ValueTask ReleaseAsync(NpgsqlConnection? connection, NpgsqlTransaction? transaction)
    {
        try
        {
            if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            if (connection is not null) await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A held lock: the connection and the transaction that hold it, released together once.</summary>
    private sealed class Hold(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        private int _released;

        /// <summary>
        /// Releases the lock by ending its transaction, which wrote nothing, and returns the connection
        /// to the pool. Idempotent.
        ///
        /// <para><b>A failure here does not leave the caller's <c>await using</c>.</b> The hold is
        /// released after the flag's append has committed through its own connections, so a throw
        /// here would answer 500 for a flag that was recorded. Disposing an open transaction issues a
        /// rollback, which throws only when the session is already broken; the lock is
        /// transaction-scoped, so a broken session has already released it on the server, and nothing
        /// is left held. That is the one failure caught, and only the driver's and the timeout's
        /// exceptions are; anything else propagates.</para>
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            try
            {
                await ReleaseAsync(connection, transaction).ConfigureAwait(false);
            }
            catch (NpgsqlException)
            {
                // A broken session: its transaction-scoped lock was released when it broke (above).
            }
            catch (TimeoutException)
            {
                // As above.
            }
        }
    }
}
