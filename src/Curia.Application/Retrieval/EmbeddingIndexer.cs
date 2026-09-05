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
/// runs at startup and indexes every discussion post the index has not seen, from the model's
/// high-water mark forward -- which is R11.10's "a model change is a reindex": a new model id
/// has a high-water mark of zero and the whole log is replayed into it.</para>
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
    /// Indexes every discussion post after the model's high-water mark. Returns how many were
    /// indexed. Withheld posts are indexed too: the index is keyed by digest and the serving path
    /// filters, so a later un-withholding needs no reindex.
    /// </summary>
    public async Task<Result<long>> ReconcileAsync(IEventReader events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var mark = await _index.MaxSequenceAsync(Model, cancellationToken).ConfigureAwait(false);
        if (!mark.TryGetValue(out var after, out var markError))
            return Result<long>.Fail(markError!);

        if (!EventSequence.From(after).TryGetValue(out var from, out var fromError))
            return Result<long>.Fail(fromError!);

        var indexed = 0L;
        while (true)
        {
            var page = await events.ReadForwardAsync(from, EventReaderExtensions.PageSize, cancellationToken).ConfigureAwait(false);
            if (!page.TryGetValue(out var batch, out var readError))
                return Result<long>.Fail(readError!);

            foreach (var appended in batch!)
            {
                if (SearchProjector.TryRead(appended) is not { } post) continue;

                var result = await IndexAsync(post, cancellationToken).ConfigureAwait(false);
                if (!result.TryGetValue(out var vector, out var indexError))
                    return Result<long>.Fail(indexError!);
                if (vector is not null) indexed++;
            }

            if (batch.Count < EventReaderExtensions.PageSize)
                return Result<long>.Ok(indexed);

            from = batch[^1].Seq;
        }
    }
}
