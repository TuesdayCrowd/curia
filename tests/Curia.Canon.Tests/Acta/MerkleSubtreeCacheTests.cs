using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Canon.Acta;
using CsCheck;
using Xunit;

namespace Curia.Canon.Tests.Acta;

/// <summary>
/// Register item 403: a board listing builds one inclusion proof per post, and each proof rehashed about 2N nodes, so the listing was O(N^2). <see cref="MerkleSubtreeCache"/> computes
/// each subtree hash once for one leaf list. The two probes: the cache is equal to <see cref="MerkleTree"/> on the slice it replaces (the oracle), and the number of node hashes it
/// computes for k proofs grows with N and not with k times N.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class MerkleSubtreeCacheTests
{
    private static ImmutableArray<ImmutableArray<byte>> Leaves(int count) =>
        [.. Enumerable.Range(0, count).Select(i => MerkleTree.LeafHash(Encoding.UTF8.GetBytes("leaf-" + i)))];

    /// <summary>
    /// MSC1, the equivalence oracle. For a random leaf count, every tree size up to it and every index of that tree, the cache's path and root equal
    /// <see cref="MerkleTree.InclusionPath"/> and <see cref="MerkleTree.Root"/> on the slice, and the cache is asked in a random order so that a memo from one size is read by another.
    /// </summary>
    [Fact]
    public void Item403_MSC1_TheCachedPathAndRootEqualTheTreesOnTheSlice()
    {
        Gen.Int[1, 70].Sample(count =>
        {
            var all = Leaves(count);
            var cache = new MerkleSubtreeCache(all);
            var queries = (from size in Enumerable.Range(1, count) from index in Enumerable.Range(0, size) select (size, index))
                .OrderBy(q => (q.size * 7919 + q.index * 104729 + count) % 1009)
                .ToArray();
            foreach (var (size, index) in queries)
            {
                var slice = all[..size];
                Assert.True(
                    MerkleTree.InclusionPath(slice, index).Zip(cache.InclusionPath(size, index)).All(p => p.First.SequenceEqual(p.Second))
                    && MerkleTree.InclusionPath(slice, index).Length == cache.InclusionPath(size, index).Length,
                    $"path differs: count {count}, size {size}, index {index}");
                Assert.True(MerkleTree.Root(slice).SequenceEqual(cache.Root(size)), $"root differs: count {count}, size {size}");
            }
        }, iter: 40);
    }

    [Fact]
    public void Item403_MSC1_TheEmptyTreeHasTheEmptyRoot() =>
        Assert.True(MerkleTree.EmptyRoot().SequenceEqual(new MerkleSubtreeCache([]).Root(0)));

    private static int HashesForEveryProof(int n)
    {
        var cache = new MerkleSubtreeCache(Leaves(n));
        _ = cache.Root(n);
        for (var i = 0; i < n; i++) _ = cache.InclusionPath(n, i);
        return cache.NodeHashes;
    }

    /// <summary>
    /// MSC2, the complexity probe. A proof for every leaf of N leaves, then of 4N: linear growth is a ratio of 4 and quadratic growth is 16. The threshold, stated before the first run, is 8:
    /// the cost of k = N proofs is under 8 times when N is quadrupled, and is at most 2N node hashes at each size (the unmemoized path costs about 2N per proof).
    /// </summary>
    [Fact]
    public void Item403_MSC2_AProofForEveryLeafCostsNodeHashesLinearInTheLeaves()
    {
        var small = HashesForEveryProof(250);
        var large = HashesForEveryProof(1000);

        Assert.True(small <= 2 * 250, $"N=250 took {small} node hashes");
        Assert.True(large <= 2 * 1000, $"N=1000 took {large} node hashes");
        Assert.True(large < 8 * small, $"N to 4N took {small} then {large} node hashes: a ratio of {(double)large / small:F1}, quadratic is about 16, linear about 4, the threshold 8");
    }
}
