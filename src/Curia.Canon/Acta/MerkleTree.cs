using System.Collections.Immutable;
using System.Security.Cryptography;

namespace Curia.Canon.Acta;

/// <summary>
/// RFC 9162 §2.1: the Merkle Tree Hash over an ordered list of leaf inputs, with audit paths
/// (§2.1.3) and consistency proofs (§2.1.4), and the verification procedures for both (§2.1.3.2,
/// §2.1.4.2). This is the Acta's tree (§6.6, R6.23); the leaf <i>inputs</i> are the log's business,
/// and R15.1 freezes how they are computed -- this type only hashes what it is handed.
///
/// <para><b>Domain prefixes.</b> A leaf is <c>SHA-256(0x00 ‖ input)</c> and a node is
/// <c>SHA-256(0x01 ‖ left ‖ right)</c>, so a leaf can never be presented as an interior node and
/// a second-preimage across levels is not available. The empty tree hashes to <c>SHA-256()</c>.
/// The split point at every level is the largest power of two strictly less than the size, which
/// is what makes a consistency proof between any two sizes exist.</para>
///
/// <para><b>Pure and BCL-only</b> (R11.1): built from an immutable list of leaf hashes on demand,
/// O(n) hashes per build and O(log n) per proof. There is no incremental state here on purpose --
/// the read paths already fold the whole log per request, and a cache that could disagree with a
/// rebuild is exactly what R11.9's replay drill exists to forbid. The size at which that stops
/// being acceptable is stated in the plan, not hidden here.</para>
/// </summary>
public static class MerkleTree
{
    public const int HashLength = 32;

    private static readonly byte[] LeafPrefix = [0x00];
    private static readonly byte[] NodePrefix = [0x01];

    /// <summary><c>SHA-256(0x00 ‖ input)</c>.</summary>
    public static ImmutableArray<byte> LeafHash(ReadOnlySpan<byte> input)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(LeafPrefix);
        sha.AppendData(input);
        return [.. sha.GetHashAndReset()];
    }

    /// <summary><c>SHA-256(0x01 ‖ left ‖ right)</c>.</summary>
    public static ImmutableArray<byte> NodeHash(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(NodePrefix);
        sha.AppendData(left);
        sha.AppendData(right);
        return [.. sha.GetHashAndReset()];
    }

    /// <summary>The empty tree's hash, <c>SHA-256()</c> (§2.1.1).</summary>
    public static ImmutableArray<byte> EmptyRoot() => [.. SHA256.HashData(ReadOnlySpan<byte>.Empty)];

    /// <summary>MTH(D[n]) over already-hashed leaves (§2.1.1).</summary>
    public static ImmutableArray<byte> Root(ImmutableArray<ImmutableArray<byte>> leafHashes) =>
        leafHashes.Length == 0 ? EmptyRoot() : Subtree(leafHashes, 0, leafHashes.Length);

    /// <summary>PATH(m, D[n]) (§2.1.3.1): the audit path for leaf <paramref name="index"/> in a tree of <paramref name="leafHashes"/>.</summary>
    public static ImmutableArray<ImmutableArray<byte>> InclusionPath(ImmutableArray<ImmutableArray<byte>> leafHashes, int index)
    {
        if (index < 0 || index >= leafHashes.Length)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Not a leaf of this tree");

        var path = ImmutableArray.CreateBuilder<ImmutableArray<byte>>();
        Path(leafHashes, 0, leafHashes.Length, index, path);
        return path.ToImmutable();
    }

    /// <summary>PROOF(m, D[n]) (§2.1.4.1): the consistency proof from the tree's first <paramref name="fromSize"/> leaves to all of it.</summary>
    public static ImmutableArray<ImmutableArray<byte>> ConsistencyPath(ImmutableArray<ImmutableArray<byte>> leafHashes, int fromSize)
    {
        if (fromSize < 1 || fromSize > leafHashes.Length)
            throw new ArgumentOutOfRangeException(nameof(fromSize), fromSize, "A consistency proof runs from a size in 1..n");

        if (fromSize == leafHashes.Length) return [];

        var path = ImmutableArray.CreateBuilder<ImmutableArray<byte>>();
        SubProof(leafHashes, 0, leafHashes.Length, fromSize, startFromComplete: true, path);
        return path.ToImmutable();
    }

    /// <summary>
    /// §2.1.3.2: whether <paramref name="path"/> proves that the leaf whose hash is
    /// <paramref name="leafHash"/> sits at <paramref name="index"/> in a tree of
    /// <paramref name="treeSize"/> leaves whose root is <paramref name="root"/>.
    /// </summary>
    public static bool VerifyInclusion(
        ReadOnlySpan<byte> leafHash, long index, long treeSize, ImmutableArray<ImmutableArray<byte>> path, ReadOnlySpan<byte> root)
    {
        if (index < 0 || treeSize <= 0 || index >= treeSize) return false;

        var fn = index;
        var sn = treeSize - 1;
        var r = leafHash.ToArray();

        foreach (var p in path)
        {
            if (sn == 0) return false;

            if ((fn & 1) == 1 || fn == sn)
            {
                r = [.. NodeHash(p.AsSpan(), r)];
                if ((fn & 1) == 0)
                {
                    while ((fn & 1) == 0 && fn != 0)
                    {
                        fn >>= 1;
                        sn >>= 1;
                    }
                }
            }
            else
            {
                r = [.. NodeHash(r, p.AsSpan())];
            }

            fn >>= 1;
            sn >>= 1;
        }

        return sn == 0 && root.SequenceEqual(r);
    }

    /// <summary>
    /// §2.1.4.2: whether <paramref name="path"/> proves that the tree of <paramref name="secondSize"/>
    /// leaves with root <paramref name="secondRoot"/> is an append-only extension of the tree of
    /// <paramref name="firstSize"/> leaves with root <paramref name="firstRoot"/>. Equal sizes with
    /// an empty path verify exactly when the roots agree (k = 0).
    /// </summary>
    public static bool VerifyConsistency(
        long firstSize, long secondSize, ReadOnlySpan<byte> firstRoot, ReadOnlySpan<byte> secondRoot,
        ImmutableArray<ImmutableArray<byte>> path)
    {
        if (firstSize <= 0 || secondSize < firstSize) return false;

        if (firstSize == secondSize)
            return path.IsEmpty && firstRoot.SequenceEqual(secondRoot);

        if (path.IsEmpty) return false;

        // §2.1.4.2 step 3: when the first size is a power of two, the first root is the first
        // proof node; the prover omits it because the verifier already holds it.
        var nodes = (firstSize & (firstSize - 1)) == 0
            ? path.Insert(0, [.. firstRoot])
            : path;

        var fn = firstSize - 1;
        var sn = secondSize - 1;

        while ((fn & 1) == 1)
        {
            fn >>= 1;
            sn >>= 1;
        }

        var fr = nodes[0].ToArray();
        var sr = nodes[0].ToArray();

        foreach (var p in nodes.Skip(1))
        {
            if (sn == 0) return false;

            if ((fn & 1) == 1 || fn == sn)
            {
                fr = [.. NodeHash(p.AsSpan(), fr)];
                sr = [.. NodeHash(p.AsSpan(), sr)];
                if ((fn & 1) == 0)
                {
                    while ((fn & 1) == 0 && fn != 0)
                    {
                        fn >>= 1;
                        sn >>= 1;
                    }
                }
            }
            else
            {
                sr = [.. NodeHash(sr, p.AsSpan())];
            }

            fn >>= 1;
            sn >>= 1;
        }

        return sn == 0 && firstRoot.SequenceEqual(fr) && secondRoot.SequenceEqual(sr);
    }

    /// <summary>The largest power of two strictly less than <paramref name="n"/>, for <paramref name="n"/> &gt; 1 (§2.1.1's k).</summary>
    public static int SplitPoint(int n)
    {
        if (n < 2) throw new ArgumentOutOfRangeException(nameof(n), n, "k is defined for n > 1");
        var k = 1;
        while (k * 2 < n) k *= 2;
        return k;
    }

    private static ImmutableArray<byte> Subtree(ImmutableArray<ImmutableArray<byte>> leaves, int start, int end)
    {
        var n = end - start;
        if (n == 1) return leaves[start];
        var k = SplitPoint(n);
        return NodeHash(Subtree(leaves, start, start + k).AsSpan(), Subtree(leaves, start + k, end).AsSpan());
    }

    private static void Path(
        ImmutableArray<ImmutableArray<byte>> leaves, int start, int end, int m, ImmutableArray<ImmutableArray<byte>>.Builder path)
    {
        var n = end - start;
        if (n == 1) return;
        var k = SplitPoint(n);
        if (m < k)
        {
            Path(leaves, start, start + k, m, path);
            path.Add(Subtree(leaves, start + k, end));
        }
        else
        {
            Path(leaves, start + k, end, m - k, path);
            path.Add(Subtree(leaves, start, start + k));
        }
    }

    private static void SubProof(
        ImmutableArray<ImmutableArray<byte>> leaves, int start, int end, int m, bool startFromComplete,
        ImmutableArray<ImmutableArray<byte>>.Builder path)
    {
        var n = end - start;
        if (m == n)
        {
            if (!startFromComplete) path.Add(Subtree(leaves, start, end));
            return;
        }

        var k = SplitPoint(n);
        if (m <= k)
        {
            SubProof(leaves, start, start + k, m, startFromComplete, path);
            path.Add(Subtree(leaves, start + k, end));
        }
        else
        {
            SubProof(leaves, start + k, end, m - k, startFromComplete: false, path);
            path.Add(Subtree(leaves, start, start + k));
        }
    }
}
