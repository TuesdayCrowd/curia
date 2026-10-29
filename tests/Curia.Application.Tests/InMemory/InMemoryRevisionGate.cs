using System.Collections.Concurrent;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>R11.4's in-memory <see cref="IRevisionGate"/> (R8.77; errata G45): a set of the digests whose revision is in flight, entered by try-add and never waited on.</summary>
internal sealed class InMemoryRevisionGate : IRevisionGate
{
    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The hold is the result: the caller owns it and disposes it.")]
    public Task<Result<IAsyncDisposable>> TryEnterAsync(string prevDigest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prevDigest);

        if (!_held.TryAdd(prevDigest, 0)) return Task.FromResult(Result<IAsyncDisposable>.Fail(IngestErrors.RevisionInFlight()));

        return Task.FromResult(Result<IAsyncDisposable>.Ok(new Hold(this, prevDigest)));
    }

    private sealed class Hold(InMemoryRevisionGate gate, string prevDigest) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate._held.TryRemove(prevDigest, out _);

            return ValueTask.CompletedTask;
        }
    }
}
