using System.Collections.Immutable;
using System.Globalization;
using Curia.Domain.Content;
using Curia.Domain.Primitives;

namespace Curia.Domain.Search;

/// <summary>
/// The searchable projection of a post: what a lexical query matches against, and nothing else.
///
/// <para>Deliberately not <c>Curia.Application.Projections.PostView</c> -- the domain cannot see
/// Application (CS-7), and it should not: ranking is domain logic and has no business knowing what
/// a read model looks like. Application maps its view onto this.</para>
/// </summary>
/// <param name="Digest">R9.10's batch-retrieval key, carried so a result can be re-fetched by digest.</param>
/// <param name="Owner">
/// The author's attested owner (R4.30), where one is known. R10.7 diversifies on "a single author
/// <i>or owner</i>", and the owner arm is the half that survives an adversary giving each post its
/// own agent -- <c>attest-owner</c> binds one agent at a time and caps the number of agents an
/// owner may hold at nothing. Null where the author has no attestation, which
/// <see cref="HybridRanking.Diversify"/> reads as the author being its own owner: never wider than
/// the evidence, and never weaker than the author cap.
/// </param>
public sealed record SearchablePost(
    string PostId,
    string Digest,
    string Board,
    PostKind Kind,
    string? Title,
    string Body,
    ImmutableArray<string> Tags,
    string Author,
    long Sequence,
    string? PossibleDuplicateOf = null,
    string? Owner = null)
{
    /// <summary>
    /// Structural equality, spelled out rather than left to the compiler because
    /// <see cref="ImmutableArray{T}"/>'s own <c>Equals</c> compares the underlying array
    /// <i>reference</i>. The generated record equality therefore reports two posts projected from
    /// the very same event as different, purely because each fold allocated its own array.
    ///
    /// <para>That is not cosmetic here for the same reason it was not on <c>AgentStanding</c>, where
    /// this exact defect shipped and was green: R11.9's rebuild-from-zero drill is asserted by
    /// comparing a projection against its own rebuild, so a type whose <c>==</c> is false for
    /// identical content makes the drill unassertable — a test that cannot fail, reporting the same
    /// green as one that passes.</para>
    /// </summary>
    public bool Equals(SearchablePost? other) =>
        other is not null
        && string.Equals(PostId, other.PostId, StringComparison.Ordinal)
        && string.Equals(PossibleDuplicateOf, other.PossibleDuplicateOf, StringComparison.Ordinal)
        && string.Equals(Digest, other.Digest, StringComparison.Ordinal)
        && string.Equals(Board, other.Board, StringComparison.Ordinal)
        && Kind == other.Kind
        && string.Equals(Title, other.Title, StringComparison.Ordinal)
        && string.Equals(Body, other.Body, StringComparison.Ordinal)
        && string.Equals(Author, other.Author, StringComparison.Ordinal)
        && string.Equals(Owner, other.Owner, StringComparison.Ordinal)
        && Sequence == other.Sequence
        && Tags.SequenceEqual(other.Tags);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(
        PostId,
        Digest,
        Board,
        Kind,
        Title,
        Body,
        Author,

        // Sequence, the owner and the tag count folded together: a hash has only to agree with
        // Equals on the values that are equal, and Combine takes eight arguments.
        HashCode.Combine(Sequence, Owner, Tags.Length));
}

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
    ImmutableArray<PostKind> Kinds = default,
    ImmutableArray<string> Tags = default,
    string? Author = null,
    SearchCursor? Cursor = null,
    int Limit = LexicalSearch.DefaultLimit);

/// <summary>
/// An opaque cursor: the position of the last result returned, as the pair the results are actually
/// ordered by.
///
/// <para><b>The cursor has to carry the whole sort key, and originally carried only <c>seq</c>.</b>
/// That is the defect this type was rewritten for. Results are ordered by score descending and seq
/// ascending; a cursor keyed on <c>seq</c> alone told the next page to skip everything below the
/// seq of the <i>lowest-scoring</i> row on the previous page, which is an arbitrary position in the
/// ordering. Paging a static ten-post corpus three at a time returned
/// <c>[post-10, post-9, post-8, post-10, post-9]</c> — two rows twice, seven rows never. Those are
/// both of the failure modes R9.7 names, on a corpus that was not even changing.</para>
///
/// <para><b>Why a score-keyed cursor is safe here</b>, against the general argument that scores
/// drift and make a cursor as bad as an offset. That argument is right about search engines in
/// general and does not apply to this corpus: a lexical score is a pure function of a post's content
/// and the query terms, post content is immutable (there is no redaction primitive, by
/// construction — R10.26), and the log is append-only. So the score of an already-returned post for
/// an already-issued query cannot change. A post appended later can land on a page not yet fetched;
/// nothing already returned moves. That is exactly the stability R9.7 asks for, and it holds because
/// of properties §6 already guarantees rather than because of anything this type does.</para>
///
/// <para>Opaque to the caller by contract, not by encryption -- it is base64 rather than a bare
/// pair so that a client which starts arithmetic on it is doing something visibly unsupported
/// rather than something that quietly works until the encoding changes.</para>
/// </summary>
/// <param name="AfterScore">The score of the last result returned.</param>
/// <param name="AfterSequence">The <c>seq</c> of the last result returned, which breaks score ties.</param>
public sealed record SearchCursor(int AfterScore, long AfterSequence)
{
    public string Encode() => Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes(
        string.Create(CultureInfo.InvariantCulture, $"{AfterScore}:{AfterSequence}")));

    /// <summary>
    /// R9.38: reads a cursor strictly. Absent or white space is <c>Ok(null)</c>, a first page; the one spelling
    /// <see cref="Encode"/> writes is the cursor; anything else is <c>curia/inbox/cursor-malformed</c> and the refusal
    /// echoes nothing of the value.
    ///
    /// <para>This used to read a malformed value as "start from the beginning", on the premise that a cursor an agent
    /// could not have been given is not worth a refusal. The premise fails for a listing that is paged: a corrupted
    /// cursor then sends an agent round page one while it believes it is advancing, which is what R9.25 asked search to
    /// refuse and what <see cref="RetrievalCursor.Decode"/> does. A sign, a leading zero or white space in either
    /// numeral, and a cursor of another route, are other spellings of a position and are refused: the value is accepted
    /// only if encoding what it parsed to gives back the value, so no position has two cursors.</para>
    /// </summary>
    public static Result<SearchCursor?> Decode(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return Result<SearchCursor?>.Ok(null);

        try
        {
            var text = System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(encoded));
            var separator = text.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0) return Malformed();

            var cursor = IsCanonicalNumeral(text[..separator]) && IsCanonicalNumeral(text[(separator + 1)..])
                && int.TryParse(text.AsSpan(..separator), NumberStyles.None, CultureInfo.InvariantCulture, out var score)
                && long.TryParse(text.AsSpan((separator + 1)..), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
                    ? new SearchCursor(score, seq)
                    : null;

            return cursor is not null && string.Equals(cursor.Encode(), encoded, StringComparison.Ordinal)
                ? Result<SearchCursor?>.Ok(cursor)
                : Malformed();
        }
        catch (FormatException)
        {
            return Malformed();
        }
    }

    /// <summary>The refusal of R9.38: it names no part of the value, which is a caller's string.</summary>
    private static Result<SearchCursor?> Malformed() => Result<SearchCursor?>.Fail(new Error(
        "curia/inbox/cursor-malformed",
        "The `cursor` member is not one this route minted",
        "Pass `next_cursor` back exactly as the previous page returned it, or omit `cursor` for the first page."));

    /// <summary>Digits only, and no leading zero unless the numeral is a lone zero: the one spelling <see cref="Encode"/> writes.</summary>
    private static bool IsCanonicalNumeral(string text) =>
        text.Length > 0 && text.All(char.IsAsciiDigit) && (text.Length == 1 || text[0] != '0');

    /// <summary>
    /// Whether <paramref name="hit"/> sorts strictly after this cursor, in the one ordering
    /// <see cref="LexicalSearch.Search"/> produces: score descending, then seq ascending.
    /// </summary>
    internal bool Precedes(SearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);

        return hit.Score < AfterScore
            || (hit.Score == AfterScore && hit.Post.Sequence > AfterSequence);
    }
}

/// <summary>
/// What a query text means to the keyword channel (R9.72): <paramref name="Terms"/>, the tokens not on the list, in the query's order and with repeats;
/// <paramref name="Ignored"/>, the tokens the list removed, once each in the query's order; and <paramref name="HadToken"/>, whether the text had any token at all.
/// A listing is a text with no token, <b>not</b> a text with no term: a text whose every token was ignored lists no post.
/// </summary>
public sealed record QueryTerms(ImmutableArray<string> Terms, ImmutableArray<string> Ignored, bool HadToken);

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
/// title and tags worth more than body. No TF-IDF, no stemming. The one list it has is on the
/// <i>query</i>: R9.72's published list of common English words (<see cref="StopWords"/>) is removed from a
/// query's terms and never from a post's text (errata G81). A more
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
        ArgumentNullException.ThrowIfNull(query);

        var ranked = Rank(corpus, query);

        // Keyset pagination on that same ordering. Skipping by predicate rather than by index is
        // what makes this not an offset: a post appended since the cursor was issued shifts no
        // already-returned row, because the comparison is against a position in the order rather
        // than a count of rows before it.
        var page = query.Cursor is { } cursor ? ranked.Where(cursor.Precedes) : ranked;

        return [.. page.Take(PageSize(query.Limit))];
    }

    /// <summary>
    /// The whole lexical ordering of the corpus for a query -- every hit, scored, best first --
    /// with no cursor and no page applied. Hybrid retrieval fuses this with the vector ordering
    /// before either is paged (R9.4), which is why the ranking is exposed separately from
    /// <see cref="Search"/>'s page of it.
    /// </summary>
    public static ImmutableArray<SearchHit> Rank(IReadOnlyList<SearchablePost> corpus, LexicalQuery query)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(query);

        return Rank(corpus, query, SearchTerms(query.Text));
    }

    /// <summary>
    /// The ordering for terms already chosen. <see cref="Rank(IReadOnlyList{SearchablePost}, LexicalQuery)"/> passes <see cref="SearchTerms(string?)"/>' result; a measurement
    /// that wants the channel without the list passes <c>new QueryTerms(Tokenize(text), [], true)</c>. The switch is <paramref name="terms"/>' <c>HadToken</c> and its terms, never
    /// <c>terms.Length &gt; 0</c> alone: a text whose every token was ignored has no term and is not a listing (R9.72).
    /// </summary>
    public static ImmutableArray<SearchHit> Rank(IReadOnlyList<SearchablePost> corpus, LexicalQuery query, QueryTerms terms)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(terms);

        // A text of only ignored words lists no post: not a listing, and not a fallback to its tokens.
        if (terms.HadToken && terms.Terms.IsEmpty) return [];

        var hits = ImmutableArray.CreateBuilder<SearchHit>();

        foreach (var post in corpus)
        {
            // No cursor test here. The cursor names a position in the *ranked* order, which is not
            // known until every candidate has been scored -- testing it against the corpus order was
            // the defect. It is applied below, after the sort.
            if (!Matches(post, query)) continue;

            var why = Explain(post, terms.Terms);

            // A query with no text is a filter, not a search: every post surviving the filters is a
            // hit at score zero, in seq order. That is what `list this board` means, and refusing it
            // would make the filter-only case need a second endpoint.
            if (terms.HadToken && why.Score == 0) continue;

            hits.Add(new SearchHit(post, why.Score, why));
        }

        // Score first, then seq. The seq tiebreak is what makes ordering *stable* under R9.7: two
        // posts with equal score must not swap places between pages, and seq is immutable once the
        // store assigned it.
        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.Post.Sequence)];
    }

    /// <summary>
    /// The largest page this will produce, whatever a caller asks for.
    ///
    /// <para>There was no cap: <c>Take(query.Limit)</c> served the caller's number, so one request
    /// could ask the Forum to rank and materialise the whole corpus. Capped in the domain rather
    /// than at the HTTP boundary because a domain function has to be total over its inputs and the
    /// transport is not the only thing that will ever call this -- the route refuses an
    /// out-of-range <c>limit</c> outright, so a client is told rather than quietly served fewer,
    /// and this remains true for every other caller.</para>
    /// </summary>
    public const int MaximumLimit = 100;

    /// <summary>The default page size when a caller expresses no preference.</summary>
    public const int DefaultLimit = 25;

    /// <summary>
    /// Errata G88, R9.79: the most distinct terms (R9.72's, after the list) a search query may name. The statement over an index costs the postings of its terms, so a request line of two thousand distinct words
    /// (an 8 KB line holds them) cost 263 ms at 12,000 posts and 1.2 s at 30,000 where twenty cost 9 and 39 (Task 0c, item iii); a natural-language question is fifteen to twenty-five tokens and the list removes
    /// a third of them. 32 is the first power of two above that, and the cost at it is the measured twenty terms' and a little more: a search for the words that matter most, not for a paragraph.
    /// </summary>
    public const int MaximumQueryTerms = 32;

    /// <summary>
    /// R9.79: the refusal of a query that names more than <see cref="MaximumQueryTerms"/> distinct terms, or <see langword="null"/>. A refusal and not a truncation: dropping the tail would answer another
    /// question than the one asked and the page would not say so. It states the count and the limit and echoes nothing of the text, which is a caller's string.
    /// </summary>
    public static Error? RefuseLongQuery(string? text)
    {
        var distinct = SearchTerms(text).Terms.Distinct(StringComparer.Ordinal).Count();
        return distinct <= MaximumQueryTerms
            ? null
            : new Error(
                "curia/search/query-too-many-terms",
                "A search query names too many distinct words",
                $"terms={distinct.ToString(CultureInfo.InvariantCulture)}; limit={MaximumQueryTerms.ToString(CultureInfo.InvariantCulture)}; search for the words that matter most, or run several searches");
    }

    private static int PageSize(int requested) => Math.Clamp(requested, 1, MaximumLimit);

    /// <summary>
    /// R9.6's structured filters alone: does this post survive board, kind, author and tags?
    ///
    /// <para>Public and separate from <see cref="Search"/> because a caller that needs the same
    /// filters over a corpus it is <i>counting</i> rather than ranking has no other honest way to
    /// get them. The inbox does exactly that — it reports how many open questions matched an agent's
    /// filters before its personal exclusions, so an empty inbox can say which kind of empty it is.
    /// A second copy of these predicates is how the two readings of "matching" come to disagree.</para>
    /// </summary>
    public static bool Matches(SearchablePost post, LexicalQuery query)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(query);

        if (query.Board is { } board && !string.Equals(post.Board, board, StringComparison.Ordinal)) return false;
        // R9.26: a set, and disjunctive where the tag filter is conjunctive. A post has many tags
        // and exactly one kind, so "both tags" narrows and "either kind" is the only reading that is
        // not empty by construction. An empty or defaulted set is no criterion at all, never "no
        // kinds" -- R9.25 says a request naming no criterion returns everything the corpus matches.
        var kindFilter = query.Kinds.IsDefault ? [] : query.Kinds;
        if (!kindFilter.IsEmpty && !kindFilter.Contains(post.Kind)) return false;
        if (query.Author is { } author && !string.Equals(post.Author, author, StringComparison.Ordinal)) return false;

        // Tag filter is conjunctive: every named tag must be present. An agent narrowing by two
        // tags means "both", and a disjunctive reading would widen the result set exactly when
        // the agent was trying to shrink it.
        var tagFilter = query.Tags.IsDefault ? [] : query.Tags;

        return tagFilter.IsEmpty
            || tagFilter.All(t => post.Tags.Any(pt => string.Equals(pt, t, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The cursor to pass for the next page, or null when the page was the last one.</summary>
    public static SearchCursor? NextCursor(ImmutableArray<SearchHit> page, int limit) =>
        page.Length < PageSize(limit) ? null : new SearchCursor(page[^1].Score, page[^1].Post.Sequence);

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
    /// R9.72: the terms the keyword channel uses for a query text and the words it ignored. The terms are <see cref="Tokenize"/>'s tokens that are not on <see cref="StopWords"/>;
    /// the ignored words are the tokens the list removed, once each, in the query's order. Applied to a query's text only: <see cref="Explain"/> tokenizes a post with
    /// <see cref="Tokenize"/>, unchanged.
    /// </summary>
    public static QueryTerms SearchTerms(string? text)
    {
        var tokens = Tokenize(text);
        var ignored = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var token in tokens)
            if (StopWords.Contains(token) && seen.Add(token)) ignored.Add(token);

        return new QueryTerms([.. tokens.Where(t => !StopWords.Contains(t))], ignored.ToImmutable(), !tokens.IsEmpty);
    }

    /// <summary>
    /// Lower-cases and splits on non-alphanumerics: maximal runs of letters and digits, lower-cased.
    ///
    /// <para>Invariant lower-casing, not the current culture's: a Turkish operator's dotless i would
    /// otherwise make the same query return different results on different machines, which is the
    /// kind of defect that survives years because nobody runs the tests in Istanbul.</para>
    ///
    /// <para>Public because the dedupe's lexical-overlap floor (R8.18) is measured over these tokens (errata G81's
    /// R8.18 (rev.): R9.72's list does not apply to it); a second tokenizer would be a second opinion about what a word is.</para>
    /// </summary>
    public static ImmutableArray<string> Tokenize(string? text)
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
