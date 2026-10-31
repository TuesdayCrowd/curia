using Curia.Application.Ports;

namespace Curia.Application.Retrieval;

// Task 0 seam: the members below are inert and touch no instance data until the repair's task gives them a body; CA1822 is expected and ends with it.
#pragma warning disable CA1822

/// <summary>What one pass of the catch-up stored: the digest of each vector it brought into the index.</summary>
public sealed record CatchUpResult(IReadOnlyList<string> StoredDigests);

/// <summary>
/// The repair that brings a pending post into the index without a restart (errata G51, R11.69). It takes <c>now</c> as an argument so that the
/// schedule is a unit over instants. <b>Task 0 seam: inert.</b> A pass does nothing and stores nothing.
/// </summary>
public sealed class EmbeddingCatchUp
{
    public EmbeddingCatchUp(EmbeddingIndexer indexer, IndexBacklog backlog)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(backlog);
    }

    public Task<CatchUpResult> RunOnceAsync(IEventReader events, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        return Task.FromResult(new CatchUpResult([]));
    }
}
