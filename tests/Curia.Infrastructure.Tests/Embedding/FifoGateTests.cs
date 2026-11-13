using System.Diagnostics.CodeAnalysis;
using Curia.Infrastructure.Embedder;
using Xunit;

namespace Curia.Infrastructure.Tests.Embedder;

/// <summary>
/// Errata G65 (D10a), SEM45: the gate around an inference run admits callers in the order they arrived (R11.92, Amendment 1 ruling 23). Task 0 M6 measured <c>SemaphoreSlim(2)</c>
/// starving waiters (a 16-token p99 of 908 ms at 8 callers against 22.6 ms through a first-in-first-out gate).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FifoGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// SEM45. A gate of two, both places held, twenty waiters queued one at a time (each seen queued before the next is added), the places released one at a time: the admission order equals the
    /// arrival order, no more than two are ever inside, and after the last release the gate is empty. Predicted red on the seam (a gate that admits at once): <c>expected at most 2 inside, saw 3</c>.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM45_WaitersAreAdmittedInTheOrderTheyArrivedAndNeverMoreThanTwoAreInside()
    {
        var gate = new FifoGate(2);
        var held = new List<IDisposable> { await gate.EnterAsync(Ct), await gate.EnterAsync(Ct) };
        Assert.Equal(2, gate.Inside);

        var admitted = new List<int>();
        var mostInside = 0;
        var done = Enumerable.Range(0, 20).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var waiters = new List<Task>();
        for (var i = 0; i < 20; i++)
        {
            var index = i;
            waiters.Add(Task.Run(
                async () =>
                {
                    using var lease = await gate.EnterAsync(Ct);
                    lock (admitted)
                    {
                        admitted.Add(index);
                        mostInside = Math.Max(mostInside, gate.Inside);
                    }

                    await done[index].Task;
                },
                Ct));

            var deadline = DateTime.UtcNow.AddSeconds(2);
            while (gate.Queued < index + 1 && !Admitted(admitted, index) && DateTime.UtcNow < deadline)
                await Task.Delay(5, Ct);

            Assert.True(gate.Inside <= 2, $"expected at most 2 inside, saw {gate.Inside}");
            Assert.True(gate.Queued == index + 1, $"waiter {index} was not seen queued: {gate.Queued} waiting");
        }

        foreach (var lease in held) lease.Dispose();
        for (var i = 0; i < 20; i++)
        {
            var index = i;
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!Admitted(admitted, index) && DateTime.UtcNow < deadline)
                await Task.Delay(5, Ct);
            Assert.True(Admitted(admitted, index), $"waiter {index} was never admitted");
            done[index].SetResult();

            // One place is freed at a time: the place this frees goes to waiter index + 2 (two are inside), which is seen admitted before the next place is freed. Two places freed together
            // admit two waiters at once, and the order they are recorded in then belongs to the thread pool and not to the gate.
            if (index + 2 < 20)
            {
                var next = index + 2;
                var seen = DateTime.UtcNow.AddSeconds(5);
                while (!Admitted(admitted, next) && DateTime.UtcNow < seen)
                    await Task.Delay(5, Ct);
            }
        }

        await Task.WhenAll(waiters);
        Assert.Equal(Enumerable.Range(0, 20), admitted);
        Assert.True(mostInside <= 2, $"expected at most 2 inside, saw {mostInside}");
        Assert.Equal(0, gate.Inside);
        Assert.Equal(0, gate.Queued);
    }

    /// <summary>
    /// SEM49 (Amendment 2 ruling 41). A gate of two, both places held, one waiter queued and seen queued; then, in one synchronous step, one lease is disposed and a fresh caller starts to
    /// enter: the fresh caller has not been admitted, the queued waiter has, and no more than two are inside. Two hundred times on fresh gates, because a gate that frees the place and
    /// then wakes the waiter opens a window of a thread-pool hand-off. Green on the gate as it stands; FSEM51's gate is what makes it a check. Predicted red on that gate:
    /// <c>a fresh caller was admitted while the waiter queued before it was still waiting</c> or <c>expected at most 2 inside, saw 3</c>.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM49_ACallerArrivingAsAPlaceIsFreedQueuesBehindTheWaiterAlreadyQueued()
    {
        for (var iteration = 1; iteration <= 200; iteration++)
        {
            var gate = new FifoGate(2);
            var first = await gate.EnterAsync(Ct);
            var second = await gate.EnterAsync(Ct);
            var queued = gate.EnterAsync(Ct).AsTask();
            await WaitQueuedAsync(gate, 1, "the queued waiter");

            first.Dispose();
            var fresh = gate.EnterAsync(Ct).AsTask();

            Assert.True(gate.Inside <= 2, $"expected at most 2 inside, saw {gate.Inside} (iteration {iteration} of 200)");
            var admitted = await Task.WhenAny(queued, fresh).WaitAsync(TimeSpan.FromSeconds(5), Ct);
            Assert.True(ReferenceEquals(admitted, queued), $"a fresh caller was admitted while the waiter queued before it was still waiting (iteration {iteration} of 200)");
            Assert.False(fresh.IsCompleted, $"a fresh caller was admitted while the waiter queued before it was still waiting (iteration {iteration} of 200)");
            Assert.True(gate.Inside <= 2, $"expected at most 2 inside, saw {gate.Inside} (iteration {iteration} of 200)");

            second.Dispose();
            (await queued).Dispose();
            (await fresh.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Dispose();
            Assert.Equal(0, gate.Inside);
        }
    }

    private static bool Admitted(List<int> admitted, int index)
    {
        lock (admitted) return admitted.Contains(index);
    }

    /// <summary>
    /// SEM45, cancellation. A waiter cancelled while queued is removed, runs nothing and does not shift the others; a token already cancelled enters nothing. Predicted red on the seam:
    /// <c>waiter 1 was not seen queued</c>.
    /// </summary>
    [Fact]
    public async Task R11_92_SEM45_ACancelledWaiterLeavesTheQueueAndShiftsNoOne()
    {
        var gate = new FifoGate(1);
        var holder = await gate.EnterAsync(Ct);
        var ran = new List<int>();

        using var cancel = new CancellationTokenSource();
        var first = Task.Run(async () => { using var _ = await gate.EnterAsync(Ct); lock (ran) ran.Add(0); }, Ct);
        await WaitQueuedAsync(gate, 1, "waiter 0");
        var second = Task.Run(async () => { using var _ = await gate.EnterAsync(cancel.Token); lock (ran) ran.Add(1); }, Ct);
        await WaitQueuedAsync(gate, 2, "waiter 1");
        var third = Task.Run(async () => { using var _ = await gate.EnterAsync(Ct); lock (ran) ran.Add(2); }, Ct);
        await WaitQueuedAsync(gate, 3, "waiter 2");

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(2, gate.Queued);

        holder.Dispose();
        await Task.WhenAll(first, third).WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.Equal([0, 2], ran);
        Assert.Equal(0, gate.Inside);
        Assert.Equal(0, gate.Queued);
    }

    private static async Task WaitQueuedAsync(FifoGate gate, int count, string who)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (gate.Queued < count && DateTime.UtcNow < deadline)
            await Task.Delay(5, Ct);
        Assert.True(gate.Queued >= count, $"{who} was not seen queued: {gate.Queued} waiting");
    }
}
