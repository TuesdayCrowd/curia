using Curia.Application.Budget;
using Curia.Application.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Npgsql;

namespace Curia.Infrastructure;

/// <summary>
/// <see cref="IAgentWriteGate"/> as the in-memory queue followed by a Postgres advisory lock (R7.27; errata G48): one agent's writes are decided and
/// recorded one at a time across every Forum process that shares the database.
///
/// <para><b>Task 0 seam.</b> This gate admits every entry at once and takes no lock. It exists so that the port, the lock key and the registration are
/// declared and the probes fail by assertion; Task 2 gives it the queue and the lock.</para>
/// </summary>
public sealed class PostgresAgentWriteGate : IAgentWriteGate
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly AgentWriteQueue _queue;

    public PostgresAgentWriteGate(NpgsqlDataSource dataSource, TimeProvider clock, TimeSpan wait, int waiters, string schema = "public")
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);

        _dataSource = dataSource;
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

    public Task<Result<IAsyncDisposable>> EnterAsync(AgentId agent, CancellationToken cancellationToken) =>
        _queue.EnterAsync(agent, cancellationToken);
}
