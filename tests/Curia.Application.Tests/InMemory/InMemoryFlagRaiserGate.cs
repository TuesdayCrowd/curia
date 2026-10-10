using System.Collections.Concurrent;
using Curia.Application.Moderation;
using Curia.Application.Ports;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>R11.4's in-memory <see cref="IFlagRaiserGate"/>: a set of the raisers whose flag is in flight, entered by try-add and never waited on.</summary>
internal sealed class InMemoryFlagRaiserGate : IFlagRaiserGate
{
    private readonly ConcurrentDictionary<string, byte> _held = new(StringComparer.Ordinal);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The hold is the result: the caller owns it and disposes it.")]
    public Task<Result<IAsyncDisposable>> TryEnterAsync(string raisedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(raisedBy);

        if (!_held.TryAdd(raisedBy, 0)) return Task.FromResult(Result<IAsyncDisposable>.Fail(FlagErrors.RaiseInFlight()));

        return Task.FromResult(Result<IAsyncDisposable>.Ok(new Hold(this, raisedBy)));
    }

    private sealed class Hold(InMemoryFlagRaiserGate gate, string raisedBy) : IAsyncDisposable
    {
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                gate._held.TryRemove(raisedBy, out _);

            return ValueTask.CompletedTask;
        }
    }
}
