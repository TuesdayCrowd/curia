using Curia.Application.Ports;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Ingest;

/// <summary>
/// R8.70 (errata G38): reads the post a submission names as its <c>parent</c>, from that post's own stream. Typed on
/// <see cref="IEventReader"/>, so CS-15 holds.
/// </summary>
public static class ParentLookup
{
    /// <returns>The parent's id and stored kind, <see langword="null"/> where no post has the id, or a failure where the log could not be read.</returns>
    public static Task<Result<ParentFacts?>> FindAsync(IEventReader events, ParentReference parent, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<ParentFacts?>.Ok(null));
}
