using System.Collections.Immutable;
using Curia.Canon.Acta;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>A signed tree head as the log holds it (R6.49), with where in the log it sits.</summary>
/// <param name="LogIndex">The head event's own leaf index. Every head is itself an entry, so a later head commits to every earlier one.</param>
/// <param name="TreeSize">How many leaves the head covers; its own leaf is <see cref="LogIndex"/> = <see cref="TreeSize"/> or later.</param>
/// <param name="Root">The root over the first <see cref="TreeSize"/> leaves, as the signer stated it.</param>
/// <param name="Timestamp">The signer's clock, as it was signed -- carried as the string it was signed as.</param>
/// <param name="Kid">The log key's identifier.</param>
/// <param name="Signature">The detached JWS over <see cref="Document"/>'s canonical form.</param>
/// <param name="Document">The signed object exactly as stored, so a verifier canonicalizes what was signed and not a re-rendering.</param>
public sealed record SignedHead(
    long LogIndex,
    long TreeSize,
    ImmutableArray<byte> Root,
    string Timestamp,
    string Kid,
    string Signature,
    JsonValue.Object Document);

/// <summary>A log signing key as published to the log (R6.50). Never retired here: R12.16's history is append-only.</summary>
public sealed record LogKey(long LogIndex, string Kid, JsonValue.Object Jwk, string ValidFrom);

/// <summary>An audit path and everything a verifier needs to check it (R6.48).</summary>
public sealed record InclusionProof(
    long LogIndex,
    long TreeSize,
    ImmutableArray<byte> LeafHash,
    ImmutableArray<ImmutableArray<byte>> AuditPath,
    ImmutableArray<byte> Root);

/// <summary>A consistency proof between two sizes and the two roots it connects (R6.23).</summary>
public sealed record ConsistencyProof(
    long FromSize,
    long ToSize,
    ImmutableArray<byte> FromRoot,
    ImmutableArray<byte> ToRoot,
    ImmutableArray<ImmutableArray<byte>> Path);

/// <summary>
/// The Acta (<see cref="ActaLog"/>): the whole event log as a Merkle tree, folded per request like every other projection.
///
/// <para><b>Cost, stated (Stage 4's success criterion).</b> Append costs nothing here -- there is
/// no separate log to write, because the event append <i>is</i> the log append (R6.46) -- and so
/// it does not grow with log length. What grows is this fold: O(n) hashes per request that needs
/// the tree, on top of the O(n) read every projection already performs. The bound is the same one
/// every read path has: the whole log fits one read
/// (<see cref="Ports.EventReaderExtensions.ReadAllAsync"/>). When it no longer does, the fix is a
/// tree with cached subtree hashes, not a larger page -- a tree over a truncated leaf list yields
/// wrong roots with no error.</para>
///
/// <para><b>Order is commit order.</b> Leaf <i>i</i> is the <i>i</i>th event in ascending
/// <c>seq</c>, and R6.47 makes the store serialize appends so that order is also the order in
/// which events became visible; otherwise a tree folded during a race could change under a head
/// that had already been signed.</para>
/// </summary>
public sealed class ActaLog
{
    private ActaLog(
        IReadOnlyList<AppendedEvent> events,
        ImmutableArray<ImmutableArray<byte>> leaves,
        ImmutableArray<SignedHead> heads,
        ImmutableArray<LogKey> keys,
        ImmutableDictionary<string, long> indexByEventId)
    {
        Events = events;
        Leaves = leaves;
        Heads = heads;
        Keys = keys;
        IndexByEventId = indexByEventId;
        Root = MerkleTree.Root(leaves);
    }

    public IReadOnlyList<AppendedEvent> Events { get; }

    /// <summary>Leaf hashes in leaf order; index <i>i</i> is R6.47's <c>log_index</c> <i>i</i>.</summary>
    public ImmutableArray<ImmutableArray<byte>> Leaves { get; }

    public long TreeSize => Leaves.Length;

    /// <summary>The root over every leaf -- the head a signer would sign now.</summary>
    public ImmutableArray<byte> Root { get; }

    /// <summary>Every signed head in the log, in log order.</summary>
    public ImmutableArray<SignedHead> Heads { get; }

    /// <summary>The latest signed head, or <see langword="null"/> before the operator has signed one.</summary>
    public SignedHead? LatestHead => Heads.IsEmpty ? null : Heads[^1];

    /// <summary>Every log key ever published, in log order (R12.16: the full history, forever).</summary>
    public ImmutableArray<LogKey> Keys { get; }

    /// <summary>The leaf index of the event with a given <c>event_id</c>, for R6.18's per-item <c>log_index</c>.</summary>
    public ImmutableDictionary<string, long> IndexByEventId { get; }

    public long? IndexOf(string eventId) => IndexByEventId.TryGetValue(eventId, out var i) ? i : null;

    /// <summary>The root over the first <paramref name="treeSize"/> leaves.</summary>
    public ImmutableArray<byte> RootAt(long treeSize) =>
        treeSize == TreeSize ? Root : MerkleTree.Root(Leaves[..checked((int)treeSize)]);

    /// <summary>
    /// The audit path for leaf <paramref name="index"/> in the tree of the first
    /// <paramref name="treeSize"/> leaves. <see langword="null"/> when the index is not a leaf of
    /// that tree or the size exceeds the log.
    /// </summary>
    public InclusionProof? Inclusion(long index, long treeSize)
    {
        if (treeSize < 1 || treeSize > TreeSize || index < 0 || index >= treeSize)
            return null;

        var tree = Leaves[..checked((int)treeSize)];
        return new InclusionProof(
            index,
            treeSize,
            Leaves[checked((int)index)],
            MerkleTree.InclusionPath(tree, checked((int)index)),
            MerkleTree.Root(tree));
    }

    /// <summary>
    /// The consistency proof from the first <paramref name="fromSize"/> leaves to the first
    /// <paramref name="toSize"/>. <see langword="null"/> unless <c>1 ≤ from ≤ to ≤ tree size</c>.
    /// </summary>
    public ConsistencyProof? Consistency(long fromSize, long toSize)
    {
        if (fromSize < 1 || fromSize > toSize || toSize > TreeSize)
            return null;

        var to = Leaves[..checked((int)toSize)];
        return new ConsistencyProof(
            fromSize,
            toSize,
            MerkleTree.Root(to[..checked((int)fromSize)]),
            MerkleTree.Root(to),
            MerkleTree.ConsistencyPath(to, checked((int)fromSize)));
    }

    /// <summary>
    /// Folds the log. Fails, rather than skipping, on an entry whose leaf cannot be computed or a
    /// head or key entry the operator tool wrote malformed: an Acta with a hole in it would serve
    /// proofs that verify against a tree nobody else can rebuild.
    /// </summary>
    public static Result<ActaLog> Fold(IReadOnlyList<AppendedEvent> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        var leaves = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(log.Count);
        var heads = ImmutableArray.CreateBuilder<SignedHead>();
        var keys = ImmutableArray.CreateBuilder<LogKey>();
        var index = ImmutableDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);

        for (var i = 0; i < log.Count; i++)
        {
            var appended = log[i];
            if (!LogLeaf.Hash(appended).TryGetValue(out var leaf, out var leafError))
                return Result<ActaLog>.Fail(ActaErrors.LeafUncomputable(i, leafError!));

            leaves.Add(leaf);
            index[appended.Event.Id.Value] = i;

            switch (appended.Event.Type.Value)
            {
                case LogEntries.HeadType:
                    if (!ReadHead(i, appended.Event.Payload).TryGetValue(out var head, out var headError))
                        return Result<ActaLog>.Fail(headError!);
                    heads.Add(head!);
                    break;

                case LogEntries.KeyType:
                    if (!ReadKey(i, appended.Event.Payload).TryGetValue(out var key, out var keyError))
                        return Result<ActaLog>.Fail(keyError!);
                    keys.Add(key!);
                    break;

                default:
                    break;
            }
        }

        return Result<ActaLog>.Ok(new ActaLog(log, leaves.ToImmutable(), heads.ToImmutable(), keys.ToImmutable(), index.ToImmutable()));
    }

    private static Result<SignedHead> ReadHead(long logIndex, JsonValue payload)
    {
        if (payload is not JsonValue.Object o
            || Member(o, LogEntries.HeadMember) is not JsonValue.Object head
            || Member(o, LogEntries.KidMember) is not JsonValue.String kid
            || Member(o, LogEntries.SignatureMember) is not JsonValue.String signature
            || Member(head, LogEntries.RootHashMember) is not JsonValue.String rootText
            || Member(head, LogEntries.TimestampMember) is not JsonValue.String timestamp
            || Member(head, LogEntries.TreeSizeMember) is not JsonValue.Number size
            || LogEntries.Unprefixed(rootText.Value) is not { } root
            || size.Value < 0 || size.Value > logIndex || Math.Floor(size.Value) != size.Value)
            return Result<SignedHead>.Fail(ActaErrors.MalformedHead(logIndex));

        return Result<SignedHead>.Ok(new SignedHead(logIndex, (long)size.Value, root, timestamp.Value, kid.Value, signature.Value, head));
    }

    private static Result<LogKey> ReadKey(long logIndex, JsonValue payload)
    {
        if (payload is not JsonValue.Object o
            || Member(o, LogEntries.KidMember) is not JsonValue.String kid
            || Member(o, LogEntries.JwkMember) is not JsonValue.Object jwk
            || Member(o, LogEntries.ValidFromMember) is not JsonValue.String validFrom)
            return Result<LogKey>.Fail(ActaErrors.MalformedKey(logIndex));

        return Result<LogKey>.Ok(new LogKey(logIndex, kid.Value, jwk, validFrom.Value));
    }

    private static JsonValue? Member(JsonValue.Object o, string name)
    {
        foreach (var m in o.Members)
            if (string.Equals(m.Key, name, StringComparison.Ordinal))
                return m.Value;
        return null;
    }
}

/// <summary>RFC 9457 problem-type slugs the Acta emits.</summary>
public static class ActaErrors
{
    public static Error LeafUncomputable(long logIndex, Error cause)
    {
        ArgumentNullException.ThrowIfNull(cause);
        return new(
            "curia/acta/leaf-uncomputable",
            "An event in the log has no canonical form and so no leaf",
            $"log_index={logIndex} cause={cause.Type}");
    }

    public static Error MalformedHead(long logIndex) => new(
        "curia/acta/malformed-head",
        "A log.head entry does not carry { head: { root_hash, timestamp, tree_size }, kid, signature } with a size the log had reached",
        $"log_index={logIndex}");

    public static Error MalformedKey(long logIndex) => new(
        "curia/acta/malformed-key",
        "A log.key entry does not carry { kid, jwk, valid_from }",
        $"log_index={logIndex}");

    public static Error NoHead(long treeSize) => new(
        "curia/log/no-head",
        "No signed tree head has been published yet",
        $"current_tree_size={treeSize}");

    public static Error NotALeaf(long index, long treeSize) => new(
        "curia/log/not-a-leaf",
        "The index is not a leaf of a tree of that size",
        $"log_index={index} tree_size={treeSize}");

    public static Error BadSizes(long from, long to, long treeSize) => new(
        "curia/log/bad-sizes",
        "A consistency proof needs 1 <= from <= to <= the log's size",
        $"from={from} to={to} tree_size={treeSize}");
}
