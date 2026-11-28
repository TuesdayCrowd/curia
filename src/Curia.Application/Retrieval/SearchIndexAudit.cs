using System.Collections.Immutable;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Search;

namespace Curia.Application.Retrieval;

/// <summary>What is wrong with one post's rows: <c>missing</c> (the log has the post and the index does not), <c>different</c> (a row is not the one the post's content makes) or <c>extra</c> (a row for no discussion post).</summary>
public sealed record SearchIndexFinding(long Seq, string Kind);

/// <summary>The audit's result: how many discussion posts the log holds, how many findings there are, and the first <see cref="SearchIndexAudit.Listed"/> of them.</summary>
public sealed record SearchIndexAuditReport(long Posts, long FindingCount, ImmutableArray<SearchIndexFinding> Findings);

/// <summary>
/// Errata G88, R11.114, non-property (a): recomputes every discussion post's rows from the <b>log</b> (never from the index) and compares them with the stored ones. A derived table is a lever for a holder
/// of the database credential (rows that make a post match what it does not contain) and the application role cannot remove one, so this is how it is found; the owner role removes it. Read-only.
/// </summary>
public static class SearchIndexAudit
{
    /// <summary>How many findings the report lists; the count is of all.</summary>
    public const int Listed = 100;

    public static async Task<Result<SearchIndexAuditReport>> RunAsync(
        IEventReader events,
        ISearchIndex index,
        SearchIndexRules rules,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(rules);

        var findings = new List<SearchIndexFinding>();
        long count = 0, posts = 0;

        void Found(long seq, string kind)
        {
            count++;
            if (findings.Count < Listed) findings.Add(new SearchIndexFinding(seq, kind));
        }

        // A fault of the store while the stored rows are read is thrown as LogUnreadableException (R11.71): a stream cannot return a failed result, and a failed read must not read as an empty index.
        var stored = index.ReadAllAsync(rules.Version, cancellationToken).GetAsyncEnumerator(cancellationToken);
        await using (stored.ConfigureAwait(false))
        {
            var hasStored = await stored.MoveNextAsync().ConfigureAwait(false);

            var after = EventSequence.Zero;
            while (true)
            {
                var page = await events.ReadForwardAsync(after, SearchIndexReconcile.BatchSize, cancellationToken).ConfigureAwait(false);
                if (!page.TryGetValue(out var rows, out var readError)) return Result<SearchIndexAuditReport>.Fail(readError!);
                if (rows!.Count == 0) break;

                foreach (var row in rows)
                {
                    if (rules.Index(row) is not { } expected) continue;
                    posts++;

                    // Stored entries below this post's position belong to no post of the log.
                    while (hasStored && stored.Current.Seq < expected.Seq)
                    {
                        Found(stored.Current.Seq, "extra");
                        hasStored = await stored.MoveNextAsync().ConfigureAwait(false);
                    }

                    if (!hasStored || stored.Current.Seq > expected.Seq) { Found(expected.Seq, "missing"); continue; }

                    if (!stored.Current.HasDocument || !SameRows(expected, stored.Current.Post)) Found(expected.Seq, stored.Current.HasDocument ? "different" : "missing");
                    hasStored = await stored.MoveNextAsync().ConfigureAwait(false);
                }

                after = rows[^1].Seq;
            }

            while (hasStored)
            {
                Found(stored.Current.Seq, "extra");
                hasStored = await stored.MoveNextAsync().ConfigureAwait(false);
            }
        }

        return Result<SearchIndexAuditReport>.Ok(new SearchIndexAuditReport(posts, count, [.. findings]));
    }

    /// <summary>The filters and every term, equal; the terms as a set, because the stored order is the database's.</summary>
    private static bool SameRows(IndexedPost expected, IndexedPost stored) =>
        string.Equals(expected.PostId, stored.PostId, StringComparison.Ordinal)
        && string.Equals(expected.Digest, stored.Digest, StringComparison.Ordinal)
        && string.Equals(expected.Board, stored.Board, StringComparison.Ordinal)
        && string.Equals(expected.Kind, stored.Kind, StringComparison.Ordinal)
        && string.Equals(expected.Author, stored.Author, StringComparison.Ordinal)
        && expected.TermsTruncated == stored.TermsTruncated
        && expected.TagsFolded.SequenceEqual(stored.TagsFolded, StringComparer.Ordinal)
        && expected.Terms.Length == stored.Terms.Length
        && expected.Terms.OrderBy(t => t.Key, StringComparer.Ordinal).SequenceEqual(stored.Terms.OrderBy(t => t.Key, StringComparer.Ordinal));
}
