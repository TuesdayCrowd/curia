using System.Collections.Immutable;

namespace Curia.Domain.Search;

/// <summary>
/// One document after fusion: its fused score and its 1-based rank in each input ranking, with
/// <c>0</c> where a ranking did not contain it. The ranks are what R9.8's <c>why_ranked</c>
/// reports; the score is what orders the page.
/// </summary>
public sealed record FusedCandidate(string Id, double Score, ImmutableArray<int> Ranks);

/// <summary>
/// Reciprocal Rank Fusion (§9.2, R9.4): <c>RRF(d) = Σ_r 1 / (k + rank_r(d))</c> over every ranking
/// that contains <i>d</i>, with <i>k</i> ≈ 60.
///
/// <para>Rank-based, not score-based, for the reason R9.4 gives: lexical scores and vector
/// distances live on incomparable scales, and any normalization that made them comparable would
/// be fragile across corpus changes. A rank is a rank. R10.1 adds the second reason: the fusion
/// is what keeps the lexical channel load-bearing, so a document optimized against the embedding
/// geometry alone cannot take the top of the page on its own.</para>
///
/// <para>Pure and deterministic: ties are broken by the best rank any list gave the document,
/// then by ordinal id, so the same inputs always fuse to the same order. A caller that wants a
/// corpus-stable tiebreak (R9.7) applies its own sequence afterwards; this function has no
/// opinion about what an id is.</para>
/// </summary>
public static class RankFusion
{
    /// <summary>§9.2's <i>k</i>. Large enough that a document's second appearance is worth about as much as its first, which is the property that rewards agreement between channels.</summary>
    public const int DefaultK = 60;

    /// <param name="rankings">Each ranking best-first. A document repeated within one ranking keeps its first position.</param>
    /// <param name="k">The smoothing constant; at least 1.</param>
    public static ImmutableArray<FusedCandidate> Fuse(IReadOnlyList<IReadOnlyList<string>> rankings, int k = DefaultK)
    {
        ArgumentNullException.ThrowIfNull(rankings);
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);

        var ranks = new Dictionary<string, int[]>(StringComparer.Ordinal);
        for (var r = 0; r < rankings.Count; r++)
        {
            var ranking = rankings[r];
            ArgumentNullException.ThrowIfNull(ranking);

            for (var i = 0; i < ranking.Count; i++)
            {
                var id = ranking[i];
                ArgumentNullException.ThrowIfNull(id);

                if (!ranks.TryGetValue(id, out var perList))
                {
                    perList = new int[rankings.Count];
                    ranks[id] = perList;
                }

                if (perList[r] == 0)
                    perList[r] = i + 1;
            }
        }

        var fused = new List<FusedCandidate>(ranks.Count);
        foreach (var (id, perList) in ranks)
        {
            var score = 0.0;
            foreach (var rank in perList)
                if (rank > 0)
                    score += 1.0 / (k + rank);

            fused.Add(new FusedCandidate(id, score, [.. perList]));
        }

        fused.Sort(static (a, b) =>
        {
            var byScore = b.Score.CompareTo(a.Score);
            if (byScore != 0) return byScore;

            var byBestRank = BestRank(a).CompareTo(BestRank(b));
            return byBestRank != 0 ? byBestRank : string.CompareOrdinal(a.Id, b.Id);
        });

        return [.. fused];
    }

    private static int BestRank(FusedCandidate c)
    {
        var best = int.MaxValue;
        foreach (var rank in c.Ranks)
            if (rank > 0 && rank < best)
                best = rank;
        return best;
    }
}
