using System.Collections.Immutable;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Curia.Domain.Retrieval;
using Curia.Domain.Search;
using Curia.Domain.Verification;

namespace Curia.Application.Retrieval;

/// <summary>What a search asks: the query and filters (R9.6), the floor it wants (else the surface's), where it left off, and how much.</summary>
public sealed record SearchQuery(
    string? Text,
    BoardName? Board,
    ImmutableArray<PostKind> Kinds,
    ImmutableArray<string> Tags,
    AgentId? Author,
    VerificationLevel? RequestedFloor,
    RetrievalCursor? Cursor,
    int Limit);

/// <summary>
/// A page of hybrid retrieval, with everything the response states about how it was made.
/// <c>ExcludedDisputed</c> (R6.57) counts the posts the current log disputes among the diversified
/// list the cursor's offset indexes, so it is equal on every page of one query; a disputed post
/// below the verification floor is not in that list and is not counted. <c>ExcludedByFloor</c>
/// (R9.30) counts the posts the floor removed from the fused candidate list, before diversification
/// and the window, so it is equal on every page of one query; it is a lower bound on what the floor
/// hides from the corpus, because each channel cuts its candidates at the candidate depth. Neither
/// count has a default: a forgotten pass-through must not compile into "removed nothing". A page may be shorter than
/// its limit, or empty with a cursor, because disputed posts are dropped after the offset.
/// </summary>
public sealed record SearchPage(
    ImmutableArray<RankedPost> Results,
    RetrievalCursor? Next,
    RetrievalSurface Surface,
    VerificationLevel Floor,
    string FloorSource,
    EmbeddingModel Model,
    long CorpusBound,
    int CandidateDepth,
    int K,
    double MinimumCosine,
    int ExcludedByFloor,
    int ExcludedDisputed,
    // R9.31: the surface's published default, the level the request named (null where it named none)
    // and whether the request was served at a higher floor. None has a default for the reason the counts do not.
    VerificationLevel PublishedDefault,
    VerificationLevel? RequestedFloor,
    bool Clamped,
    ChannelCounts? ByChannel,
    int Diversified,
    // R9.72: the query's tokens the keyword channel ignored (the published list), in the query's order, once each; empty where nothing was ignored and for a listing.
    ImmutableArray<string> IgnoredTerms,
    // R9.72 (Amendment 3): the distinct terms the keyword channel took from the query; 0 for a listing and for a query of only listed words.
    int KeywordTerms);

/// <summary>R9.67: how many of the served page's results each channel chose.</summary>
public sealed record ChannelCounts(int Words, int Meaning, int WordsAndMeaning);

/// <summary>
/// §9.2's pipeline: lexical and vector candidates fused by reciprocal rank fusion (R9.4), Table
/// 13's weight, the surface's floor (R10.2), diversification (R10.6, R10.7), and a page cut from a
/// corpus fixed at the cursor's bound (R9.7).
///
/// <para><b>There is no overload that takes one channel.</b> A hybrid searcher that could run
/// without its vector port would be a lexical searcher with a vector's name, which is the
/// fallback the plan forbids and R10.1 calls a security-relevant change. A missing adapter does
/// not compile a working search.</para>
/// </summary>
public sealed class HybridSearch
{
    private readonly ITextEmbedder _embedder;
    private readonly IVectorIndex _index;
    private readonly RetrievalFloors _floors;
    private readonly double _minimumCosine;

    /// <summary>The floor of a model with no row: every cosine is at least this, so no neighbour is removed (errata G66, R11.102).</summary>
    private const double NoFloor = -1.0;

    /// <summary>
    /// <paramref name="row"/> is the served model's row of <see cref="RetrievalRows"/>: its floor is the vector channel's published minimum cosine (R9.22), read here and at the page's
    /// construction. <see langword="null"/> is a model with no row, which removes nothing. It has no default, so a forgotten argument does not compile into a floor of nothing.
    /// </summary>
    public HybridSearch(ITextEmbedder embedder, IVectorIndex index, RetrievalFloors floors, RetrievalRow? row)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(floors);
        _embedder = embedder;
        _index = index;
        _floors = floors;
        _minimumCosine = row?.Floor ?? NoFloor;
    }

    /// <summary>
    /// Every step of a search at which it can be made to wait (errata G71, R9.65): the floor and the clamp, the cursor's binding (a refused request embeds nothing and asks the index nothing, R9.35),
    /// then, for a query with text, the embedding in the query role (R11.92's gate) and the index's neighbours. It takes no log: the caller reads the log after this returns and hands it to
    /// <see cref="Search"/>, so what the page is cut from is as new as the wait allows.
    /// </summary>
    public async Task<Result<PreparedSearch>> PrepareAsync(RetrievalSurface surface, SearchQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var limit = Math.Clamp(query.Limit, 1, LexicalSearch.MaximumLimit);

        var (surfaceFloor, source) = _floors.Resolve(surface);

        // R10.54 (rev.), R9.31: a request may raise the surface's floor and never lower it. A request below it is served at the floor in force and the page says so; `source` names the request
        // only where the request's level is the one applied.
        var clamped = query.RequestedFloor is { } asked && asked < surfaceFloor;
        var floor = query.RequestedFloor is { } requested && !clamped ? requested : surfaceFloor;

        // R9.35: a cursor continues the query that minted it and no other. The floor in force is a member of the binding, so
        // the check follows the floor and precedes both channels: a refused request embeds nothing and asks the index nothing.
        var binding = QueryBinding.Of(query.Text, query.Board?.Value, query.Author?.Value, query.Kinds, query.Tags, query.RequestedFloor, floor, limit);
        if (query.Cursor is { } presented && !string.Equals(presented.Binding, binding, StringComparison.Ordinal))
            return Result<PreparedSearch>.Fail(RetrievalCursor.QueryMismatch());

        var neighbours = await NeighboursAsync(query.Text, cancellationToken).ConfigureAwait(false);
        return neighbours.TryGetValue(out var matches, out var error)
            ? Result<PreparedSearch>.Ok(new PreparedSearch(surface, query, floor, query.RequestedFloor is null || clamped ? source : "requested", clamped, binding, limit, matches))
            : Result<PreparedSearch>.Fail(error!);
    }

    /// <summary>
    /// The rest of a search, synchronous (errata G71, R9.65): nothing from the log read to the page can wait. The corpus, its bound and the dispute mark are folded from <paramref name="verified"/>, and
    /// the prepared neighbours (over the whole index) are kept where the corpus holds them.
    /// </summary>
    public Result<SearchPage> Search(VerifiedLog verified, PreparedSearch prepared, VerificationFold verification)
    {
        ArgumentNullException.ThrowIfNull(verified);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(verification);

        var query = prepared.Query;
        var surface = prepared.Surface;
        var floor = prepared.Floor;
        var limit = prepared.Limit;
        var binding = prepared.Binding;
        var clamped = prepared.Clamped;
        var log = verified.Log;

        // R9.7: the corpus a page is cut from is the corpus as of the cursor's bound, and a first
        // page fixes that bound at the newest event it can see. Posts appended afterwards join on
        // the next fresh query, never mid-page.
        var bound = query.Cursor?.CorpusBound ?? (log.Count == 0 ? 0 : log[^1].Seq.Value);
        var offset = query.Cursor?.Offset ?? 0;

        // R9.47: a digest is one result. A log that holds two posts of one digest (the same bytes stored twice, before R8.78 refused it)
        // is read, not repaired: the digest resolves to the first post in log order, and the second copy is served by its id alone.
        var seenDigests = new HashSet<string>(StringComparer.Ordinal);
        var corpus = SearchProjector.FoldHonoured(verified).Where(p => p.Sequence <= bound && seenDigests.Add(p.Digest)).ToImmutableArray();
        var lexicalQuery = new LexicalQuery(query.Text, query.Board?.Value, query.Kinds, query.Tags, query.Author?.Value, Cursor: null, Limit: LexicalSearch.MaximumLimit);
        // R9.72: the channel ranks the query's terms, and says which tokens it ignored. The switch is the terms' HadToken and not their count: a query of only listed words lists no post.
        var searchTerms = LexicalSearch.SearchTerms(query.Text);
        var lexical = LexicalSearch.Rank(corpus, lexicalQuery, searchTerms);

        var vectorHits = VectorHits(corpus, lexicalQuery, prepared.Neighbours);

        var fused = HybridRanking.Rank(lexical, vectorHits, p => verification.LevelOf(p.Digest));

        // R6.57 is judged over the whole log the caller holds, not the cursor's corpus bound: R9.7
        // fixes which posts a page is cut from, it does not make a declaration appended after page 1
        // invisible to page 2. The offset indexes a list fixed by the cursor's CorpusBound, the query
        // and the floor, so Admit and Diversify run over the unfiltered fused list, disputed posts
        // included, and a post the current log disputes is dropped from the PAGE after the offset is
        // applied (a current-log fact is applied to a page's content, never to the list an offset
        // indexes, or a declaration between pages would silently skip a kept post). A page may
        // therefore come back short, and a continuation empty with a cursor still present, on a fresh
        // query as well as on a continuation; that is visible because `excluded_disputed` is stated.
        // It counts the disputed posts in the diversified list (those that survive the floor and
        // take a diversification slot), so it is equal on every page of one query and can be 0 while
        // a disputed post below the floor exists. `read` by id still serves a disputed post.
        var disputed = DisputedPosts.Fold(verified, PostProjector.Fold(log));

        // R9.67: the mark is read from the ranks fusion holds, and only for a query with a term: a filter alone, or a text with no term, ran no channel to name.
        var hasTerm = !string.IsNullOrWhiteSpace(query.Text) && LexicalSearch.Tokenize(query.Text).Length > 0;
        if (hasTerm)
            fused = [.. fused.Select(r => r with { MatchedBy = r.LexicalRank > 0 && r.VectorRank > 0 ? ChannelMark.WordsAndMeaning : r.LexicalRank > 0 ? ChannelMark.Words : ChannelMark.Meaning })];

        var admitted = HybridRanking.Admit(fused, floor);
        var excludedByFloor = fused.Length - admitted.Length;   // R9.30: the floor's removals alone, before dispute
        var diversified = HybridRanking.Diversify(admitted, limit);

        var window = diversified.Skip(offset).Take(limit);
        var page = window.Where(r => !disputed.IsDisputed(r.Post)).ToImmutableArray();
        var next = offset + limit < diversified.Length ? new RetrievalCursor(bound, offset + limit, binding) : null;
        var excludedDisputed = diversified.Count(r => disputed.IsDisputed(r.Post));

        // R9.68: how many posts the page's windows moved later, over the whole list and before the dispute drop, so it is equal on every page of one query.
        var diversifiedCount = diversified.Count(r => r.Deferred);
        var byChannel = hasTerm
            ? new ChannelCounts(
                page.Count(r => r.MatchedBy == ChannelMark.Words),
                page.Count(r => r.MatchedBy == ChannelMark.Meaning),
                page.Count(r => r.MatchedBy == ChannelMark.WordsAndMeaning))
            : null;

        return Result<SearchPage>.Ok(new SearchPage(
            page,
            next,
            surface,
            floor,
            prepared.FloorSource,
            _embedder.Model,
            bound,
            HybridRanking.CandidateDepth,
            RankFusion.DefaultK,
            _minimumCosine,
            excludedByFloor,
            excludedDisputed,
            RetrievalFloorPolicy.PublishedFloor(surface),
            query.RequestedFloor,
            clamped,
            byChannel,
            diversifiedCount,
            searchTerms.Ignored,
            searchTerms.Terms.Distinct(StringComparer.Ordinal).Count()));
    }

    /// <summary>
    /// The vector channel's waits: the query embedded once and the index's nearest neighbours. A query with no text has no vector channel -- a filter is not a
    /// search -- and a query with no embeddable text has none either.
    /// </summary>
    private async Task<Result<ImmutableArray<VectorMatch>>> NeighboursAsync(string? text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result<ImmutableArray<VectorMatch>>.Ok([]);

        // A search query is embedded in the query role (errata G65, R9.63): a model with an instruction on one side means something different on each. A stored post and a
        // submitted question are passages.
        var embedded = await _embedder.EmbedAsync(text, EmbeddingRole.Query, cancellationToken).ConfigureAwait(false);
        if (!embedded.TryGetValue(out var embedding, out var embedError))
        {
            return string.Equals(embedError!.Type, EmbeddingErrors.NoFeatures().Type, StringComparison.Ordinal)
                ? Result<ImmutableArray<VectorMatch>>.Ok([])
                : Result<ImmutableArray<VectorMatch>>.Fail(embedError);
        }

        return await _index.NearestAsync(embedding!, HybridRanking.CandidateDepth, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The neighbours kept where they are in the filtered corpus, at or above the cosine line.</summary>
    private ImmutableArray<VectorHit> VectorHits(ImmutableArray<SearchablePost> corpus, LexicalQuery filters, ImmutableArray<VectorMatch> matches)
    {
        // R9.47: two stored posts of one digest resolve to the first in log order; a repeat never throws.
        var byDigest = new Dictionary<string, SearchablePost>(StringComparer.Ordinal);
        foreach (var post in corpus.Where(p => LexicalSearch.Matches(p, filters)))
            byDigest.TryAdd(post.Digest, post);

        var hits = ImmutableArray.CreateBuilder<VectorHit>();
        foreach (var match in matches)
        {
            var cosine = 1.0 - match.Distance;
            if (cosine < _minimumCosine) break;   // nearest first: nothing after this is closer
            if (byDigest.TryGetValue(match.Digest, out var post))
                hits.Add(new VectorHit(post, cosine));
        }

        return hits.ToImmutable();
    }
}
