using System.Data;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// <see cref="IRevisionGate"/> as a Postgres advisory lock (R8.77; errata G45), the shape of <see cref="PostgresFlagRaiserGate"/>: one post's
/// revisions are decided and recorded one at a time, across every Forum process sharing the database.
///
/// <para><b>Transaction-scoped, try, never wait.</b> The lock is <c>pg_try_advisory_xact_lock</c>, taken in a transaction the hold owns and that
/// writes nothing; it is released when that transaction ends, so a process that crashes holding it releases it with its session. A refused entry
/// gives its connection back at once, because a blocking lock would hold a pooled connection for every waiting request.</para>
///
/// <para><b>Per database and per kind.</b> The key is <see cref="RevisionLockKey"/>: the schema, a prefix of its own and the digest, so two
/// isolated test schemas are two gates and the key cannot collide with the flag gate's, the key store's or the event store's. The gate pool is
/// the flag gate's (<see cref="PostgresAdapters"/>); a hold keeps its connection while the Forum's own pool does the reads and the append, so the
/// order of acquisition, gate before Forum, rules out a deadlock between them.</para>
///
/// <para><b>The gate writes nothing and takes no event store</b> (CS-15's spirit, ruling 12): it is a data source and a key.</para>
/// </summary>
public sealed class PostgresRevisionGate : IRevisionGate
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;

    public PostgresRevisionGate(NpgsqlDataSource dataSource, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        _dataSource = dataSource;
        _schema = schema;
    }

    /// <summary>The advisory-lock key a revision of <paramref name="prevDigest"/> holds: one key per digest per schema, in a prefix of its own.</summary>
    public static string RevisionLockKey(string schema, string prevDigest) => schema + ":revision_prev:" + prevDigest;

    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Every awaited call carries ConfigureAwait(false); what the analyzer flags is the `await using` disposal of the command, as in PostgresEventStore.")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "On success the connection and transaction are owned by the returned Hold, which disposes both; on every other path they are disposed here.")]
    public async Task<Result<IAsyncDisposable>> TryEnterAsync(string prevDigest, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prevDigest);

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
                command.Parameters.Add(new NpgsqlParameter("lockkey", NpgsqlDbType.Text) { Value = RevisionLockKey(_schema, prevDigest) });
                entered = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
            }

            if (!entered)
            {
                await ReleaseAsync(connection, transaction).ConfigureAwait(false);
                return Result<IAsyncDisposable>.Fail(IngestErrors.RevisionInFlight());
            }

            var hold = new Hold(connection, transaction);
            connection = null;
            transaction = null;
            return Result<IAsyncDisposable>.Ok(hold);
        }
        catch (Exception e) when (e is not OperationCanceledException && PostgresStoreFaults.Is(e))
        {
            // NpgsqlException covers PostgresException and a pool timeout, whose inner exception is a
            // TimeoutException. The message is not carried: a driver message can quote what it read.
            await ReleaseAsync(connection, transaction).ConfigureAwait(false);
            return Result<IAsyncDisposable>.Fail(IngestErrors.RevisionGateUnavailable());
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
        /// released after the revision's append has committed through its own connections, so a throw
        /// here would answer 500 for a revision that was recorded. Disposing an open transaction issues a
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
            catch (Exception e) when (e is not OperationCanceledException && PostgresStoreFaults.Is(e))
            {
                // A broken session: its transaction-scoped lock was released when it broke (above).
            }
        }
    }
}
