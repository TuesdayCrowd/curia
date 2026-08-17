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
    private readonly TimeProvider _clock;

    public PostgresAdapters(string connectionString, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(clock);

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _clock = clock;
    }

    /// <summary>The append-only event log (R11.6), which is the system of record.</summary>
    public IEventStore EventStore => new PostgresEventStore(_dataSource, _clock);

    /// <summary>
    /// The read half. CS-15: a component typed to this has no member reaching the write surface, so
    /// a read path cannot append even by accident.
    /// </summary>
    public IEventReader EventReader => new PostgresEventStore(_dataSource, _clock);

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

    public async ValueTask DisposeAsync()
    {
        await _dataSource.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
