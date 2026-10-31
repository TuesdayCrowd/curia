using Curia.Domain.Search;

namespace Curia.Application.Retrieval;

/// <summary>What became of a committed post's vector (R8.81).</summary>
public enum CommittedIndexState
{
    Indexed,
    Pending,
    NotApplicable,
}

/// <summary>The state of one committed post's vector and, for a pending one, the type and detail of the fault.</summary>
public sealed record IndexOutcome(CommittedIndexState State, string? FaultType = null, string? FaultDetail = null);

/// <summary>
/// The step after PERSIST that stores a committed post's vector (errata G51, R8.81). <b>Task 0 seam: behaviour-preserving.</b> It forwards to
/// <see cref="EmbeddingIndexer"/> on the caller's token with no bound, reports a failed result as pending, catches no exception and holds
/// nothing in the backlog: each of those is a probe that is red by assertion until the answer's task replaces this body.
/// </summary>
public sealed class CommittedPostIndexer
{
    /// <summary>How long the step may take before the post is pending (provisional; R8.81 publishes it).</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    private readonly EmbeddingIndexer _indexer;

    public CommittedPostIndexer(EmbeddingIndexer indexer, IndexBacklog backlog, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(backlog);
        ArgumentNullException.ThrowIfNull(clock);
        _indexer = indexer;
    }

    public async Task<IndexOutcome> IndexAsync(SearchablePost post, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(post);

        var stored = await _indexer.IndexAsync(post, shutdown).ConfigureAwait(false);
        if (!stored.TryGetValue(out var vector, out var error))
            return new IndexOutcome(CommittedIndexState.Pending, error!.Type, error.Detail);

        return new IndexOutcome(vector is null ? CommittedIndexState.NotApplicable : CommittedIndexState.Indexed);
    }
}
