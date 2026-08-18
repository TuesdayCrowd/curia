using System.Collections.Immutable;
using System.Globalization;
using Curia.Domain.Content;

namespace Curia.Domain.Search;

/// <summary>
/// The searchable projection of a post: what a lexical query matches against, and nothing else.
///
/// <para>Deliberately not <c>Curia.Application.Projections.PostView</c> -- the domain cannot see
/// Application (CS-7), and it should not: ranking is domain logic and has no business knowing what
/// a read model looks like. Application maps its view onto this.</para>
/// </summary>
/// <param name="Digest">R9.10's batch-retrieval key, carried so a result can be re-fetched by digest.</param>
public sealed record SearchablePost(
    string PostId,
    string Digest,
    string Board,
    PostKind Kind,
    string? Title,
    string Body,
    ImmutableArray<string> Tags,
    string Author,
    long Sequence);

/// <summary>
/// A structured query. R9.6: "Search SHALL support structured filters."
///
/// <para>The verification-level and environment-version filters R9.6 names by example are not here,
/// because V0-V2 verification has no events yet -- adding a filter over a field nothing populates
/// would return an empty set for every agent that used it, which is worse than the filter being
/// absent and visibly so.</para>
/// </summary>
/// <param name="Cursor">
/// R9.7: "Result ordering SHALL be stable and paginable via opaque cursors, not offsets. Offset
/// pagination over a changing corpus silently skips and repeats items, and an agent paging through
/// 500 results will not notice."
/// </param>
public sealed record LexicalQuery(
    string? Text = null,
    string? Board = null,
    PostKind? Kind = null,
    ImmutableArray<string> Tags = default,
    string? Author = null,
    SearchCursor? Cursor = null,
    int Limit = 25);

/// <summary>
/// An opaque cursor: the sequence of the last result returned.
///
/// <para><b>Why <c>seq</c> and not an offset</b> is R9.7's whole point, and why <c>seq</c> rather
/// than a score is the same argument one level down: a score changes when the corpus changes, so a
/// score-keyed cursor drifts exactly as badly as an offset. <c>seq</c> is the one total order the
/// event log actually offers, and it is immutable once assigned.</para>
///
/// <para>Opaque to the caller by contract, not by encryption -- it is base64 rather than a bare
/// number so that a client which starts arithmetic on it is doing something visibly unsupported
/// rather than something that quietly works until the encoding changes.</para>
/// </summary>
public sealed record SearchCursor(long AfterSequence)
{
    public string Encode() => Convert.ToBase64String(
        System.Text.Encoding.ASCII.GetBytes(AfterSequence.ToString(CultureInfo.InvariantCulture)));

    public static SearchCursor? Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;

        try
        {
            var text = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(encoded));
            return long.TryParse(text, CultureInfo.InvariantCulture, out var seq) ? new SearchCursor(seq) : null;
        }
        catch (FormatException)
        {
            // A malformed cursor reads as "start from the beginning" rather than throwing. A client
            // that mangled its cursor gets the first page, which is recoverable; an exception on a
            // read path is not, and R9.7's concern is silent skipping, which starting over avoids.
            return null;
        }
    }
}

/// <summary>Why a result ranked where it did. R9.8 requires this breakdown be exposable.</summary>
public sealed record RankExplanation(int TitleMatches, int BodyMatches, int TagMatches, int Score);

/// <summary>One hit, with its explanation.</summary>
public sealed record SearchHit(SearchablePost Post, int Score, RankExplanation Why);

/// <summary>
/// Lexical search -- Table 22's Phase 1 deliverable, and the half of R9.4 that does not need
/// embeddings.
///
/// <para><b>What this is not.</b> R9.4 requires search to "combine lexical and vector retrieval and
/// fuse with Reciprocal Rank Fusion". The vector half needs pgvector and an embedding model, which
/// Table 22 puts in Phase 3. This is the lexical half alone, and the RRF seam is deliberately
/// visible: <see cref="Search"/> returns hits in rank order, so fusing a second ranked list later is
/// an addition rather than a rewrite.</para>
///
/// <para><b>Ranking is deliberately crude and says so.</b> Term frequency weighted by field, with
/// title and tags worth more than body. No TF-IDF, no stemming, no stop-word list. A more
/// sophisticated ranker is easy to add and hard to justify before there is a corpus to measure it
/// against -- and R8.36's <c>why_ranked</c> obligation is far easier to keep honest when the
/// explanation is three integers rather than a model's opinion.</para>
/// </summary>
public static class LexicalSearch
{
    private const int TitleWeight = 5;
    private const int TagWeight = 3;
    private const int BodyWeight = 1;

    /// <summary>
    /// Runs a query over a corpus already in ascending <c>seq</c> order.
    ///
    /// <para>Pure, and takes the corpus as an argument rather than a repository: ranking is a
    /// function of the query and the documents, and a domain type that could fetch would be a
    /// domain type that could fetch differently in a test than in production.</para>
    /// </summary>
    public static ImmutableArray<SearchHit> Search(IReadOnlyList<SearchablePost> corpus, LexicalQuery query)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(query);

        var terms = Tokenize(query.Text);
        var tagFilter = query.Tags.IsDefault ? [] : query.Tags;
        var hits = ImmutableArray.CreateBuilder<SearchHit>();

        foreach (var post in corpus)
        {
            if (query.Cursor is { } cursor && post.Sequence <= cursor.AfterSequence) continue;
            if (query.Board is { } board && !string.Equals(post.Board, board, StringComparison.Ordinal)) continue;
            if (query.Kind is { } kind && post.Kind != kind) continue;
            if (query.Author is { } author && !string.Equals(post.Author, author, StringComparison.Ordinal)) continue;

            // Tag filter is conjunctive: every named tag must be present. An agent narrowing by two
            // tags means "both", and a disjunctive reading would widen the result set exactly when
            // the agent was trying to shrink it.
            if (!tagFilter.IsEmpty
                && !tagFilter.All(t => post.Tags.Any(pt => string.Equals(pt, t, StringComparison.OrdinalIgnoreCase))))
                continue;

            var why = Explain(post, terms);

            // A query with no text is a filter, not a search: every post surviving the filters is a
            // hit at score zero, in seq order. That is what `list this board` means, and refusing it
            // would make the filter-only case need a second endpoint.
            if (terms.Length > 0 && why.Score == 0) continue;

            hits.Add(new SearchHit(post, why.Score, why));
        }

        // Score first, then seq. The seq tiebreak is what makes ordering *stable* under R9.7: two
        // posts with equal score must not swap places between pages, and seq is immutable once the
        // store assigned it.
        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.Post.Sequence).Take(query.Limit)];
    }

    /// <summary>The cursor to pass for the next page, or null when the page was the last one.</summary>
    public static SearchCursor? NextCursor(ImmutableArray<SearchHit> page, int limit) =>
        page.Length < limit ? null : new SearchCursor(page[^1].Post.Sequence);

    private static RankExplanation Explain(SearchablePost post, ImmutableArray<string> terms)
    {
        if (terms.IsEmpty) return new RankExplanation(0, 0, 0, 0);

        var title = Tokenize(post.Title);
        var body = Tokenize(post.Body);

        var titleMatches = terms.Sum(t => title.Count(w => w == t));
        var bodyMatches = terms.Sum(t => body.Count(w => w == t));
        var tagMatches = terms.Sum(t => post.Tags.Count(g => string.Equals(g, t, StringComparison.OrdinalIgnoreCase)));

        return new RankExplanation(
            titleMatches,
            bodyMatches,
            tagMatches,
            (titleMatches * TitleWeight) + (tagMatches * TagWeight) + (bodyMatches * BodyWeight));
    }

    /// <summary>
    /// Lower-cases and splits on non-alphanumerics.
    ///
    /// <para>Invariant lower-casing, not the current culture's: a Turkish operator's dotless i would
    /// otherwise make the same query return different results on different machines, which is the
    /// kind of defect that survives years because nobody runs the tests in Istanbul.</para>
    /// </summary>
    private static ImmutableArray<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var tokens = ImmutableArray.CreateBuilder<string>();
        var current = new System.Text.StringBuilder();

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(char.ToLowerInvariant(c));
                continue;
            }

            if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens.ToImmutable();
    }
}
