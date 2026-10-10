using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

/// <summary>R7.22, R10.70 (errata G18): one raiser's flags are counted and recorded one at a time. Held from before the budget is counted until the flag's append has committed; a second entry for the same raiser while one is held is refused, never queued, so a burst holds no pooled connection waiting.</summary>
public interface IFlagRaiserGate
{
    /// <summary>Ok(hold) when no flag by <paramref name="raisedBy"/> is in flight; Fail(FlagErrors.RaiseInFlight()) when one is; Fail(FlagErrors.RaiserGateUnavailable()) when the gate's store cannot be reached. Disposing the hold releases it, and disposing it twice is harmless.</summary>
    Task<Result<IAsyncDisposable>> TryEnterAsync(string raisedBy, CancellationToken cancellationToken);
}
