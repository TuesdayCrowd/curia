using System.Collections.Immutable;
using Curia.Domain.Primitives;
using Curia.Domain.Search;

namespace Curia.Application.Ports;

/// <summary>What the database holds of the keyword channel's index, as the role the Forum connects as can see it (errata G88, R11.114).</summary>
/// <param name="TablesPresent">The three tables exist.</param>
/// <param name="IndexesPresent">Every index the migration creates exists, the primary keys and the GIN on the folded tags included.</param>
/// <param name="CanSelectAndInsert">The role holds SELECT and INSERT on each table, which an append of this build needs.</param>
/// <param name="CanUpdateOrDelete">The role holds UPDATE, DELETE or TRUNCATE on a table: db/0008 is not applied as written, or the role is a superuser or the owner. The Forum says so and starts.</param>
public sealed record SearchIndexSchema(bool TablesPresent, bool IndexesPresent, bool CanSelectAndInsert, bool CanUpdateOrDelete);

/// <summary>What <c>search_index_versions</c> records for an index version: the digests of the tokenizer's letter tables and of the fold table the rows were written under (R11.116).</summary>
public sealed record SearchIndexVersionRecord(ImmutableArray<byte> TokenizerDigest, ImmutableArray<byte> FoldDigest);

/// <summary>One post's stored rows. <paramref name="HasDocument"/> is false for terms whose <c>seq</c> has no <c>search_docs</c> row, which the audit reports as extra.</summary>
public sealed record StoredIndexEntry(long Seq, bool HasDocument, IndexedPost Post);

/// <summary>
/// The keyword channel's index as a port (errata G88): derived rows in the database, append-only by grant. <b>No member updates or deletes</b>, and the writes are <c>ON CONFLICT DO NOTHING</c>:
/// a row that exists is never replaced, so a wrong row is the audit's finding and the owner's to remove, never this port's to repair. The append of the event store writes the same rows in its own
/// transaction through the adapter's internals (R11.115); this port is what start-up, the reconcile and the audit use. A store that cannot be asked answers a failed result of type
/// <c>curia/store/unavailable</c>.
/// </summary>
public interface ISearchIndex
{
    /// <summary>The tables, indexes and grants as the connected role sees them.</summary>
    Task<Result<SearchIndexSchema>> CheckSchemaAsync(CancellationToken cancellationToken = default);

    /// <summary>The digests recorded for <paramref name="version"/>, or <see langword="null"/> when none is.</summary>
    Task<Result<SearchIndexVersionRecord?>> ReadVersionAsync(short version, CancellationToken cancellationToken = default);

    /// <summary>Records <paramref name="version"/>'s digests once; a version already recorded is left as it is. True when this call recorded it.</summary>
    Task<Result<bool>> RecordVersionAsync(short version, ImmutableArray<byte> tokenizerDigest, ImmutableArray<byte> foldDigest, CancellationToken cancellationToken = default);

    /// <summary>The <c>seq</c> of every post that has a document under <paramref name="version"/>.</summary>
    Task<Result<IReadOnlySet<long>>> IndexedSeqsAsync(short version, CancellationToken cancellationToken = default);

    /// <summary>Inserts the rows of each post that has no document under <paramref name="version"/> yet, in one transaction; returns how many posts were inserted.</summary>
    Task<Result<long>> InsertAsync(short version, IReadOnlyList<IndexedPost> posts, CancellationToken cancellationToken = default);

    /// <summary>Every stored entry of <paramref name="version"/> in ascending <c>seq</c> order, documents and their terms; the terms of a <c>seq</c> without a document are one entry with no document. A fault of the store is thrown as <see cref="LogUnreadableException"/> (R11.71): a stream has no failed result, and a read that failed must not read as an empty index.</summary>
    IAsyncEnumerable<StoredIndexEntry> ReadAllAsync(short version, CancellationToken cancellationToken = default);
}
