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

        return await StoreAsync(post, embedding!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Result<IndexedVector?>> StoreAsync(SearchablePost post, Embedding embedding, CancellationToken cancellationToken)
    {
        var stored = await _index.UpsertAsync(post.Digest, post.PostId, post.Sequence, embedding, cancellationToken).ConfigureAwait(false);
        return stored.Map(v => (IndexedVector?)v);
    }

    /// <summary>
    /// Indexes every discussion post whose digest the index does not hold under the model (R11.69), not the posts after the index's highest sequence: a post
    /// whose write failed and was followed by one that succeeded is below that mark for ever. Returns how many were indexed. A post the model cannot place
    /// (<c>NoFeatures</c>, D24) is skipped, never failed, and so is looked at again at every start. Withheld posts are indexed too: the index is keyed by
    /// digest and the serving path filters, so a later un-withholding needs no reindex. A model change is R11.10's reindex: a new model id holds no digests.
    /// </summary>
    public async Task<Result<long>> ReconcileAsync(IEventReader events, CancellationToken cancellationToken = default) =>
        await ReconcileAsync(events, sweep: null, cancellationToken).ConfigureAwait(false);

    /// <summary><see cref="ReconcileAsync(IEventReader, CancellationToken)"/>, noting to <paramref name="sweep"/> the posts the model cannot place.</summary>
    public async Task<Result<long>> ReconcileAsync(IEventReader events, IndexSweep? sweep, CancellationToken cancellationToken = default)
    {
        var stored = new Collection<string>();
        var reconciled = await ReconcileByAbsenceAsync(events, stored, sweep, cancellationToken).ConfigureAwait(false);
        return reconciled.Map(_ => (long)stored.Count);
    }

    /// <summary>
    /// <see cref="ReconcileAsync"/>, appending to <paramref name="stored"/> the digest of each vector it stored (the catch-up reports them, R11.69). It indexes
    /// what <see cref="FindAbsentAsync"/> finds, one post at a time as the scan meets it, and returns the first failure.
    /// </summary>
    public async Task<Result<bool>> ReconcileByAbsenceAsync(
        IEventReader events, Collection<string> stored, IndexSweep? sweep = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(stored);

        var scan = await FindAbsentAsync(
            events,
            sweep,
            async (post, embedding, token) =>
            {
                var result = await StoreAsync(post, embedding, token).ConfigureAwait(false);
                if (!result.TryGetValue(out var vector, out var indexError)) return indexError;
                if (vector is not null) stored.Add(post.Digest);
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        return scan.Failure is { } failure ? Result<bool>.Fail(failure) : Result<bool>.Ok(true);
    }

    /// <summary>
    /// The one absence rule (errata G54, decision 1): a discussion post the log holds whose digest the index does not hold under the model and that the model can
    /// place. Reads the log in pages of <see cref="EventReaderExtensions.PageSize"/> and keeps none; returns each such post as a digest, a post id and a sequence
    /// and never its text. A post the model cannot place (<c>NoFeatures</c>, D24) is skipped and noted to <paramref name="sweep"/>'s memory when one is given. Where
    /// <paramref name="each"/> is given it is called with each post as the scan meets it, and a returned error ends the scan with that error; a scan that cannot
    /// complete returns what it found before the failure, so a post known to be absent is never un-known.
    /// </summary>
    public async Task<AbsentScan> FindAbsentAsync(
        IEventReader events, IndexSweep? sweep, Func<SearchablePost, Embedding, CancellationToken, Task<Error?>>? each, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);

        var found = new List<AbsentPost>();
        var held = await _index.IndexedDigestsAsync(Model, cancellationToken).ConfigureAwait(false);
        if (!held.TryGetValue(out var digests, out var heldError))
            return new AbsentScan(found, heldError!);

        var present = new HashSet<string>(digests!, StringComparer.Ordinal);
        if (!EventSequence.From(0).TryGetValue(out var from, out var fromError))
            return new AbsentScan(found, fromError!);

        while (true)
        {
            var page = await events.ReadForwardAsync(from, EventReaderExtensions.PageSize, cancellationToken).ConfigureAwait(false);
            if (!page.TryGetValue(out var batch, out var readError))
                return new AbsentScan(found, readError!);

            foreach (var appended in batch!)
            {
                if (SearchProjector.TryRead(appended) is not { } post || present.Contains(post.Digest)) continue;

                // A post the model could not place at an earlier check, and a post whose index step is running in this process, are skipped (R8.83).
                if (sweep is not null && (sweep.IsUnplaceable(post.Digest) || sweep.IsIndexing(post.Digest))) continue;

                var embedded = _embedder.Embed(EmbeddedText.Of(post));
                if (!embedded.TryGetValue(out var embedding, out var embedError))
                {
                    if (!string.Equals(embedError!.Type, EmbeddingErrors.NoFeatures().Type, StringComparison.Ordinal))
                        return new AbsentScan(found, embedError);

                    sweep?.NoteUnplaceable(post.Digest);
                    continue;
                }

                present.Add(post.Digest);
                found.Add(new AbsentPost(post.Digest, post.PostId, post.Sequence));
                if (each is not null && await each(post, embedding!, cancellationToken).ConfigureAwait(false) is { } eachError)
                    return new AbsentScan(found, eachError);
            }

            if (batch.Count < EventReaderExtensions.PageSize)
                return new AbsentScan(found, null);

            from = batch[^1].Seq;
        }
    }
}

/// <summary>A discussion post the log holds and the index lacks: its digest, its post id and its sequence, never its text.</summary>
public sealed record AbsentPost(string Digest, string PostId, long Sequence);

/// <summary>What <see cref="EmbeddingIndexer.FindAbsentAsync"/> found, and the error that ended the scan where it did not complete.</summary>
public sealed record AbsentScan(IReadOnlyList<AbsentPost> Found, Error? Failure);
