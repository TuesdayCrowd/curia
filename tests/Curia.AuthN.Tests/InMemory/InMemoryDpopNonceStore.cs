using Curia.AuthN.Ports;
using Curia.Domain.Primitives;

namespace Curia.AuthN.Tests.InMemory;

/// <summary>
/// The R11.4 in-memory adapter for <see cref="IDpopNonceStore"/> (errata B4/R5.19): one
/// currently-active nonce at a time, rotated by calling <see cref="IssueAsync"/> again -- a real,
/// if single-instance, implementation of the "issue and require" half of R5.19 this stage owns
/// (the <c>use_dpop_nonce</c> challenge-and-retry flow itself is reference-client work per B4,
/// out of scope here).
/// </summary>
internal sealed class InMemoryDpopNonceStore : IDpopNonceStore
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _rotationInterval;
    private DpopNonce? _current;
    private long _counter;

    public InMemoryDpopNonceStore(TimeProvider clock, TimeSpan? rotationInterval = null)
    {
        _clock = clock;
        _rotationInterval = rotationInterval ?? AuthNConstants.MaxDpopNonceRotationInterval;
    }

    public Task<Result<DpopNonce>> IssueAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var value = $"nonce-{Interlocked.Increment(ref _counter)}";
            var nonce = new DpopNonce(value, _clock.GetUtcNow() + _rotationInterval);
            _current = nonce;
            return Task.FromResult(Result<DpopNonce>.Ok(nonce));
        }
    }

    public Task<Result<bool>> IsCurrentAsync(string nonce, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var isCurrent = _current is { } current
                && string.Equals(current.Value, nonce, StringComparison.Ordinal)
                && _clock.GetUtcNow() < current.ExpiresAt;
            return Task.FromResult(Result<bool>.Ok(isCurrent));
        }
    }
}
