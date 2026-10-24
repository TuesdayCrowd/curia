using System.Collections.Immutable;
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
    int ExcludedDisputed);

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

    public HybridSearch(ITextEmbedder embedder, IVectorIndex index, RetrievalFloors floors)
    {
        ArgumentNullException.ThrowIfNull(embedder);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(floors);
        _embedder = embedder;
        _index = index;
        _floors = floors;
    }

    public async Task<Result<SearchPage>> SearchAsync(
        IReadOnlyList<AppendedEvent> log,
        RetrievalSurface surface,
        SearchQuery query,
        VerificationFold verification,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(verification);

        // R9.7: the corpus a page is cut from is the corpus as of the cursor's bound, and a first
        // page fixes that bound at the newest event it can see. Posts appended afterwards join on
        // the next fresh query, never mid-page.
        var bound = query.Cursor?.CorpusBound ?? (log.Count == 0 ? 0 : log[^1].Seq.Value);
        var offset = query.Cursor?.Offset ?? 0;
        var limit = Math.Clamp(query.Limit, 1, LexicalSearch.MaximumLimit);

        var corpus = SearchProjector.Fold(log).Where(p => p.Sequence <= bound).ToImmutableArray();
        var lexicalQuery = new LexicalQuery(query.Text, query.Board?.Value, query.Kinds, query.Tags, query.Author?.Value, Cursor: null, Limit: LexicalSearch.MaximumLimit);
        var lexical = LexicalSearch.Rank(corpus, lexicalQuery);

        var vector = await VectorChannelAsync(corpus, lexicalQuery, cancellationToken).ConfigureAwait(false);
        if (!vector.TryGetValue(out var vectorHits, out var vectorError))
            return Result<SearchPage>.Fail(vectorError!);

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
        var disputed = DisputedPosts.Fold(log, PostProjector.Fold(log));

        var (surfaceFloor, source) = _floors.Resolve(surface);
        var floor = query.RequestedFloor ?? surfaceFloor;
        var admitted = HybridRanking.Admit(fused, floor);
        var excludedByFloor = fused.Length - admitted.Length;   // R9.30: the floor's removals alone, before dispute
        var diversified = HybridRanking.Diversify(admitted, limit);

        var window = diversified.Skip(offset).Take(limit);
        var page = window.Where(r => !disputed.IsDisputed(r.Post)).ToImmutableArray();
        var next = offset + limit < diversified.Length ? new RetrievalCursor(bound, offset + limit) : null;
        var excludedDisputed = diversified.Count(r => disputed.IsDisputed(r.Post));

        return Result<SearchPage>.Ok(new SearchPage(
            page,
            next,
            surface,
            floor,
            query.RequestedFloor is null ? source : "requested",
            _embedder.Model,
            bound,
            HybridRanking.CandidateDepth,
            RankFusion.DefaultK,
            HybridRanking.MinimumCosine,
            excludedByFloor,
            excludedDisputed));
    }

    /// <summary>
    /// The vector channel: the query embedded once, the index's nearest neighbours, kept where they
    /// are in the filtered corpus. A query with no text has no vector channel -- a filter is not a
    /// search -- and a query with no embeddable text has none either.
    /// </summary>
    private async Task<Result<ImmutableArray<VectorHit>>> VectorChannelAsync(
        ImmutableArray<SearchablePost> corpus, LexicalQuery filters, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(filters.Text))
            return Result<ImmutableArray<VectorHit>>.Ok([]);

        var embedded = _embedder.Embed(filters.Text);
        if (!embedded.TryGetValue(out var embedding, out var embedError))
        {
            return string.Equals(embedError!.Type, EmbeddingErrors.NoFeatures().Type, StringComparison.Ordinal)
                ? Result<ImmutableArray<VectorHit>>.Ok([])
                : Result<ImmutableArray<VectorHit>>.Fail(embedError);
        }

        var nearest = await _index.NearestAsync(embedding!, HybridRanking.CandidateDepth, cancellationToken).ConfigureAwait(false);
        if (!nearest.TryGetValue(out var matches, out var nearestError))
            return Result<ImmutableArray<VectorHit>>.Fail(nearestError!);

        var byDigest = corpus.Where(p => LexicalSearch.Matches(p, filters)).ToDictionary(p => p.Digest, StringComparer.Ordinal);
        var hits = ImmutableArray.CreateBuilder<VectorHit>();
        foreach (var match in matches)
        {
            var cosine = 1.0 - match.Distance;
            if (cosine < HybridRanking.MinimumCosine) break;   // nearest first: nothing after this is closer
            if (byDigest.TryGetValue(match.Digest, out var post))
                hits.Add(new VectorHit(post, cosine));
        }

        return Result<ImmutableArray<VectorHit>>.Ok(hits.ToImmutable());
    }
}
