using System.Collections.Immutable;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Search;

namespace Curia.Application.Retrieval;

/// <summary>
/// The rules an index version is written under (errata G88): the tag-fold table and the digests the build commits for the tokenizer and for that table. <see cref="Index"/> is the one place that turns an
/// appended row into the rows the index holds, so the append, the reconcile and the audit cannot disagree about what a post's rows are.
/// </summary>
public sealed class SearchIndexRules
{
    private readonly Func<ImmutableArray<byte>> _runtimeTokenizerDigest;

    /// <param name="runtimeTokenizerDigest">The digest of this runtime's letter-and-case tables; <see cref="TokenizerPin.Compute"/> where omitted. A test passes one that differs to see the start refuse.</param>
    public SearchIndexRules(
        TagFoldTable fold,
        ImmutableArray<byte> foldDigest,
        ImmutableArray<byte> committedTokenizerDigest,
        Func<ImmutableArray<byte>>? runtimeTokenizerDigest = null,
        short version = SearchIndex.Version)
    {
        ArgumentNullException.ThrowIfNull(fold);
        Fold = fold;
        FoldDigest = foldDigest;
        CommittedTokenizerDigest = committedTokenizerDigest;
        Version = version;
        _runtimeTokenizerDigest = runtimeTokenizerDigest ?? (() => [.. TokenizerPin.Compute()]);
    }

    public short Version { get; }

    public TagFoldTable Fold { get; }

    /// <summary>The SHA-256 of the committed tag-fold table's bytes.</summary>
    public ImmutableArray<byte> FoldDigest { get; }

    /// <summary>The digest the build commits for the tokenizer (<c>conformance/retrieval/tokenizer-units.sha256</c>).</summary>
    public ImmutableArray<byte> CommittedTokenizerDigest { get; }

    /// <summary>The digest of the running runtime's tables.</summary>
    public ImmutableArray<byte> RuntimeTokenizerDigest() => _runtimeTokenizerDigest();

    /// <summary>The rows of <paramref name="row"/>, or <see langword="null"/> when it is not a discussion post (the same predicate the vector index and the lexical corpus use).</summary>
    public IndexedPost? Index(AppendedEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return SearchProjector.TryRead(row) is { } post ? SearchIndex.IndexTerms(post, Fold) : null;
    }
}
