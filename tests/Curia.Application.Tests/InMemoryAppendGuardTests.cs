#pragma warning disable CA2007 // test code: there is no SynchronizationContext to resume on (as the other suites of this project do, per await, with ConfigureAwait(true))
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Tests.InMemory;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;
using Curia.Tests.Shared;

namespace Curia.Application.Tests;

/// <summary>Runs <see cref="AppendGuardContractTests"/> against <see cref="InMemoryEventStore"/>, plus LCB8's in-memory half.</summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class InMemoryAppendGuardTests : AppendGuardContractTests
{
    protected override IEventStore CreateStore() => new InMemoryEventStore(TimeProvider.System, new PermissiveLifecycleAppendGate());

    protected override IEventStore CreateStore(ILifecycleAppendGate gate) => new InMemoryEventStore(TimeProvider.System, gate);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title} ({e.Detail})"));

    private static DomainEvent Ev(string id) => new(Require(EventId.Create(id)), Require(EventType.Create("test.event")), null, new JsonValue.Object([]));

    /// <summary>
    /// LCB8 (in-memory half). An append inside its guard blocks a second append until the guard returns: the guard is evaluated inside the same critical section as the
    /// version check, so a second writer cannot interleave. The evidence is the order of events the test records, not a sleep.
    /// </summary>
    [Fact]
    public async Task R6_60_LCB8_AnAppendInsideItsGuardBlocksASecondAppend()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore();
        Require(await store.AppendAsync(Require(AggregateId.Create("lcb8-seed")), AggregateVersion.New, [Ev("lcb8-seed")], ct));
        var order = new List<string>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim(false);
        var guard = new AppendGuard(EventSequence.Zero, _ =>
        {
            lock (order) order.Add("guard entered");
            entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
            lock (order) order.Add("guard left");
            return GuardVerdict.Proceed;
        });

        var first = Task.Run(async () => Require(await store.AppendAsync(Require(AggregateId.Create("lcb8-first")), AggregateVersion.New, [Ev("lcb8-first")], null, guard, ct)), ct);
        var enteredInTime = await Task.WhenAny(entered.Task, Task.Delay(TimeSpan.FromSeconds(3), ct)) == entered.Task;
        Task? second = null;
        if (enteredInTime)
        {
            second = Task.Run(async () =>
            {
                Require(await store.AppendAsync(Require(AggregateId.Create("lcb8-second")), AggregateVersion.New, [Ev("lcb8-second")], ct));
                lock (order) order.Add("second append done");
            }, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }

        release.Set();
        await first;
        if (second is not null) await second;

        string said;
        lock (order) said = string.Join(", ", order);
        if (said != "guard entered, guard left, second append done")
            throw new Xunit.Sdk.XunitException($"Expected: guard entered, guard left, second append done{Environment.NewLine}Actual:   {(enteredInTime ? said : "the guard was never called")}");
    }
}
