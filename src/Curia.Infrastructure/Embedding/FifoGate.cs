namespace Curia.Infrastructure.Embedder;

/// <summary>
/// The gate around an inference run (errata G65, R11.92, Amendment 1 ruling 23): at most <see cref="Limit"/> callers inside, admitted in the order they arrived, a waiter
/// that is cancelled leaving the queue and running nothing. The order is this gate's promise by construction and not a behaviour of the BCL today: <c>SemaphoreSlim</c> documents no order. Task 0 M6's starvation (a 16-token p99 of 908 ms at 8 callers) was
/// measured on a synchronous <c>Wait()</c> from dedicated threads that re-enter in a loop; <c>WaitAsync</c>, the path this adapter would use, kept arrival order in the same shape of loop, and neither the live run's
/// eight-at-once leg nor a 64-thread query flood with writes alongside separated the two forms (Amendment 4 ruling 59). A place freed is handed to the waiter at the head of the queue and not released for
/// whoever arrives first.
/// </summary>
internal sealed class FifoGate(int limit)
{
    private readonly object _lock = new();
    private readonly LinkedList<Waiter> _queue = new();
    private int _inside;

    /// <summary>How many callers may be inside at once.</summary>
    internal int Limit { get; } = limit;

    /// <summary>How many callers are inside now.</summary>
    internal int Inside
    {
        get
        {
            lock (_lock) return _inside;
        }
    }

    /// <summary>How many callers are waiting for a place.</summary>
    internal int Queued
    {
        get
        {
            lock (_lock) return _queue.Count;
        }
    }

    /// <summary>Waits for a place; disposing the lease frees it. A token that is cancelled while waiting ends the wait with <see cref="OperationCanceledException"/> and holds no place.</summary>
    internal async ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Waiter waiter;
        lock (_lock)
        {
            if (_inside < Limit && _queue.Count == 0)
            {
                _inside++;
                return new Lease(this);
            }

            waiter = new Waiter();
            waiter.Node = _queue.AddLast(waiter);
        }

        using var registration = cancellationToken.Register(
            static state =>
            {
                var (gate, queued, token) = ((FifoGate, Waiter, CancellationToken))state!;
                gate.Cancel(queued, token);
            },
            (this, waiter, cancellationToken));
        await waiter.Admitted.Task.ConfigureAwait(false);
        return new Lease(this);
    }

    private void Cancel(Waiter waiter, CancellationToken token)
    {
        lock (_lock)
        {
            // A waiter already admitted keeps its place: its caller takes the lease and its own token stops what it runs.
            if (waiter.Node is null) return;
            _queue.Remove(waiter.Node);
            waiter.Node = null;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static state => state.Waiter.Admitted.TrySetCanceled(state.Token), (Waiter: waiter, Token: token), preferLocal: false);
    }

    private void Release()
    {
        Waiter? next = null;
        lock (_lock)
        {
            if (_queue.First is { } head)
            {
                next = head.Value;
                _queue.RemoveFirst();
                next.Node = null;
            }
            else
            {
                _inside--;
            }
        }

        // The place passes straight to the head of the queue: _inside is unchanged.
        next?.Admitted.TrySetResult();
    }

    private sealed class Waiter
    {
        // Not RunContinuationsAsynchronously: the waiter's continuation runs on the thread that frees the place, so two places freed one after the other admit their waiters one after the other
        // and not in whatever order two thread-pool items start (SEM45 holds the order a continuation sees). What runs inline is the few lines after the await, which hand the run to the
        // session and return. A waiter that is cancelled is completed from the thread pool instead (Cancel), so no caller's code runs inside another caller's Cancel.
        internal TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.None);

        internal LinkedListNode<Waiter>? Node { get; set; }
    }

    private sealed class Lease(FifoGate gate) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) gate.Release();
        }
    }
}
