using System.Diagnostics.CodeAnalysis;
using Curia.Application.Projections;
using Curia.Canon.Acta;
using Xunit;
using static Curia.Application.Tests.Acta.LogBuilder;

namespace Curia.Application.Tests.Acta;

/// <summary>
/// Register item 403: the wiring half of MSC2. <see cref="ActaLog.Inclusion"/> must take its hashes from the log's one cache, so a proof for every leaf of the log costs node hashes linear
/// in the log (threshold 8 for a quadrupled log, stated before the first run; linear is 4, quadratic 16), and a proof is equal to the tree's own on the slice.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class InclusionProofCostTests
{
    private static ActaLog FoldOf(int n) =>
        ActaLog.Fold(LogOf([.. Enumerable.Range(0, n).Select(i => Post("post-" + i))])).Match(a => a, e => throw new InvalidOperationException(e.Type));

    private static int HashesForEveryProof(int n)
    {
        var acta = FoldOf(n);
        for (var i = 0; i < n; i++) Assert.NotNull(acta.Inclusion(i, n));
        return acta.SubtreeHashesComputed;
    }

    [Fact]
    public void Item403_MSC3_AProofForEveryEntryOfTheLogCostsNodeHashesLinearInTheLog()
    {
        var small = HashesForEveryProof(100);
        var large = HashesForEveryProof(400);

        Assert.True(large < 8 * small, $"N to 4N took {small} then {large} node hashes: a ratio of {(double)large / small:F1}");
    }

    [Fact]
    public void Item403_MSC3_AProofAtAnyTreeSizeEqualsTheTreesOwnOnTheSlice()
    {
        var acta = FoldOf(37);
        foreach (var size in new[] { 1, 2, 5, 16, 36, 37 })
            for (var index = 0; index < size; index++)
            {
                var proof = acta.Inclusion(index, size)!;
                var slice = acta.Leaves[..size];
                Assert.Equal(Convert.ToHexString([.. MerkleTree.Root(slice)]), Convert.ToHexString([.. proof.Root]));
                Assert.Equal(
                    MerkleTree.InclusionPath(slice, index).Select(h => Convert.ToHexString([.. h])),
                    proof.AuditPath.Select(h => Convert.ToHexString([.. h])));
            }
    }
}
