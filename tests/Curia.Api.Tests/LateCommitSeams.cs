using Curia.Application.Ports;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G57's seams for the route facts (plan Task 0c). A decorator over the host's event store that can (a) append a chosen event to the real store before each
/// append attempt of a chosen stream (a flag that keeps being outrun), and (b) answer an append with a chosen failure or throw instead of forwarding it (the mapping
/// of a guard's error, and every exit that must release the agent's hold and the revision gate). It forwards to the real store otherwise and fabricates no answer
/// a real store would not give.
/// </summary>
internal sealed class LateInjectingStore(IEventStore inner) : IEventStore
{
    private readonly object _gate = new();
    private Func<AggregateId, bool>? _injectScope;
    private Func<int, CancellationToken, Task>? _inject;
    private Func<AggregateId, bool>? _failScope;
    private Error? _failWith;
    private Exception? _throwWith;
    private int _attempts;

    /// <summary>Before each append to a stream <paramref name="scope"/> accepts, <paramref name="inject"/> runs (it appends through <see cref="Inner"/>); the first argument is the attempt number from 1.</summary>
    internal void Arm(Func<AggregateId, bool> scope, Func<int, CancellationToken, Task> inject)
    {
        lock (_gate)
        {
            _injectScope = scope;
            _inject = inject;
            _attempts = 0;
        }
    }

    /// <summary>Answers an append to a stream <paramref name="scope"/> accepts with <paramref name="error"/> (or throws <paramref name="exception"/>) and writes nothing.</summary>
    internal void FailAppends(Func<AggregateId, bool> scope, Error? error, Exception? exception = null)
    {
        lock (_gate)
        {
            _failScope = scope;
            _failWith = error;
            _throwWith = exception;
        }
    }

    internal void Reset()
    {
        lock (_gate)
        {
            _injectScope = null;
            _inject = null;
            _failScope = null;
            _failWith = null;
            _throwWith = null;
            _attempts = 0;
        }
    }

    internal IEventStore Inner => inner;

    internal int Attempts
    {
        get { lock (_gate) return _attempts; }
    }

    private async Task<Result<IReadOnlyList<AppendedEvent>>?> BeforeAsync(AggregateId aggregateId, CancellationToken cancellationToken)
    {
        Func<int, CancellationToken, Task>? inject = null;
        int attempt = 0;
        Error? fail = null;
        Exception? thrown = null;
        lock (_gate)
        {
            if (_injectScope is { } injectScope && injectScope(aggregateId))
            {
                attempt = ++_attempts;
                inject = _inject;
            }

            if (_failScope is { } failScope && failScope(aggregateId))
            {
                fail = _failWith;
                thrown = _throwWith;
            }
        }

        if (inject is not null)
            await inject(attempt, cancellationToken).ConfigureAwait(false);

        if (thrown is not null) throw thrown;
        return fail is null ? null : Result<IReadOnlyList<AppendedEvent>>.Fail(fail);
    }

    public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, CancellationToken cancellationToken = default) =>
        await BeforeAsync(aggregateId, cancellationToken).ConfigureAwait(false)
        ?? await inner.AppendAsync(aggregateId, expectedVersion, events, cancellationToken).ConfigureAwait(false);

    public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition condition, CancellationToken cancellationToken = default) =>
        await BeforeAsync(aggregateId, cancellationToken).ConfigureAwait(false)
        ?? await inner.AppendAsync(aggregateId, expectedVersion, events, condition, cancellationToken).ConfigureAwait(false);

    public async Task<Result<IReadOnlyList<AppendedEvent>>> AppendAsync(
        AggregateId aggregateId, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events, StreamCondition? condition, AppendGuard guard, CancellationToken cancellationToken = default) =>
        await BeforeAsync(aggregateId, cancellationToken).ConfigureAwait(false)
        ?? await inner.AppendAsync(aggregateId, expectedVersion, events, condition, guard, cancellationToken).ConfigureAwait(false);

    private int _byAggregate;
    private int _forward;

    /// <summary>How many reads of one stream the store has been asked for since it was made (errata G58's ASA37 counts the reads a request makes by differences of these).</summary>
    internal int ReadsByAggregate => Volatile.Read(ref _byAggregate);

    /// <summary>How many forward reads of the whole log the store has been asked for since it was made.</summary>
    internal int ReadsForward => Volatile.Read(ref _forward);

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadByAggregateAsync(AggregateId aggregateId, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _byAggregate);
        return inner.ReadByAggregateAsync(aggregateId, cancellationToken);
    }

    public Task<Result<IReadOnlyList<AppendedEvent>>> ReadForwardAsync(EventSequence afterSeq, int? maxCount = null, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _forward);
        return inner.ReadForwardAsync(afterSeq, maxCount, cancellationToken);
    }
}

/// <summary>
/// The Forum of the late-commit route facts: the production composition in the Production environment, connecting as the application role (traps 84 and 86: R11.6's
/// grants apply to the host and to the lock holder alike), its event store wrapped by <see cref="LateInjectingStore"/>, and every log event captured. The oracle reads
/// the events table and <c>flag_details</c> on the owner's connection, never the Forum's answer.
/// </summary>
public sealed class LateCommitForumFixture : ForumFixture
{
    private LateInjectingStore? _store;
    private NpgsqlDataSource? _appRole;

    internal LogCapture Log { get; } = new();

    internal LateInjectingStore Store => _store ?? throw new InvalidOperationException("the host has not composed its event store yet");

    /// <summary>A data source for the application role, the one the lock holder connects as.</summary>
    internal NpgsqlDataSource AppRole => _appRole ??= new NpgsqlDataSourceBuilder(HostConnectionString).Build();

    protected override string HostConnectionString =>
        new NpgsqlConnectionStringBuilder(ConnectionString) { Username = RoleName, Password = RolePassword }.ConnectionString;

    /// <summary>Everything a fact set on the shared host, back to forwarding.</summary>
    internal void ResetSeams()
    {
        _ = Services.GetRequiredService<IEventStore>();
        Store.Reset();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
        builder.ConfigureLogging(logging => logging.AddProvider(Log));
    }

    protected override void ConfigureStore(IServiceCollection services)
    {
        var registered = services.Last(d => d.ServiceType == typeof(IEventStore));
        if (registered.ImplementationFactory is null)
            throw new InvalidOperationException("IEventStore is registered by a factory; this decorator wraps what it builds");

        services.Remove(registered);
        services.AddSingleton<IEventStore>(sp =>
        {
            _store = new LateInjectingStore((IEventStore)registered.ImplementationFactory(sp));
            return _store;
        });
    }

    public override async ValueTask DisposeAsync()
    {
        if (_appRole is not null) await _appRole.DisposeAsync();
        await base.DisposeAsync();
    }
}
