using System.Globalization;
using Curia.Application.Ports;
using Curia.Domain.Primitives;

namespace Curia.Application.Retrieval;

/// <summary>What a start found: the schema as the role sees it, whether this start recorded the version's digests, and the reconcile that brought the index up to the log.</summary>
public sealed record SearchIndexStartReport(SearchIndexSchema Schema, bool RecordedVersion, SearchIndexReconcileReport Reconcile);

/// <summary>
/// Errata G88, R11.114, R11.116: what the Forum settles about the keyword channel's index before it listens, in this order, each refusal naming what is wrong and never a value.
/// <list type="number">
/// <item>db/0008 is applied (tables, indexes, <c>SELECT</c> and <c>INSERT</c>): <c>curia/search-index/not-applied</c>.</item>
/// <item>The running runtime's letter-and-case digest is the one the build commits: <c>curia/search-index/tokenizer-changed</c>. A runtime upgrade that assigns a new letter fails here, at deploy, and not as a post
/// that tokenizes differently on the two sides.</item>
/// <item>The digests recorded for this index version are the running ones (recorded now when the version has none): <c>curia/search-index/version-digests-differ</c>.</item>
/// <item>The reconcile (insert-only) leaves no discussion post without its rows: <c>curia/search-index/incomplete</c>.</item>
/// </list>
/// </summary>
public static class SearchIndexStartCheck
{
    public static async Task<Result<SearchIndexStartReport>> RunAsync(
        IEventReader events,
        ISearchIndex index,
        SearchIndexRules rules,
        Action<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(rules);

        var schemaAnswer = await index.CheckSchemaAsync(cancellationToken).ConfigureAwait(false);
        if (!schemaAnswer.TryGetValue(out var schema, out var schemaError)) return Result<SearchIndexStartReport>.Fail(schemaError!);

        if (!schema!.TablesPresent || !schema.IndexesPresent || !schema.CanSelectAndInsert)
            return Fail(
                "curia/search-index/not-applied",
                "db/0008 is not applied to this database",
                $"tables={Said(schema.TablesPresent)}; indexes={Said(schema.IndexesPresent)}; select_and_insert={Said(schema.CanSelectAndInsert)}; the database's owner applies db/0008 before this build runs");

        if (!rules.RuntimeTokenizerDigest().SequenceEqual(rules.CommittedTokenizerDigest))
            return Fail(
                "curia/search-index/tokenizer-changed",
                "The runtime's letter and case tables are not those this build was committed with",
                $"index_version={rules.Version.ToString(CultureInfo.InvariantCulture)}; write version {(rules.Version + 1).ToString(CultureInfo.InvariantCulture)} with the files regenerated, or run the build's runtime");

        var recordedAnswer = await index.ReadVersionAsync(rules.Version, cancellationToken).ConfigureAwait(false);
        if (!recordedAnswer.TryGetValue(out var recorded, out var recordedError)) return Result<SearchIndexStartReport>.Fail(recordedError!);

        var recordedNow = false;
        if (recorded is null)
        {
            var wrote = await index.RecordVersionAsync(rules.Version, rules.RuntimeTokenizerDigest(), rules.FoldDigest, cancellationToken).ConfigureAwait(false);
            if (!wrote.TryGetValue(out recordedNow, out var writeError)) return Result<SearchIndexStartReport>.Fail(writeError!);

            // A second Forum may have recorded it between the read and the write: what is held is what counts.
            var again = await index.ReadVersionAsync(rules.Version, cancellationToken).ConfigureAwait(false);
            if (!again.TryGetValue(out recorded, out var againError)) return Result<SearchIndexStartReport>.Fail(againError!);
        }

        if (recorded is null
            || !recorded.TokenizerDigest.SequenceEqual(rules.RuntimeTokenizerDigest())
            || !recorded.FoldDigest.SequenceEqual(rules.FoldDigest))
            return Fail(
                "curia/search-index/version-digests-differ",
                "The tokenizer or the tag-fold table is not the one index version " + rules.Version.ToString(CultureInfo.InvariantCulture) + " was written under",
                $"index_version={rules.Version.ToString(CultureInfo.InvariantCulture)}; write version {(rules.Version + 1).ToString(CultureInfo.InvariantCulture)}, never change a version's rules in place");

        var reconciled = await SearchIndexReconcile.RunAsync(events, index, rules, progress, cancellationToken).ConfigureAwait(false);
        if (!reconciled.TryGetValue(out var report, out var reconcileError)) return Result<SearchIndexStartReport>.Fail(reconcileError!);

        if (report!.StillMissing > 0)
            return Fail(
                "curia/search-index/incomplete",
                "Discussion posts have no rows in the search index after the reconcile",
                $"missing={report.StillMissing.ToString(CultureInfo.InvariantCulture)}; the index would drop them from search and say nothing");

        return Result<SearchIndexStartReport>.Ok(new SearchIndexStartReport(schema, recordedNow, report));

        static string Said(bool ok) => ok ? "ok" : "absent";
        static Result<SearchIndexStartReport> Fail(string type, string title, string detail) => Result<SearchIndexStartReport>.Fail(new Error(type, title, detail));
    }
}
