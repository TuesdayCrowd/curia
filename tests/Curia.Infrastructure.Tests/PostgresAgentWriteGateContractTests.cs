using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// <see cref="AgentWriteGatePortContractTests"/> over the per-run database (R7.27; errata G48), and the facts only a database can state: the hold is the
/// database's across two gates (BRB11), it goes with the hold and with the session (BRB12), the key families are disjoint (BRB13), a starved pool is
/// <c>gate-unavailable</c> within the bound (BRB14) and waiting holds no connection (BRB15). Each gate takes a schema of its own, which is only the lock
/// key's prefix, so no two tests' holds can meet.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "See PostgresEventStoreContractTests: xUnit discovery needs the concrete class public.")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresAgentWriteGateContractTests : AgentWriteGatePortContractTests, IAsyncDisposable
{
    private readonly PostgresDatabaseFixture _fixture;
    private readonly List<IAsyncDisposable> _cleanup = [];

    public PostgresAgentWriteGateContractTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewSchema() => "gate_" + Guid.NewGuid().ToString("N");

    protected override TimeProvider CreateClock() => TimeProvider.System;

    protected override Task AdvanceAsync(TimeSpan by) => Task.Delay(by, Ct);

    protected override TimeSpan Slack => TimeSpan.FromMilliseconds(400);

    protected override int OtherAgents => 20;

    protected override IAgentWriteGate CreateGate(TimeSpan wait, int waiters) =>
        new PostgresAgentWriteGate(_fixture.AppRoleDataSource, Clock, wait, waiters, NewSchema());

    protected override int TrackedKeysOf(IAgentWriteGate gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        return ((PostgresAgentWriteGate)gate).TrackedKeys;
    }

    protected override (IAgentWriteGate Gate, Action Recover) CreateGateWithUnreachableStore()
    {
        var starved = StarvedSource(poolSize: 1, timeoutSeconds: 1);
        var occupant = starved.OpenConnection();
        return (new PostgresAgentWriteGate(starved, Clock, Wait, Waiters, NewSchema()), () => occupant.Dispose());
    }

    private NpgsqlDataSource StarvedSource(int poolSize, int timeoutSeconds)
    {
        var builder = new NpgsqlConnectionStringBuilder(_fixture.AppRoleConnectionString)
        {
            MaxPoolSize = poolSize,
            MinPoolSize = 0,
            Timeout = timeoutSeconds,
            Pooling = true,
        };
        var source = NpgsqlDataSource.Create(builder.ConnectionString);
        _cleanup.Add(source);
        return source;
    }

    private static string Outcome(Result<IAsyncDisposable> result) => result.TryGetValue(out _, out var error) ? "entered" : error!.Type;

    /// <summary>BRB11. Two gates over two data sources and one schema (distinct queues, as two processes): the second waits for the first's hold, is refused after its bound, and enters after a release.</summary>
    [Fact]
    public async Task R7_27_BRB11_TwoGatesOnOneDatabaseHoldOneAgentAtATime()
    {
        var schema = NewSchema();
        var bound = TimeSpan.FromMilliseconds(800);
        var first = new PostgresAgentWriteGate(_fixture.AppRoleDataSource, TimeProvider.System, bound, Waiters, schema);
        var second = new PostgresAgentWriteGate(_fixture.AdminDataSource, TimeProvider.System, bound, Waiters, schema);
        var x = Agent("two-gates");

        string whileHeld;
        TimeSpan elapsed;
        var hold = Require(await first.EnterAsync(x, Ct));
        try
        {
            var timer = Stopwatch.StartNew();
            whileHeld = Outcome(await second.EnterAsync(x, Ct));
            elapsed = timer.Elapsed;
        }
        finally
        {
            await hold.DisposeAsync();
        }

        var longWait = new PostgresAgentWriteGate(_fixture.AdminDataSource, TimeProvider.System, TimeSpan.FromSeconds(10), Waiters, schema);
        var holdAgain = Require(await first.EnterAsync(x, Ct));
        var pending = longWait.EnterAsync(x, Ct);
        var stillPending = await Task.WhenAny(pending, Task.Delay(300, Ct)) != pending;
        await holdAgain.DisposeAsync();
        var afterRelease = await pending.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        var entered = Outcome(afterRelease);
        if (afterRelease.TryGetValue(out var held, out _)) await held!.DisposeAsync();

        var within = elapsed >= bound * 0.9 && elapsed <= bound * 2 ? "within the bound and twice it" : $"after {elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms";
        Said(
            $"refused {PublishedBudgetGate.InFlightType()} within the bound and twice it; the long waiter pending while held; entered after release",
            $"{whileHeld} {within}; the long waiter {(stillPending ? "pending" : "completed")} while held; {entered} after release");
    }

    private async Task<int?> BackendHoldingAsync(string schema, AgentId agent)
    {
        await using var command = _fixture.AdminDataSource.CreateCommand(
            "SELECT l.pid FROM pg_locks l, (SELECT hashtextextended(@key, 0) AS h) k " +
            "WHERE l.locktype = 'advisory' AND l.granted " +
            "AND l.classid = ((k.h >> 32) & 4294967295)::oid AND l.objid = (k.h & 4294967295)::oid LIMIT 1;");
        command.Parameters.AddWithValue("key", PostgresAgentWriteGate.AgentWriteLockKey(schema, agent));
        return await command.ExecuteScalarAsync(Ct) is int pid ? pid : null;
    }

    /// <summary>BRB12. The lock goes with the hold and with the session: disposing frees it for the other gate, and terminating the holder's backend frees it too.</summary>
    [Fact]
    public async Task R7_27_BRB12_TheLockGoesWithTheHoldAndWithTheSession()
    {
        var schema = NewSchema();
        var bound = TimeSpan.FromMilliseconds(600);
        var first = new PostgresAgentWriteGate(_fixture.AppRoleDataSource, TimeProvider.System, bound, Waiters, schema);
        var second = new PostgresAgentWriteGate(_fixture.AdminDataSource, TimeProvider.System, bound, Waiters, schema);
        var x = Agent("session");

        var hold = Require(await first.EnterAsync(x, Ct));
        var pid = await BackendHoldingAsync(schema, x);
        await hold.DisposeAsync();
        var afterDispose = Outcome(await second.EnterAsync(x, Ct));

        var holdAgain = Require(await first.EnterAsync(x, Ct));
        var terminated = "no backend held the lock";
        var holder = await BackendHoldingAsync(schema, x);
        if (holder is int backend)
        {
            await using var kill = _fixture.AdminDataSource.CreateCommand("SELECT pg_terminate_backend(@pid);");
            kill.Parameters.AddWithValue("pid", backend);
            terminated = await kill.ExecuteScalarAsync(Ct) is true ? "terminated" : "not terminated";
        }

        var afterTermination = await second.EnterAsync(x, Ct);
        var entered = Outcome(afterTermination);
        if (afterTermination.TryGetValue(out var held, out _)) await held!.DisposeAsync();
        await holdAgain.DisposeAsync();
        var released = Outcome(await first.EnterAsync(x, Ct));

        Said(
            "a backend holds the lock; entered after dispose; the holder's backend terminated; entered after termination; the first gate enters once its hold is disposed",
            $"{(pid is null ? "no backend holds the lock" : "a backend holds the lock")}; {afterDispose} after dispose; the holder's backend {terminated}; {entered} after termination; the first gate {released} once its hold is disposed");
    }

    private static IEnumerable<MethodInfo> LockKeyMethods() =>
        typeof(PostgresAgentWriteGate).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
            .Where(m => m.Name.EndsWith("LockKey", StringComparison.Ordinal) && m.ReturnType == typeof(string));

    private static string KeyOf(MethodInfo method, string schema, string value)
    {
        var parameters = method.GetParameters();
        var arguments = new object?[parameters.Length];
        arguments[0] = schema;
        for (var i = 1; i < parameters.Length; i++)
        {
            arguments[i] = parameters[i].ParameterType == typeof(AgentId)
                ? AgentId.Parse(value).Match(v => v, e => throw new InvalidOperationException(e.Type))
                : value;
        }

        return (string)method.Invoke(null, arguments)!;
    }

    /// <summary>
    /// BRB13. Per schema, per agent, in a family of its own, data-driven: every <c>*LockKey</c> static of the assembly (flag, revision, agent write, the key
    /// store's, the event store's) is found by reflection; two schemas give two keys; the keys' fixed prefixes are pairwise disjoint (neither is the start of
    /// another); and the agent write family is among them.
    /// </summary>
    [Fact]
    public void R7_27_BRB13_EveryLockKeyFamilyIsPerSchemaAndDisjointFromTheOthers()
    {
        var methods = LockKeyMethods().ToArray();
        var names = methods.Select(m => $"{m.DeclaringType!.Name}.{m.Name}").Order(StringComparer.Ordinal).ToArray();

        var problems = new List<string>();
        if (!names.Contains("PostgresAgentWriteGate.AgentWriteLockKey")) problems.Add("no agent write lock key family");

        var prefixes = new Dictionary<string, string>();
        foreach (var method in methods)
        {
            var name = $"{method.DeclaringType!.Name}.{method.Name}";
            var one = KeyOf(method, "schema_one", "value-1");
            var other = KeyOf(method, "schema_two", "value-1");
            if (one == other) problems.Add($"{name} omits the schema");

            var twoValues = method.GetParameters().Length > 1;
            var prefix = twoValues ? one[..one.LastIndexOf("value-1", StringComparison.Ordinal)] : one;
            if (twoValues && KeyOf(method, "schema_one", "value-2") == one) problems.Add($"{name} ignores the value");
            prefixes[name] = prefix;
        }

        foreach (var (a, pa) in prefixes)
        {
            foreach (var (b, pb) in prefixes)
            {
                if (string.CompareOrdinal(a, b) < 0 && (pa.StartsWith(pb, StringComparison.Ordinal) || pb.StartsWith(pa, StringComparison.Ordinal)))
                    problems.Add($"{a} (\"{pa}\") and {b} (\"{pb}\") share a family");
            }
        }

        Said("the agent write family is among the lock keys; per schema; pairwise disjoint", problems.Count == 0 ? "the agent write family is among the lock keys; per schema; pairwise disjoint" : string.Join(" | ", problems) + " (families: " + string.Join(", ", names) + ")");
    }

    /// <summary>BRB14. A starved pool is <c>gate-unavailable</c>, never a hang: the request's own deadline governs, not Npgsql's 15 s default.</summary>
    [Fact]
    public async Task R7_27_BRB14_AStarvedPoolIsGateUnavailableWithinTheBound()
    {
        var starved = StarvedSource(poolSize: 1, timeoutSeconds: 15);
        await using var occupant = await starved.OpenConnectionAsync(Ct);
        var bound = TimeSpan.FromSeconds(1);
        var gate = new PostgresAgentWriteGate(starved, TimeProvider.System, bound, Waiters, NewSchema());

        var timer = Stopwatch.StartNew();
        string outcome;
        try
        {
            outcome = Outcome(await gate.EnterAsync(Agent("starved"), Ct).WaitAsync(TimeSpan.FromSeconds(6), Ct));
        }
        catch (TimeoutException)
        {
            outcome = "still waiting at 6 s";
        }

        var elapsed = timer.Elapsed;
        var timely = elapsed <= bound + TimeSpan.FromSeconds(1.5) ? "inside the bound and a slack" : $"after {elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s";
        Said($"{PublishedBudgetGate.UnavailableType()} inside the bound and a slack", $"{outcome} {timely}");
    }

    /// <summary>BRB15. Waiting holds no connection: with an agent pool of two, X holds and many more X requests wait, and agents Y and Z still enter at once.</summary>
    [Fact]
    public async Task R7_27_BRB15_WaitingHoldsNoConnection()
    {
        var pool = StarvedSource(poolSize: 2, timeoutSeconds: 2);
        var gate = new PostgresAgentWriteGate(pool, TimeProvider.System, TimeSpan.FromSeconds(30), 32, NewSchema());
        var x = Agent("many");

        var holder = Require(await gate.EnterAsync(x, Ct));
        var waiting = Enumerable.Range(0, 32).Select(_ => gate.EnterAsync(x, Ct)).ToArray();
        await Task.Delay(300, Ct);
        var pendingWaiters = waiting.Count(w => !w.IsCompleted);

        var y = Outcome(await gate.EnterAsync(Agent("y"), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct));
        var z = Outcome(await gate.EnterAsync(Agent("z"), Ct).WaitAsync(TimeSpan.FromSeconds(5), Ct));
        await holder.DisposeAsync();

        Said("32 of 32 waiters pending; Y entered; Z entered", $"{pendingWaiters} of 32 waiters pending; Y {y}; Z {z}");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var item in _cleanup) await item.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
