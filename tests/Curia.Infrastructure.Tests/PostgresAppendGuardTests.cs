using System.Diagnostics.CodeAnalysis;
using Curia.Application;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// Runs <see cref="AppendGuardContractTests"/> against <see cref="PostgresEventStore"/> as the application role (R11.6's grants apply), plus the probes that need the
/// real lock: LCB8 (the guard is called under the lock, after a late commit lands), LCB10 (both adapters run the same suite) and LCB22's store half (every exit
/// releases the connection and the lock).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Design", "CA1515:Consider making public types internal", Justification = "xUnit discovers a concrete test class only when it is public.")]
[SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The only interpolated text is the schema name CreateIsolatedSchemaAsync generated from a literal prefix and a GUID's hex digits.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresAppendGuardTests : AppendGuardContractTests
{
    private readonly PostgresDatabaseFixture _fixture;
    private string _schema = string.Empty;

    public PostgresAppendGuardTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    protected override IEventStore CreateStore() => CreateStore(_fixture.PermissiveLifecycleGate());

    protected override IEventStore CreateStore(ILifecycleAppendGate gate)
    {
        _schema = _fixture.CreateIsolatedSchemaAsync().GetAwaiter().GetResult();
        return new PostgresEventStore(_fixture.AppRoleDataSource, TimeProvider.System, gate, _schema);
    }

    protected override async Task<bool> LeaveGapAsync(IEventStore store, CancellationToken cancellationToken)
    {
        await using var holder = await LateCommitHolder.BeginAsync(_fixture.AppRoleDataSource, _schema, cancellationToken);
        await holder.InsertAsync("gap-1", "test.event", "gap-aggregate", null, "{}", DateTimeOffset.UtcNow, cancellationToken);
        await holder.RollbackAsync(cancellationToken);
        return true;
    }

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static DomainEvent Ev(string id) => new(Require(EventId.Create(id)), Require(EventType.Create("test.event")), null, new JsonValue.Object([]));

    private static AggregateId Agg(string value) => Require(AggregateId.Create(value));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static void Equal(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private async Task WaitUntilQueuedAsync()
    {
        await using var probe = await _fixture.AdminDataSource.OpenConnectionAsync(Ct);
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (await LateCommitHolder.WaitersAsync(probe, Ct) > 0) return;
            await Task.Delay(25, Ct);
        }

        throw new Xunit.Sdk.XunitException("the append never queued behind the holder's lock");
    }

    /// <summary>
    /// LCB8 (Postgres half). Under the lock, over the real thing (trap 90: a sequential run cannot see a sibling). A second session as the application role holds the
    /// store's lock with an uncommitted event; an append with a guard is queued, its guard not yet called (checked after 500 ms); the session commits; the append
    /// returns and its guard was called with the session's event in its delta, strictly after the commit. With the session rolled back instead the delta is empty (the
    /// guard is not called) and the append proceeds. The evidence is the order of events the test records.
    /// </summary>
    [Fact]
    public async Task R6_60_LCB8_TheGuardIsCalledUnderTheLockWithTheLateCommitInItsDelta()
    {
        var store = CreateStore();
        Require(await store.AppendAsync(Agg("lcb8-seed"), AggregateVersion.New, [Ev("lcb8-seed")], Ct));
        var position = Require(EventSequence.From((await Highest()) ));
        var order = new List<string>();
        var deltas = new List<long[]>();
        var guard = new AppendGuard(position, delta =>
        {
            lock (order) order.Add("guard");
            deltas.Add([.. delta.Select(e => e.Seq.Value)]);
            return GuardVerdict.Proceed;
        });

        await using (var holder = await LateCommitHolder.BeginAsync(_fixture.AppRoleDataSource, _schema, Ct))
        {
            var lateSeq = await holder.InsertAsync("lcb8-late", "test.event", "lcb8-late-agg", null, "{}", DateTimeOffset.UtcNow, Ct);
            var append = store.AppendAsync(Agg("lcb8-target"), AggregateVersion.New, [Ev("lcb8-target")], null, guard, Ct);
            await WaitUntilQueuedAsync();
            await Task.Delay(500, Ct);
            var queued = !append.IsCompleted;
            var calledBeforeCommit = order.Count > 0;
            lock (order) order.Add("commit");
            await holder.CommitAsync(Ct);
            var result = await append;

            string Delta() => deltas.Count == 0 ? "the guard was not called" : "delta " + string.Join(" then ", deltas.Select(d => "[" + string.Join(", ", d) + "]"));
            Equal(
                $"queued behind the lock {true}, called before the commit {false}, order commit, guard, delta [{lateSeq}], appended {true}",
                $"queued behind the lock {queued}, called before the commit {calledBeforeCommit}, order {string.Join(", ", order)}, {Delta()}, appended {result.TryGetValue(out _, out _)}");
        }

        order.Clear();
        deltas.Clear();
        var position2 = Require(EventSequence.From(await Highest()));
        var guard2 = new AppendGuard(position2, delta => { deltas.Add([.. delta.Select(e => e.Seq.Value)]); return GuardVerdict.Proceed; });
        await using (var holder = await LateCommitHolder.BeginAsync(_fixture.AppRoleDataSource, _schema, Ct))
        {
            await holder.InsertAsync("lcb8-rolled", "test.event", "lcb8-rolled-agg", null, "{}", DateTimeOffset.UtcNow, Ct);
            var append = store.AppendAsync(Agg("lcb8-target-2"), AggregateVersion.New, [Ev("lcb8-target-2")], null, guard2, Ct);
            await WaitUntilQueuedAsync();
            await holder.RollbackAsync(Ct);
            var result = await append;
            Equal("rolled back: the guard was not called, appended True", $"rolled back: {(deltas.Count == 0 ? "the guard was not called" : "the guard was called with " + deltas[0].Length + " events")}, appended {result.TryGetValue(out _, out _)}");
        }

        async Task<long> Highest()
        {
            var all = Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: Ct));
            return all.Count == 0 ? 0 : all[^1].Seq.Value;
        }
    }

    /// <summary>LCB10. Both adapters run the same suite: the contract class is inherited by exactly two concrete classes (counted by reflection), so the guard cases are not written once and run once.</summary>
    [Fact]
    public void R6_60_LCB10_BothAdaptersRunTheSameSuite()
    {
        var subclasses = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); } })
            .Where(t => !t.IsAbstract && typeof(AppendGuardContractTests).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Equal("InMemoryAppendGuardTests, PostgresAppendGuardTests", string.Join(", ", subclasses));
    }

    /// <summary>
    /// LCB22 (store half). Every exit releases: after <c>Proceed</c>, <c>Refuse</c>, <c>Redecide</c>, a throwing function and a cancellation raised inside the function, no
    /// advisory lock is held, no session of the role is idle in a transaction, and an append by another agent completes within a second. On the seam the guard is never called, so the throw and the
    /// cancellation never happen and the probe is red by its assertion.
    /// </summary>
    [Fact]
    public async Task R6_60_LCB22_EveryExitReleasesTheConnectionAndTheLock()
    {
        var store = CreateStore();
        Require(await store.AppendAsync(Agg("lcb22-seed"), AggregateVersion.New, [Ev("lcb22-seed")], Ct));
        var said = new List<string>();
        var expected = new List<string>();

        async Task<string> Released(string name, Func<CancellationTokenSource, AppendGuard> make, Func<Task<string>> outcome)
        {
            using var cts = new CancellationTokenSource();
            var happened = await outcome();
            await using var probe = await _fixture.AdminDataSource.OpenConnectionAsync(Ct);
            await using var locks = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database());", probe);
            var held = Convert.ToInt64(await locks.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
            await using var idle = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE state = 'idle in transaction' AND datname = current_database();", probe);
            var idleInTransaction = Convert.ToInt64(await idle.ExecuteScalarAsync(Ct), System.Globalization.CultureInfo.InvariantCulture);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var other = await store.AppendAsync(Agg("lcb22-other-" + name), AggregateVersion.New, [Ev("lcb22-other-" + name + Guid.NewGuid().ToString("N"))], Ct);
            return $"{name}: {happened}, locks held {held}, idle in transaction {idleInTransaction}, another agent's append {(other.TryGetValue(out _, out _) && clock.Elapsed < TimeSpan.FromSeconds(1) ? "done within a second" : "late or failed")}";
        }

        async Task<EventSequence> Position() => Require(EventSequence.From(Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: Ct)).Count == 0 ? 0 : Require(await store.ReadForwardAsync(EventSequence.Zero, cancellationToken: Ct))[^1].Seq.Value));

        async Task CommitLateAsync(string name)
        {
            await using var holder = await LateCommitHolder.BeginAsync(_fixture.AppRoleDataSource, _schema, Ct);
            await holder.InsertAsync("lcb22-late-" + name, "test.event", "lcb22-late-" + name, null, "{}", DateTimeOffset.UtcNow, Ct);
            await holder.CommitAsync(Ct);
        }

        foreach (var (name, verdict) in new (string, Func<GuardVerdict>)[]
        {
            ("proceed", () => GuardVerdict.Proceed),
            ("refuse", () => GuardVerdict.Refuse(new Error("probe/refused", "Refused by the probe"))),
            ("redecide", () => GuardVerdict.Redecide),
        })
        {
            // a late event so that the delta is not empty
            var position = await Position();
            await CommitLateAsync(name);
            var guard = new AppendGuard(position, _ => verdict());
            said.Add(await Released(name, _ => guard, async () => (await store.AppendAsync(Agg("lcb22-" + name), AggregateVersion.New, [Ev("lcb22-" + name)], null, guard, Ct)).Match(_ => "Ok", e => e.Type)));
            expected.Add($"{name}: {(name == "proceed" ? "Ok" : name == "refuse" ? "probe/refused" : PublishedLateCommitWords.RedecideType)}, locks held 0, idle in transaction 0, another agent's append done within a second");
        }

        {
            var position = await Position();
            await CommitLateAsync("throw");
            var guard = new AppendGuard(position, _ => throw new InvalidOperationException("the probe's function threw"));
            said.Add(await Released("throw", _ => guard, async () => (await Record.ExceptionAsync(() => store.AppendAsync(Agg("lcb22-throw"), AggregateVersion.New, [Ev("lcb22-throw")], null, guard, Ct)))?.GetType().Name ?? "no exception"));
            expected.Add("throw: InvalidOperationException, locks held 0, idle in transaction 0, another agent's append done within a second");
        }

        {
            var position = await Position();
            await CommitLateAsync("cancel");
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var guard = new AppendGuard(position, _ => { cancel.Cancel(); return GuardVerdict.Proceed; });
            said.Add(await Released("cancel", _ => guard, async () => (await Record.ExceptionAsync(() => store.AppendAsync(Agg("lcb22-cancel"), AggregateVersion.New, [Ev("lcb22-cancel")], null, guard, cancel.Token)))?.GetType().Name ?? "no exception"));
            expected.Add("cancel: OperationCanceledException, locks held 0, idle in transaction 0, another agent's append done within a second");
        }

        Equal(string.Join(" | ", expected), string.Join(" | ", said));
    }
}
