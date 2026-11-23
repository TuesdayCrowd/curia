using System.Collections.Immutable;
using Curia.Domain.Retrieval;
using Curia.Domain.Verification;

namespace Curia.Domain.Search;

/// <summary>One vector-channel candidate: the post and its cosine similarity to the query.</summary>
public sealed record VectorHit(SearchablePost Post, double Cosine);

/// <summary>
/// A post after fusion, weighting and diversification -- every term R8.36 asks for that this
/// build computes, kept apart so <c>why_ranked</c> can recombine them (errata G10).
/// </summary>
/// <param name="LexicalRank">1-based rank in the lexical channel, 0 when absent.</param>
/// <param name="VectorRank">1-based rank in the vector channel, 0 when absent.</param>
/// <param name="LexicalTerm"><c>1 / (k + lexical rank)</c>, or 0.</param>
/// <param name="VectorTerm"><c>1 / (k + vector rank)</c>, or 0.</param>
/// <param name="Fused">The reciprocal-rank-fusion score: the two terms summed.</param>
/// <param name="Weight">Table 13's multiplier for <paramref name="Level"/>.</param>
/// <param name="Score"><c>Fused × Weight</c>: what orders the page.</param>
/// <param name="Deferred">Whether diversification (R10.6, R10.7) moved this post to a later window of the page size than the one it ranked in (R9.68): by position, not by mechanism.</param>
/// <param name="MatchedBy">Which channel or channels chose this post (R9.67), read from the ranks fusion holds; null where the query had no term, set by the search and never by <c>Rank</c>.</param>
public sealed record RankedPost(
    SearchablePost Post,
    VerificationLevel Level,
    int LexicalRank,
    RankExplanation? Lexical,
    int VectorRank,
    double? Cosine,
    double LexicalTerm,
    double VectorTerm,
    double Fused,
    double Weight,
    double Score,
    bool Deferred,
    ChannelMark? MatchedBy = null);

/// <summary>R9.67: which channel chose a result, from the ranks fusion holds and never from a score.</summary>
public enum ChannelMark
{
    /// <summary>The keyword channel alone ranked it.</summary>
    Words,

    /// <summary>The vector channel alone ranked it.</summary>
    Meaning,

    /// <summary>Both channels ranked it.</summary>
    WordsAndMeaning,
}

/// <summary>
/// §9.2's pipeline after the two retrievers: reciprocal rank fusion over equal-depth candidate
/// lists (R9.4), Table 13's verification weight, the floor's admission (R10.2), and
/// diversification (R10.6, R10.7). Pure; the page is cut afterwards.
/// </summary>
public static class HybridRanking
{
    /// <summary>
    /// How deep each channel's list goes into fusion. Equal and published: RRF's contribution is
    /// purely positional, so an asymmetric depth biases fusion toward the deeper list for free.
    /// </summary>
    public const int CandidateDepth = 200;

    /// <summary>
    /// The largest share of one page a single author may hold before further posts of theirs are
    /// deferred behind others (R10.7). Provisional; errata G10 records it as the number to measure.
    /// </summary>
    public const double MaximumAuthorShare = 0.5;

    public static ImmutableArray<RankedPost> Rank(
        ImmutableArray<SearchHit> lexical,
        ImmutableArray<VectorHit> vector,
        Func<SearchablePost, VerificationLevel> levelOf,
        int k = RankFusion.DefaultK,
        int depth = CandidateDepth)
    {
        ArgumentNullException.ThrowIfNull(levelOf);
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);

        var lexicalTop = lexical.Take(depth).ToList();
        var vectorTop = vector.Take(depth).ToList();

        var posts = new Dictionary<string, SearchablePost>(StringComparer.Ordinal);
        var explanations = new Dictionary<string, RankExplanation>(StringComparer.Ordinal);
        var cosines = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var hit in lexicalTop) { posts[hit.Post.PostId] = hit.Post; explanations[hit.Post.PostId] = hit.Why; }
        foreach (var hit in vectorTop) { posts.TryAdd(hit.Post.PostId, hit.Post); cosines[hit.Post.PostId] = hit.Cosine; }

        var fused = RankFusion.Fuse(
            [lexicalTop.Select(h => h.Post.PostId).ToList(), vectorTop.Select(h => h.Post.PostId).ToList()], k);

        var ranked = new List<RankedPost>(fused.Length);
        foreach (var candidate in fused)
        {
            var post = posts[candidate.Id];
            var level = RetrievalFloorPolicy.AppliesTo(post.Kind) ? levelOf(post) : VerificationLevel.V0;
            var weight = RetrievalFloorPolicy.Weight(level);
            var lexicalRank = candidate.Ranks[0];
            var vectorRank = candidate.Ranks[1];
            var lexicalTerm = lexicalRank > 0 ? 1.0 / (k + lexicalRank) : 0.0;
            var vectorTerm = vectorRank > 0 ? 1.0 / (k + vectorRank) : 0.0;

            ranked.Add(new RankedPost(
                post,
                level,
                lexicalRank,
                explanations.TryGetValue(candidate.Id, out var why) ? why : null,
                vectorRank,
                cosines.TryGetValue(candidate.Id, out var cosine) ? cosine : null,
                lexicalTerm,
                vectorTerm,
                candidate.Score,
                weight,
                candidate.Score * weight,
                Deferred: false));
        }

        // Weighted score, then the fused score (so a weight of 1.0 leaves fusion's own order), then
        // seq: immutable once assigned, which is what R9.7's stability needs.
        ranked.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;
            var byFused = b.Fused.CompareTo(a.Fused);
            return byFused != 0 ? byFused : a.Post.Sequence.CompareTo(b.Post.Sequence);
        });

        return [.. ranked];
    }

    /// <summary>R10.2's admission: gradable kinds below the floor leave the list; every other kind stays.</summary>
    public static ImmutableArray<RankedPost> Admit(ImmutableArray<RankedPost> ranked, VerificationLevel floor) =>
        [.. ranked.Where(r => RetrievalFloorPolicy.Serves(floor, r.Post.Kind, r.Level))];

    /// <summary>
    /// R10.7, R10.6 and R9.68, window by window. Each window of <paramref name="pageSize"/> posts takes, in rank order, the remaining posts that pass the caps: the author
    /// and the author's attested owner each under <see cref="MaximumAuthorShare"/> of the window, and no possible duplicate of a post already placed in the window (or that post
    /// of it). The caps' state restarts with every window. A post that fails stays remaining and is offered again to the next window, so it moves to the next window in which
    /// it passes and never to the end of the list. A window the passing posts cannot fill is topped up with the earliest-ranked remaining posts, so every window but the last
    /// is <paramref name="pageSize"/> long and nothing is dropped. A retrieval that hands k passages from one source to a reader has handed one actor the reader's whole
    /// context, which is the precondition reader-side defenses need not to hold.
    /// </summary>
    public static ImmutableArray<RankedPost> Diversify(ImmutableArray<RankedPost> ranked, int pageSize, double maximumAuthorShare = MaximumAuthorShare)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAuthorShare, 0.0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumAuthorShare, 1.0);

        var cap = Math.Max(1, (int)Math.Floor(pageSize * maximumAuthorShare));
        var remaining = ranked.Select((post, position) => (Post: post, Position: position)).ToList();
        var output = new List<RankedPost>(ranked.Length);

        while (remaining.Count > 0)
        {
            var windowIndex = output.Count / pageSize;
            var window = new List<(RankedPost Post, int Position)>(pageSize);
            var taken = new HashSet<int>();

            var authorsInWindow = new Dictionary<string, int>(StringComparer.Ordinal);
            var ownersInWindow = new Dictionary<string, int>(StringComparer.Ordinal);
            var digestsInWindow = new HashSet<string>(StringComparer.Ordinal);
            var duplicatesOfInWindow = new HashSet<string>(StringComparer.Ordinal);

            for (var i = 0; i < remaining.Count && window.Count < pageSize; i++)
            {
                var candidate = remaining[i].Post;
                var author = candidate.Post.Author;

                // An unattested author is its own owner. Grouping every ownerless post under one key
                // would defer unrelated authors as though they had colluded, which is wider than the
                // evidence; this way the owner cap is never weaker than the author cap and never
                // claims a relationship the log does not record.
                var owner = candidate.Post.Owner ?? author;
                var digest = candidate.Post.Digest;
                var duplicateOf = candidate.Post.PossibleDuplicateOf;

                var overAuthorShare = authorsInWindow.TryGetValue(author, out var count) && count >= cap;
                var overOwnerShare = ownersInWindow.TryGetValue(owner, out var ownerCount) && ownerCount >= cap;
                var duplicatesPlaced = (duplicateOf is not null && digestsInWindow.Contains(duplicateOf)) || duplicatesOfInWindow.Contains(digest);
                if (overAuthorShare || overOwnerShare || duplicatesPlaced) continue;

                window.Add(remaining[i]);
                taken.Add(i);
                authorsInWindow[author] = count + 1;
                ownersInWindow[owner] = ownerCount + 1;
                digestsInWindow.Add(digest);
                if (duplicateOf is not null) duplicatesOfInWindow.Add(duplicateOf);
            }

            // Top up: a window the passing posts cannot fill takes the earliest-ranked of what remains.
            for (var i = 0; i < remaining.Count && window.Count < pageSize; i++)
            {
                if (taken.Add(i)) window.Add(remaining[i]);
            }

            // R9.68: deferred is by position -- the post's window in the output is later than its window in the input.
            foreach (var (post, position) in window)
                output.Add(post with { Deferred = windowIndex > position / pageSize });

            remaining = [.. remaining.Where((_, i) => !taken.Contains(i))];
        }

        return [.. output];
    }
}
