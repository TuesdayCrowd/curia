using System.Collections.ObjectModel;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Search;

namespace Curia.Application.Retrieval;

/// <summary>
/// Keeps the vector index in step with the log.
///
/// <para>Two paths, one computation. <see cref="IndexAsync"/> runs at the end of a submission,
/// after PERSIST, so the vector channel sees a post the moment the lexical channel does
/// (R6.22's spirit: served and searchable are the same instant). <see cref="ReconcileAsync"/>
/// runs at startup and indexes every discussion post whose digest the index does not hold under the
/// model -- which is R11.10's "a model change is a reindex": a new model id holds nothing and the
/// whole log is replayed into it. It does not start after the index's highest sequence (R11.69).</para>
///
/// <para>Non-discussion posts (votes, verification reports) are not indexed, for the reason
/// <see cref="SearchProjector"/> does not search them; they are skipped, never failed.</para>
/// </summary>
public sealed class EmbeddingIndexer
{
    private readonly ITextEmbedder _embedder;
    private readonly IVectorIndex _index;

    public EmbeddingIndexer(ITextEmbedder embedder, IVectorIndex index)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(index);
        _embedder = embedder;
        _index = index;
    }

    public EmbeddingModel Model => _embedder.Model;

    /// <summary>Embeds and stores one searchable post. A post with no embeddable text is skipped and reported as <see langword="null"/>.</summary>
    public async Task<Result<IndexedVector?>> IndexAsync(SearchablePost post, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(post);

        var embedded = _embedder.Embed(EmbeddedText.Of(post));
        if (!embedded.TryGetValue(out var embedding, out var embedError))
        {
            // No letters or digits at all: nothing to place in the space. Not an error of the
            // post's -- it was admitted -- and not something a retry would change.
            return string.Equals(embedError!.Type, EmbeddingErrors.NoFeatures().Type, StringComparison.Ordinal)
                ? Result<IndexedVector?>.Ok(null)
                : Result<IndexedVector?>.Fail(embedError);
        }

        var stored = await _index.UpsertAsync(post.Digest, post.PostId, post.Sequence, embedding!, cancellationToken).ConfigureAwait(false);
        return stored.Map(v => (IndexedVector?)v);
    }

    /// <summary>
    /// Indexes every discussion post whose digest the index does not hold under the model (R11.69), not the posts after the index's highest sequence: a post
    /// whose write failed and was followed by one that succeeded is below that mark for ever. Returns how many were indexed. A post the model cannot place
    /// (<c>NoFeatures</c>, D24) is skipped, never failed, and so is looked at again at every start. Withheld posts are indexed too: the index is keyed by
    /// digest and the serving path filters, so a later un-withholding needs no reindex. A model change is R11.10's reindex: a new model id holds no digests.
    /// </summary>
    public async Task<Result<long>> ReconcileAsync(IEventReader events, CancellationToken cancellationToken = default)
    {
        var stored = new Collection<string>();
        var reconciled = await ReconcileByAbsenceAsync(events, stored, cancellationToken).ConfigureAwait(false);
        return reconciled.Map(_ => (long)stored.Count);
    }

    /// <summary>
    /// <see cref="ReconcileAsync"/>, appending to <paramref name="stored"/> the digest of each vector it stored (the catch-up reports them, R11.69).
    /// </summary>
    public async Task<Result<bool>> ReconcileByAbsenceAsync(IEventReader events, Collection<string> stored, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(stored);

        var held = await _index.IndexedDigestsAsync(Model, cancellationToken).ConfigureAwait(false);
        if (!held.TryGetValue(out var digests, out var heldError))
            return Result<bool>.Fail(heldError!);

        var present = new HashSet<string>(digests!, StringComparer.Ordinal);
        if (!EventSequence.From(0).TryGetValue(out var from, out var fromError))
            return Result<bool>.Fail(fromError!);

        while (true)
        {
            var page = await events.ReadForwardAsync(from, EventReaderExtensions.PageSize, cancellationToken).ConfigureAwait(false);
            if (!page.TryGetValue(out var batch, out var readError))
                return Result<bool>.Fail(readError!);

            foreach (var appended in batch!)
            {
                if (SearchProjector.TryRead(appended) is not { } post || present.Contains(post.Digest)) continue;

                var result = await IndexAsync(post, cancellationToken).ConfigureAwait(false);
                if (!result.TryGetValue(out var vector, out var indexError))
                    return Result<bool>.Fail(indexError!);
                if (vector is null) continue;

                present.Add(post.Digest);
                stored.Add(post.Digest);
            }

            if (batch.Count < EventReaderExtensions.PageSize)
                return Result<bool>.Ok(true);

            from = batch[^1].Seq;
        }
    }
}
