using Curia.Domain.Search;

namespace Curia.Application.Retrieval;

/// <summary>What became of a committed post's vector (R8.81).</summary>
public enum CommittedIndexState
{
    Indexed,
    Pending,
    NotApplicable,
}

/// <summary>The state of one committed post's vector and, for a pending one, the type of the fault and its fixed detail (never an exception's message, which can quote the post).</summary>
public sealed record IndexOutcome(CommittedIndexState State, string? FaultType = null, string? FaultDetail = null);

/// <summary>
/// The step after PERSIST that stores a committed post's vector (errata G51, R8.81). The log holds the post, so nothing this step does may make the submission
/// fail: it does not throw, a failed result, an exception and the bound are alike <see cref="CommittedIndexState.Pending"/>, the caller's disconnection does not
/// reach it (only <c>shutdown</c> does), and the bound is made from the <see cref="TimeProvider"/> (CS-9). A pending post goes into the backlog by digest, post id
/// and sequence, and the catch-up retries it from the log (R11.69).
/// </summary>
public sealed class CommittedPostIndexer
{
    /// <summary>How long the step may take before the post is pending (provisional; R8.81 publishes it).</summary>
    public static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);

    private readonly EmbeddingIndexer _indexer;
    private readonly IndexBacklog _backlog;
    private readonly TimeProvider _clock;
    private readonly IndexSweep _sweep;

    public CommittedPostIndexer(EmbeddingIndexer indexer, IndexBacklog backlog, TimeProvider clock, IndexSweep sweep)
    {
        ArgumentNullException.ThrowIfNull(indexer);
        ArgumentNullException.ThrowIfNull(backlog);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(sweep);
        _indexer = indexer;
        _backlog = backlog;
        _clock = clock;
        _sweep = sweep;
    }

    public async Task<IndexOutcome> IndexAsync(SearchablePost post, CancellationToken shutdown)
    {
        ArgumentNullException.ThrowIfNull(post);

        // The post is registered as in flight for the life of the step (errata G54, R8.83): a check that runs meanwhile does not enrol it.
        _sweep.BeginIndexing(post.Digest);
        try
        {
            return await StepAsync(post, shutdown).ConfigureAwait(false);
        }
        finally
        {
            _sweep.EndIndexing(post.Digest);
        }
    }

    private async Task<IndexOutcome> StepAsync(SearchablePost post, CancellationToken shutdown)
    {
        string faultType;
        string faultDetail;
        try
        {
            using var bound = new CancellationTokenSource(Bound, _clock);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, bound.Token);
            try
            {
                var stored = await _indexer.IndexAsync(post, linked.Token).ConfigureAwait(false);
                if (stored.TryGetValue(out var vector, out var error))
                {
                    // Only a stored vector is a success of the store (R11.69 (rev.)): a post the model cannot place proves nothing about it.
                    if (vector is null) return new IndexOutcome(CommittedIndexState.NotApplicable);

                    _backlog.NoteSuccess(_clock.GetUtcNow());
                    return new IndexOutcome(CommittedIndexState.Indexed);
                }

                faultType = error!.Type;
                faultDetail = error.Title;
            }
            catch (OperationCanceledException) when (bound.IsCancellationRequested)
            {
                faultType = "curia/retrieval/index-bound";
                faultDetail = $"the vector step did not finish within {Bound.TotalSeconds:0} seconds";
            }
        }
#pragma warning disable CA1031 // R8.81: the post is committed, so no fault of the vector step may leave this method; every one is pending and logged by the caller.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            faultType = ex.GetType().FullName ?? ex.GetType().Name;
            faultDetail = "the vector step threw an exception";
        }

        var now = _clock.GetUtcNow();
        _backlog.Add(new PendingEntry(post.Digest, post.PostId, post.Sequence), now + IndexBacklog.FirstWait);
        return new IndexOutcome(CommittedIndexState.Pending, faultType, faultDetail);
    }
}
