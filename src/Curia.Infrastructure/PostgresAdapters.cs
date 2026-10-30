using Curia.Application.Ports;
using Curia.AuthN.Ports;
using Npgsql;

namespace Curia.Infrastructure;

/// <summary>
/// Every Postgres-backed adapter, behind one factory, so a host project never names a database type.
///
/// <para><b>Why this exists.</b> The scoping document's CS-7 says "nothing outside Infrastructure
/// references Npgsql, NSec, OpenIddict, or ONNX types", and <c>Curia.Api</c> was doing exactly that
/// — constructing an <c>NpgsqlDataSource</c> and passing it to four constructors. The architecture
/// test that enforces CS-7 iterates the hexagon assemblies and does not include host projects, so
/// the rule was stated, violated, and green.</para>
///
/// <para>A composition root has to wire adapters, but it does not have to know what they are made
/// of. It hands over a connection string and receives ports. The one type that owns the data source
/// is here, where the rule already permits it, and <c>Curia.Api</c>'s <c>using Npgsql</c> goes
/// away — which is what makes the architecture test extendable to host projects rather than
/// permanently scoped around a known violation.</para>
///
/// <para>Disposable because <see cref="NpgsqlDataSource"/> owns a connection pool. A composition
/// root registering this as a singleton gets the pool's lifetime tied to the container's, which is
/// the behaviour a long-running host wants and the reason the data source is not created per call.</para>
/// </summary>
public sealed class PostgresAdapters : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly NpgsqlDataSource _gateDataSource;
    private readonly NpgsqlDataSource _agentDataSource;
    private readonly TimeProvider _clock;
    private readonly ILifecycleAppendGate _lifecycleGate;
    private readonly PostgresAgentWriteGate _agentWriteGate;

    public PostgresAdapters(string connectionString, TimeProvider clock, ILifecycleAppendGate lifecycleGate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(lifecycleGate);

        // R7.27 (errata G48, D91): the configured Maximum Pool Size is the total one Forum process holds, carved into three pools: the agent write
        // gate's (a quarter), the raiser and revision gates' (a quarter, as R7.22 built it) and the Forum's (the rest). Each is at least 1 and the Forum's at
        // least 2, so a size below 4 is refused here, before any data source is built and so with nothing to dispose. The sum over every Forum process and
        // operator tool sharing a database must stay within its max_connections less the reserved ones (README).
        var main = new NpgsqlConnectionStringBuilder(connectionString);
        if (main.MaxPoolSize < 4)
            throw new InvalidOperationException(
                "The agent write gate and the raiser gate each need a connection pool of their own, carved from the Forum's (R7.22, R7.27): Maximum Pool Size must be at least 4.");

        var quarter = main.MaxPoolSize / 4;
        var forum = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = main.MaxPoolSize - (2 * quarter) };
        var gate = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = quarter, MinPoolSize = 0 };
        var agent = new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = quarter, MinPoolSize = 0 };

        _dataSource = NpgsqlDataSource.Create(forum.ConnectionString);
        _gateDataSource = NpgsqlDataSource.Create(gate.ConnectionString);
        _agentDataSource = NpgsqlDataSource.Create(agent.ConnectionString);
        _clock = clock;
        _lifecycleGate = lifecycleGate;
        _agentWriteGate = new PostgresAgentWriteGate(_agentDataSource, clock, Curia.Application.Budget.AgentWriteGateBounds.Wait, Curia.Application.Budget.AgentWriteGateBounds.Waiters);
    }

    /// <summary>The append-only event log (R11.6), which is the system of record.</summary>
    public IEventStore EventStore => new PostgresEventStore(_dataSource, _clock, _lifecycleGate);

    /// <summary>
    /// The read half. CS-15: a component typed to this has no member reaching the write surface, so
    /// a read path cannot append even by accident.
    /// </summary>
    public IEventReader EventReader => new PostgresEventStore(_dataSource, _clock, _lifecycleGate);

    /// <summary>R5.17's replay cache, shared across instances rather than per process.</summary>
    public IReplayCache ReplayCache => new PostgresReplayCache(_dataSource, _clock);

    /// <summary>R5.19's DPoP nonce store, epoch-keyed so instances agree without coordination.</summary>
    public IDpopNonceStore DpopNonceStore => new PostgresDpopNonceStore(_dataSource, _clock);

    /// <summary>
    /// The Registrar's key store, which satisfies three ports at once.
    ///
    /// <para>One store, three interfaces, because <c>Curia.Application</c> and <c>Curia.AuthN</c>
    /// cannot see each other and each declares the capability it needs. Exposed as a concrete type
    /// rather than as three properties so a caller cannot accidentally wire two different instances
    /// and wonder why a key registered through one does not resolve through another.</para>
    /// </summary>
    public PostgresAgentKeyStore AgentKeys => new(_dataSource);

    public IVectorIndex VectorIndex => new PostgresVectorIndex(_dataSource, _clock);

    /// <summary>The private half of every flag (R10.62, R11.32): append-only, like the event log it is bound to.</summary>
    public IFlagDetailStore FlagDetails => new PostgresFlagDetailStore(_dataSource);

    /// <summary>
    /// R7.22, R10.70 (errata G18): one raiser's flags counted and recorded one at a time, by a
    /// try-only advisory lock, on a connection pool of its own.
    ///
    /// <para><b>Why a pool of its own</b> (review of 980fb0e). A hold keeps a gate connection while the
    /// flag's reads and append draw on the Forum's pool. Were the two one pool, as many raisers in
    /// flight as the pool holds would each keep a connection and wait for another, and the pool would
    /// deadlock until the connection timeout, every flag inside answering 500 and every other route
    /// stalled. So the <see cref="PostgresFlagRaiserGate"/> is built on <c>_gateDataSource</c>, a quarter
    /// of the Forum's pool: acquisition always runs gate before Forum, never the reverse, so no cycle can
    /// form and a fleet of raisers as large as either pool cannot deadlock it. At most that quarter of
    /// the Forum's connections is ever held by flags, one each, leaving the rest to every other route.</para>
    ///
    /// <para>A gate pool that is exhausted answers 503 <c>curia/flag/raiser-gate-unavailable</c> after
    /// the connection timeout, and holds no Forum connection while it waits. Advisory locks are per
    /// database, so the gate's own data source shares lock space with any other process's gate, as
    /// <c>R7_22_TheHoldIsSeenAcrossTwoDataSources</c> pins. A Forum pool below two connections cannot
    /// be split, and the constructor refuses it.</para>
    /// </summary>
    public IFlagRaiserGate FlagRaiserGate => new PostgresFlagRaiserGate(_gateDataSource);

    /// <summary>
    /// R8.77 (errata G45): one post's revisions entered one at a time, on the gate pool beside <see cref="FlagRaiserGate"/>, held
    /// by the submit route for the whole of a revision's attempt.
    /// </summary>
    public IRevisionGate RevisionGate => new PostgresRevisionGate(_gateDataSource);

    /// <summary>
    /// R7.27 (errata G48): one agent's posts and acceptances counted and recorded one at a time (one gate, and so one queue, for this adapter), by the in-memory queue followed by a try-only advisory lock,
    /// on a connection pool of its own. The hold keeps its connection for the whole write, while the write draws on the Forum's pool and a revision takes
    /// the revision gate on the gate pool; the order of acquisition is agent, gate, Forum.
    /// </summary>
    public IAgentWriteGate AgentWriteGate => _agentWriteGate;

    public async ValueTask DisposeAsync()
    {
        await _agentDataSource.DisposeAsync().ConfigureAwait(false);
        await _gateDataSource.DisposeAsync().ConfigureAwait(false);
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
