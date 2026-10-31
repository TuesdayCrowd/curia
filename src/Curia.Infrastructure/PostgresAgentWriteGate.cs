using System.Data;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Budget;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Npgsql;
using NpgsqlTypes;

namespace Curia.Infrastructure;

/// <summary>
/// <see cref="IAgentWriteGate"/> as the in-memory queue followed by a Postgres advisory lock (R7.27; errata G48): one agent's writes are decided and
/// recorded one at a time across every Forum process that shares the database.
///
/// <para><b>Two legs, one deadline.</b> A request first takes its turn in <see cref="AgentWriteQueue"/>, which holds no connection while it waits
/// (BRB15). The holder of the turn, and only it, opens a connection on the agent pool and takes <c>pg_try_advisory_xact_lock</c> on
/// <see cref="AgentWriteLockKey"/>, retrying every <see cref="RetryPeriod"/> on the same connection and transaction until the wait set at arrival runs
/// out. Running out in the queue or in the retries, or being granted the turn with nothing left of the wait, is <c>agent-in-flight</c> (another write of this
/// agent holds the lock, here or in another process); failing to open the connection, a pool timeout or a driver error is <c>gate-unavailable</c> (the store,
/// not the agent). The open is bounded by the store's own connection timeout and not by the wait's remainder, so the wait running out is never told as the store failing (BRB66).</para>
///
/// <para><b>Transaction-scoped</b>, as <see cref="PostgresFlagRaiserGate"/>'s lock is: the lock is released when the hold's transaction ends, and a
/// process that dies holding it releases it with its session. The hold ends the transaction first and the in-process turn second, so the next waiter
/// in this process never tries a lock that is still held by the connection it is replacing.</para>
///
/// <para><b>A pool of its own.</b> The hold keeps its connection for the whole write while the write's reads and append draw on the Forum's pool, and a
/// revision then takes the revision gate on the gate pool; the order of acquisition is agent pool, gate pool, Forum pool, never the reverse, so no cycle
/// can form (BRB35).</para>
/// </summary>
/// <remarks>The <c>schema</c> parameter exists for per-test isolation; see <see cref="PostgresEventStore"/>'s remarks. Production uses the default.</remarks>
public sealed class PostgresAgentWriteGate : IAgentWriteGate
{
    /// <summary>How long a failed try waits before the next one. A starting value: the round trip measured 0.05 ms at the median, so the period dominates a hand-off.</summary>
    internal static readonly TimeSpan RetryPeriod = TimeSpan.FromMilliseconds(20);

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _clock;
    private readonly string _schema;
    private readonly AgentWriteQueue _queue;

    public PostgresAgentWriteGate(NpgsqlDataSource dataSource, TimeProvider clock, TimeSpan wait, int waiters, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        _dataSource = dataSource;
        _clock = clock;
        _schema = schema;
        _queue = new AgentWriteQueue(clock, wait, waiters);
    }

    /// <summary>The advisory-lock key an agent's writes hold: one key per agent per schema, in a prefix of its own.</summary>
    public static string AgentWriteLockKey(string schema, AgentId agent)
    {
        ArgumentNullException.ThrowIfNull(agent);
        return schema + ":agent_write:" + agent.Value;
    }

    /// <summary>The agents the in-memory half holds an entry for now; zero when it is idle (BRB8).</summary>
    public int TrackedKeys => _queue.TrackedKeys;

    /// <summary>The schema this gate's lock keys are built in.</summary>
    internal string Schema => _schema;

    /// <summary>The data source the gate takes its lock connection from.</summary>
    internal NpgsqlDataSource DataSource => _dataSource;

    [SuppressMessage(
        "Reliability",
        "CA2007:Consider calling ConfigureAwait on the awaited task",
        Justification = "Every awaited call carries ConfigureAwait(false); what the analyzer flags is the `await using` disposal of the command, as in PostgresEventStore.")]
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "On success the turn, connection and transaction are owned by the returned Hold, which disposes all three; on every other path they are disposed here.")]
    public async Task<Result<IAsyncDisposable>> EnterAsync(AgentId agent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var taken = await _queue.TakeTurnAsync(agent, cancellationToken).ConfigureAwait(false);
        if (!taken.TryGetValue(out var turn, out var refusal)) return Result<IAsyncDisposable>.Fail(refusal!);

        // A turn granted with nothing left of the wait is a request that has not entered when its wait has passed: agent-in-flight, and no connection is opened.
        if (turn!.Remaining <= TimeSpan.Zero) return await RefuseAsync(null, null, turn, BudgetErrors.AgentInFlight()).ConfigureAwait(false);

        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        try
        {
            // The open is bounded by the store's own limits (the pool's connection timeout), not by what is left of the queue's wait: a store that fails is
            // gate-unavailable, and the wait running out is never mistaken for it.
            connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);

            using var deadline = new CancellationTokenSource(turn.Remaining, _clock);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);

            var key = AgentWriteLockKey(_schema, agent);
            while (true)
            {
                if (await TryLockAsync(connection, transaction, key, cancellationToken).ConfigureAwait(false))
                {
                    var hold = new Hold(connection, transaction, turn);
                    connection = null;
                    transaction = null;
                    turn = null;
                    return Result<IAsyncDisposable>.Ok(hold);
                }

                var left = turn.Remaining;
                if (left <= TimeSpan.Zero) return await RefuseAsync(connection, transaction, turn, BudgetErrors.AgentInFlight()).ConfigureAwait(false);

                await Task.Delay(left < RetryPeriod ? left : RetryPeriod, _clock, bounded.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the pause between tries is bounded by the deadline: the agent's other write still holds the lock.
            return await RefuseAsync(connection, transaction, turn, BudgetErrors.AgentInFlight()).ConfigureAwait(false);
        }
        catch (Exception e) when (PostgresStoreFaults.IsStoreFault(e, cancellationToken))
        {
            // NpgsqlException covers PostgresException and a pool timeout. The message is not carried: a driver message can quote what it read.
            return await RefuseAsync(connection, transaction, turn, BudgetErrors.GateUnavailable()).ConfigureAwait(false);
        }
        catch
        {
            // The caller's cancellation and anything unforeseen propagate, with what was opened and the turn given back.
            await ReleaseAsync(connection, transaction, turn).ConfigureAwait(false);
            throw;
        }
    }

    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "The analyzer flags the `await using` disposal of the command, as in PostgresEventStore.")]
    private static async Task<bool> TryLockAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string key, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT pg_try_advisory_xact_lock(hashtextextended(@lockkey, 0));", connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("lockkey", NpgsqlDbType.Text) { Value = key });
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    private static async Task<Result<IAsyncDisposable>> RefuseAsync(
        NpgsqlConnection? connection, NpgsqlTransaction? transaction, AgentWriteTurn? turn, Error error)
    {
        await ReleaseAsync(connection, transaction, turn).ConfigureAwait(false);
        return Result<IAsyncDisposable>.Fail(error);
    }

    /// <summary>
    /// Ends the transaction (which rolls back one still open and so releases the lock), returns the connection, then gives the turn back. Takes no
    /// cancellation token: a release must happen whatever the request's state. A broken session has already lost its transaction-scoped lock on the
    /// server, so the driver's and the timeout's exceptions are the one failure caught, and the turn is given back whatever happens.
    /// </summary>
    private static async ValueTask ReleaseAsync(NpgsqlConnection? connection, NpgsqlTransaction? transaction, AgentWriteTurn? turn)
    {
        try
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
        catch (Exception e) when (e is not OperationCanceledException && PostgresStoreFaults.Is(e))
        {
            // A broken session: its transaction-scoped lock was released when it broke.
        }
        finally
        {
            if (turn is not null) await turn.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>A held lock: the connection and the transaction that hold it and the turn that precedes it, released together once.</summary>
    private sealed class Hold(NpgsqlConnection connection, NpgsqlTransaction transaction, AgentWriteTurn turn) : IAsyncDisposable
    {
        private int _released;

        /// <summary>Idempotent. A failure of the session does not leave the caller's <c>await using</c>: the hold is released after the act has committed through other connections.</summary>
        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;

            await ReleaseAsync(connection, transaction, turn).ConfigureAwait(false);
        }
    }
}
