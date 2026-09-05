using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Domain.Tests.Search;

/// <summary>R9.4's fusion against hand-computed rankings, k = 60 unless stated.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RankFusionTests
{
    private const double Tolerance = 1e-12;

    private static ImmutableArray<FusedCandidate> Fuse(params string[][] rankings) =>
        RankFusion.Fuse([.. rankings.Select(r => (IReadOnlyList<string>)r)]);

    [Fact]
    public void R9_4_AgreementBetweenChannelsOutranksASingleTopHit()
    {
        // x tops the lexical list and is absent from the vector list; y and z appear in both.
        //   x: 1/61                  = 0.0163934...
        //   y: 1/62 + 1/62           = 0.0322580...
        //   z: 1/63 + 1/61           = 0.0322665...
        var fused = Fuse(["x", "y", "z"], ["z", "y"]);

        Assert.Equal(["z", "y", "x"], fused.Select(c => c.Id));
        Assert.Equal(1.0 / 63 + 1.0 / 61, fused[0].Score, Tolerance);
        Assert.Equal(2.0 / 62, fused[1].Score, Tolerance);
        Assert.Equal(1.0 / 61, fused[2].Score, Tolerance);
        Assert.Equal([3, 1], fused[0].Ranks);
        Assert.Equal([1, 0], fused[2].Ranks);
    }

    /// <summary>The plan's case: the two rankings disagree completely.</summary>
    [Fact]
    public void R9_4_WhenTheChannelsDisagreeCompletelyTheEndsTieAboveTheMiddle()
    {
        //   a: 1/61 + 1/63,  b: 1/62 + 1/62,  c: 1/63 + 1/61  -- a and c tie, and both beat b.
        var fused = Fuse(["a", "b", "c"], ["c", "b", "a"]);

        Assert.Equal(["a", "c", "b"], fused.Select(c => c.Id));
        Assert.Equal(fused[0].Score, fused[1].Score, Tolerance);
        Assert.True(fused[1].Score > fused[2].Score);
    }

    [Fact]
    public void R9_4_ASingleRankingIsReturnedInItsOwnOrder()
    {
        var fused = Fuse(["p", "q", "r"]);
        Assert.Equal(["p", "q", "r"], fused.Select(c => c.Id));
        Assert.Equal([1], fused[0].Ranks);
    }

    [Fact]
    public void R9_4_ARepeatedIdKeepsItsFirstPosition()
    {
        var fused = Fuse(["p", "p", "q"], ["q"]);
        Assert.Equal([1, 0], fused.Single(c => c.Id == "p").Ranks);
        Assert.Equal(1.0 / 61, fused.Single(c => c.Id == "p").Score, Tolerance);
    }

    [Fact]
    public void R9_4_TiesBreakByBestRankThenById()
    {
        // m and n both score 1/61: m from list one, n from list two. Same best rank, so by id.
        var fused = Fuse(["n"], ["m"]);
        Assert.Equal(["m", "n"], fused.Select(c => c.Id));
    }

    [Fact]
    public void R9_4_KIsSmoothingAndMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => RankFusion.Fuse([(IReadOnlyList<string>)["a"]], 0));
        Assert.Equal(1.0 / 2, RankFusion.Fuse([(IReadOnlyList<string>)["a"]], 1)[0].Score, Tolerance);
        Assert.Empty(RankFusion.Fuse([]));
        Assert.Empty(Fuse([], []));
    }
}
