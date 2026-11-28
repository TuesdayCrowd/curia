using System.Collections.Immutable;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Search;

namespace Curia.Application.Retrieval;

/// <summary>What a reconcile found and did. <paramref name="StillMissing"/> is counted after it, from the database, and is what the start refuses on.</summary>
public sealed record SearchIndexReconcileReport(long Posts, long AlreadyIndexed, long Inserted, long StillMissing);

/// <summary>
/// Errata G88, R11.9, R11.114: brings the index up to the log by <b>inserting</b> the rows of every discussion post that has none under the current version. Insert-only, so the application role can run
/// it (no owner role, no UPDATE) and a row that exists is left as it is: a wrong row is the audit's finding. Used by the start-up check and by <c>curia-operator reindex-search</c>.
/// </summary>
public static class SearchIndexReconcile
{
    /// <summary>How many events are read, and how many posts are inserted, at once.</summary>
    public const int BatchSize = 500;

    public static async Task<Result<SearchIndexReconcileReport>> RunAsync(
        IEventReader events,
        ISearchIndex index,
        SearchIndexRules rules,
        Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(rules);

        var held = await index.IndexedSeqsAsync(rules.Version, cancellationToken).ConfigureAwait(false);
        if (!held.TryGetValue(out var indexed, out var heldError)) return Result<SearchIndexReconcileReport>.Fail(heldError!);

        var discussionSeqs = new List<long>();
        long alreadyIndexed = 0, inserted = 0;
        var after = EventSequence.Zero;
        while (true)
        {
            var page = await events.ReadForwardAsync(after, BatchSize, cancellationToken).ConfigureAwait(false);
            if (!page.TryGetValue(out var rows, out var readError)) return Result<SearchIndexReconcileReport>.Fail(readError!);
            if (rows!.Count == 0) break;

            var missing = new List<IndexedPost>();
            foreach (var row in rows)
            {
                if (rules.Index(row) is not { } post) continue;
                discussionSeqs.Add(post.Seq);
                if (indexed!.Contains(post.Seq)) alreadyIndexed++;
                else missing.Add(post);
            }

            if (missing.Count > 0)
            {
                var wrote = await index.InsertAsync(rules.Version, missing, cancellationToken).ConfigureAwait(false);
                if (!wrote.TryGetValue(out var count, out var writeError)) return Result<SearchIndexReconcileReport>.Fail(writeError!);
                inserted += count;
                progress?.Invoke(inserted);
            }

            after = rows[^1].Seq;
        }

        // Counted again, from the database: what the Forum refuses to start on is what is not there now, not what this run meant to write.
        var after2 = await index.IndexedSeqsAsync(rules.Version, cancellationToken).ConfigureAwait(false);
        if (!after2.TryGetValue(out var now, out var nowError)) return Result<SearchIndexReconcileReport>.Fail(nowError!);

        return Result<SearchIndexReconcileReport>.Ok(new SearchIndexReconcileReport(
            discussionSeqs.Count, alreadyIndexed, inserted, discussionSeqs.Count(seq => !now!.Contains(seq))));
    }
}
