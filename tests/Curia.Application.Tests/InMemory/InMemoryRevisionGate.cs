using Curia.Application.Ports;
using Curia.Domain.Primitives;

namespace Curia.Application.Tests.InMemory;

/// <summary>
/// R11.4's in-memory <see cref="IRevisionGate"/> (R8.77; errata G45). <b>A seam of PR 1's Task 0:</b> it admits every entry and its hold releases
/// nothing, so <c>RevisionGatePortContractTests</c> fails by assertion and not by compile error. Task 1 replaces it with the flag gate's shape
/// (<c>InMemoryFlagRaiserGate</c>): a set of the digests whose revision is in flight, entered by try-add and never waited on.
/// </summary>
internal sealed class InMemoryRevisionGate : IRevisionGate
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The hold is the result: the caller owns it and disposes it.")]
    public Task<Result<IAsyncDisposable>> TryEnterAsync(string prevDigest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prevDigest);

        return Task.FromResult(Result<IAsyncDisposable>.Ok(new Hold()));
    }

    private sealed class Hold : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
