namespace Curia.Infrastructure.Embedder;

/// <summary>
/// The gate around an inference run (errata G65, R11.92, Amendment 1 ruling 23): at most <see cref="Limit"/> callers inside, admitted in the order they arrived,
/// a waiter that is cancelled leaving the queue and running nothing. <b>Seam stub (D10a Task 1):</b> admits every caller at once.
/// </summary>
internal sealed class FifoGate(int limit)
{
    private int _inside;
#pragma warning disable CS0649 // the seam stub queues no one; Task 2 counts the waiters
    private int _queued;
#pragma warning restore CS0649

    /// <summary>How many callers may be inside at once.</summary>
    internal int Limit { get; } = limit;

    /// <summary>How many callers are inside now.</summary>
    internal int Inside => Volatile.Read(ref _inside);

    /// <summary>How many callers are waiting for a place.</summary>
    internal int Queued => Volatile.Read(ref _queued);

    /// <summary>Waits for a place; disposing the lease frees it.</summary>
    internal ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref _inside);
        #pragma warning disable CA2000 // the lease is the caller's to dispose
        return ValueTask.FromResult<IDisposable>(new Lease(this));
#pragma warning restore CA2000
    }

    private sealed class Lease(FifoGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Interlocked.Decrement(ref gate._inside);
        }
    }
}
