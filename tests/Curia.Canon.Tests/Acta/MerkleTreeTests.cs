using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Acta;
using Curia.Canon.Tests.Vectors;
using Xunit;

namespace Curia.Canon.Tests.Acta;

/// <summary>
/// RFC 9162 §2.1 against the <c>conformance/merkle/</c> family: the Certificate Transparency
/// reference leaves, hashed by an oracle written from the RFC's recursive definitions and agreeing
/// with the reference implementation's published vectors. Powers of two and the sizes either side
/// of them are where Merkle implementations break, so every size from 0 to 8 is there, every audit
/// path, and every consistency path -- including k = 0. The negative cases below are this suite's
/// own: the corpus pins what must succeed, and these pin what must not.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class MerkleTreeTests
{
    private static readonly IReadOnlyList<MerkleVector> Vectors = MerkleVectorLoader.Load();

    private static MerkleVector Vector(int size) => Vectors.Single(v => v.Size == size);

    private static ImmutableArray<ImmutableArray<byte>> LeafHashes(MerkleVector v, int? take = null) =>
        [.. v.Leaves.Take(take ?? v.Size).Select(l => MerkleTree.LeafHash(l.AsSpan()))];

    private static string Hex(ImmutableArray<byte> bytes) => Convert.ToHexStringLower([.. bytes]);

    /// <summary>
    /// The runner's half of R6.45: the family is not directory-shaped, so
    /// <see cref="ConformanceIndexTests"/> cannot see whether anything here loads it. This does.
    /// </summary>
    [Fact]
    public void R6_45_ThisRunnerLoadsEveryMerkleVectorTheIndexDeclares()
    {
        Assert.Equal(MerkleVectorLoader.DeclaredCount(), Vectors.Count);
        Assert.Equal(Enumerable.Range(0, Vectors.Count), Vectors.Select(v => v.Size).Order());
        Assert.All(Vectors, v => Assert.False(string.IsNullOrWhiteSpace(v.Requirement), $"{v.Name} cites no requirement"));
    }

    [Fact]
    public void RFC9162_LeafHashesArePrefixedWithZero()
    {
        var v = Vector(8);
        for (var i = 0; i < v.Size; i++)
            Assert.Equal(v.LeafHashes[i], Hex(MerkleTree.LeafHash(v.Leaves[i].AsSpan())));
    }

    /// <summary>The plan's list -- 0, 1, 2, 3, 7, 8 -- and everything between.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void RFC9162_TheRootMatchesTheReference(int size)
    {
        var v = Vector(size);
        Assert.Equal(v.Root, Hex(MerkleTree.Root(LeafHashes(v))));
    }

    [Fact]
    public void RFC9162_TheEmptyTreeIsTheHashOfNothing()
    {
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", Hex(MerkleTree.EmptyRoot()));
        Assert.Equal(Vector(0).Root, Hex(MerkleTree.EmptyRoot()));
    }

    /// <summary>Every audit path for every size up to 8 is node-for-node the reference's, and verifies against that size's root.</summary>
    [Fact]
    public void R6_23_EveryAuditPathMatchesTheReferenceAndVerifies()
    {
        var cases = Vectors.SelectMany(v => v.Inclusion.Select(c => (v, c))).ToList();
        Assert.Equal(36, cases.Count);
        foreach (var (v, c) in cases)
        {
            var leaves = LeafHashes(v);
            var path = MerkleTree.InclusionPath(leaves, c.Index);

            Assert.Equal(c.Path, path.Select(Hex));
            Assert.True(
                MerkleTree.VerifyInclusion(leaves[c.Index].AsSpan(), c.Index, v.Size, path, MerkleTree.Root(leaves).AsSpan()),
                $"inclusion of leaf {c.Index} in a tree of {v.Size} did not verify");
        }
    }

    /// <summary>Every consistency path between every pair of sizes up to 8, including k = 0, matches and verifies.</summary>
    [Fact]
    public void R6_23_EveryConsistencyPathMatchesTheReferenceAndVerifies()
    {
        var cases = Vectors.SelectMany(v => v.Consistency.Select(c => (v, c))).ToList();
        Assert.Equal(36, cases.Count);
        foreach (var (v, c) in cases)
        {
            var path = MerkleTree.ConsistencyPath(LeafHashes(v), c.From);

            Assert.Equal(c.Path, path.Select(Hex));
            Assert.True(
                MerkleTree.VerifyConsistency(
                    c.From, v.Size, MerkleTree.Root(LeafHashes(v, c.From)).AsSpan(), MerkleTree.Root(LeafHashes(v)).AsSpan(), path),
                $"consistency {c.From} -> {v.Size} did not verify");
        }
    }

    /// <summary>The point of the structure, half one: a leaf changed by one byte fails its inclusion proof.</summary>
    [Fact]
    public void P7_ATamperedLeafFailsItsInclusionProof()
    {
        var leaves = LeafHashes(Vector(8));
        var path = MerkleTree.InclusionPath(leaves, 5);
        var root = MerkleTree.Root(leaves);

        var tampered = leaves[5].ToArray();
        tampered[0] ^= 0x01;

        Assert.True(MerkleTree.VerifyInclusion(leaves[5].AsSpan(), 5, 8, path, root.AsSpan()));
        Assert.False(MerkleTree.VerifyInclusion(tampered, 5, 8, path, root.AsSpan()));
        Assert.False(MerkleTree.VerifyInclusion(leaves[5].AsSpan(), 4, 8, path, root.AsSpan()));
    }

    /// <summary>
    /// Half two: a log that dropped or rewrote an entry is not an append-only extension of an
    /// earlier head, and the consistency proof says so.
    /// </summary>
    [Fact]
    public void P7_ATruncatedOrRewrittenLogFailsConsistency()
    {
        var honest = LeafHashes(Vector(7));
        var earlierRoot = MerkleTree.Root(LeafHashes(Vector(3)));

        // Rewritten: the same size, with leaf 1 replaced after the earlier head was published.
        var rewritten = honest.SetItem(1, MerkleTree.LeafHash("not what was logged"u8));
        var rewrittenPath = MerkleTree.ConsistencyPath(rewritten, 3);
        Assert.False(MerkleTree.VerifyConsistency(3, 7, earlierRoot.AsSpan(), MerkleTree.Root(rewritten).AsSpan(), rewrittenPath));

        // Truncated: the later "head" is a tree that dropped the entries the earlier head covered.
        var truncated = ImmutableArray.CreateRange(honest.Skip(2));
        var truncatedPath = MerkleTree.ConsistencyPath(truncated, 3);
        Assert.False(MerkleTree.VerifyConsistency(3, 5, earlierRoot.AsSpan(), MerkleTree.Root(truncated).AsSpan(), truncatedPath));

        // Control: the honest extension verifies with the same earlier head.
        Assert.True(MerkleTree.VerifyConsistency(3, 7, earlierRoot.AsSpan(), MerkleTree.Root(honest).AsSpan(), MerkleTree.ConsistencyPath(honest, 3)));
    }

    /// <summary>A proof between unrelated heads does not verify, whatever path is offered.</summary>
    [Fact]
    public void P7_UnrelatedHeadsDoNotVerify()
    {
        var a = LeafHashes(Vector(8));
        var b = ImmutableArray.CreateRange(Enumerable.Range(0, 8).Select(i => MerkleTree.LeafHash([(byte)(0x80 + i)])));
        var aFour = MerkleTree.Root(LeafHashes(Vector(4)));

        Assert.False(MerkleTree.VerifyConsistency(4, 8, aFour.AsSpan(), MerkleTree.Root(b).AsSpan(), MerkleTree.ConsistencyPath(a, 4)));
        Assert.False(MerkleTree.VerifyConsistency(4, 8, aFour.AsSpan(), MerkleTree.Root(b).AsSpan(), MerkleTree.ConsistencyPath(b, 4)));
        Assert.False(MerkleTree.VerifyConsistency(8, 8, MerkleTree.Root(a).AsSpan(), MerkleTree.Root(b).AsSpan(), []));
    }

    [Fact]
    public void RFC9162_TheSplitPointIsTheLargestPowerOfTwoBelowTheSize()
    {
        Assert.Equal(1, MerkleTree.SplitPoint(2));
        Assert.Equal(2, MerkleTree.SplitPoint(3));
        Assert.Equal(2, MerkleTree.SplitPoint(4));
        Assert.Equal(4, MerkleTree.SplitPoint(5));
        Assert.Equal(4, MerkleTree.SplitPoint(8));
        Assert.Equal(8, MerkleTree.SplitPoint(9));
    }
}
