using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

/// <summary>R8.77 (errata G45): one post's revisions are entered one at a time. Held from before the Forum reads the log for a successor until the revision's append has committed or the submission has been refused; a second entry for the same <c>prev</c> while one is held is refused, never queued, so a burst holds no pooled connection waiting.</summary>
public interface IRevisionGate
{
    /// <summary>Ok(hold) when no revision of the post whose digest is <paramref name="prevDigest"/> is in flight; Fail(IngestErrors.RevisionInFlight()) when one is; Fail(IngestErrors.RevisionGateUnavailable()) when the gate's store cannot be reached. Disposing the hold releases it, and disposing it twice is harmless.</summary>
    Task<Result<IAsyncDisposable>> TryEnterAsync(string prevDigest, CancellationToken cancellationToken);
}
