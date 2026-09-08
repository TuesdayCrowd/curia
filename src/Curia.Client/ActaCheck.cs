using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Acta;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Envelope;
using Curia.Canon.Jws;
using Curia.Canon.Sodium;
using Curia.Domain.Acta;
using Curia.Domain.Primitives;

namespace Curia.Client;

/// <summary>
/// What a check established. Three outcomes, and R6.52 forbids collapsing the third into either of
/// the others.
///
/// <para><b>Why three and not a boolean.</b> "The key set was unreachable" and "this signature is
/// forged" are the two claims a reader most needs kept apart, because one is a network fault and
/// the other is an attack. A client that collapses them reports an attack whenever a host is down,
/// and does it to a reader that cannot ask a follow-up question. The same collapse the other way --
/// reporting <i>verified</i> for a check that never ran -- is the shape this project's own trap
/// list is about: an absence that reads as a satisfied answer.</para>
/// </summary>
public enum CheckOutcome
{
    /// <summary>The check ran and the predicate held.</summary>
    Verified,

    /// <summary>The check ran and the predicate did not hold. Something is wrong with the material.</summary>
    Failed,

    /// <summary>
    /// The check did not run: material was unreachable, absent, or unparseable. Not a verdict either
    /// way, and never reported as one.
    /// </summary>
    CouldNotCheck,
}

/// <summary>One of R6.52's checks, with the predicate that decided it.</summary>
/// <param name="Detail">
/// Why. Carried on every outcome including success, because "verified against a head at tree size
/// 41" and "verified against a root nobody signed" are different sentences and only one of them is
/// evidence.
/// </param>
public sealed record Check(CheckOutcome Outcome, string Detail)
{
    public static Check Verified(string detail) => new(CheckOutcome.Verified, detail);

    public static Check Failed(string detail) => new(CheckOutcome.Failed, detail);

    public static Check CouldNotCheck(string detail) => new(CheckOutcome.CouldNotCheck, detail);

    /// <summary>The outcome as a word a reader cannot misread, then the predicate.</summary>
    public string Describe => Outcome switch
    {
        CheckOutcome.Verified => "verified: " + Detail,
        CheckOutcome.Failed => "FAILED: " + Detail,
        CheckOutcome.CouldNotCheck => "COULD NOT BE CHECKED: " + Detail,
        _ => "COULD NOT BE CHECKED: " + Detail,
    };
}

/// <summary>
/// R6.52's inclusion and consistency checks, as pure predicates over material somebody else
/// fetched.
///
/// <para><b>Nothing here performs I/O</b>, which is what lets the whole of R6.52 be exercised
/// against <c>conformance/merkle/</c> and <c>conformance/acta/</c> without a Forum, and what keeps
/// the fetching -- where "could not check" is decided -- in one place above it
/// (<see cref="PostVerifier"/>).</para>
///
/// <para><b>The leaf is recomputed, never taken.</b> R6.52 spells out why: the served
/// <c>leaf_hash</c> sits on the response and on the proof, outside the object whose hash is being
/// proved, so substituting it for a recomputation does not shorten the check -- it skips it, and
/// what remains is the Forum checking its own arithmetic against its own input. The independently
/// written Rust verifier makes the same choice, in the same words, and refuses a digest
/// (<c>rust/curia-testis/src/acta.rs</c>).</para>
/// </summary>
public static class ActaCheck
{
    /// <summary>
    /// R6.46's leaf hash, recomputed from the served entry: the entry object under <b>pure</b>
    /// RFC 8785 -- no Unicode normalization -- then <c>SHA-256(0x00 ‖ input)</c>.
    ///
    /// <para>Pure, not NFC, and the distinction is load-bearing: R6.9's normalization belongs where
    /// a signature is computed, and normalizing an event's payload on its way into a digest would
    /// touch bytes §6.4 fixed at PERSIST. <see cref="LogLeaf.Input"/> makes the same choice on the
    /// Forum's side, and <c>conformance/acta/nfd-payload-stays-nfd</c> pins it in both
    /// implementations.</para>
    /// </summary>
    public static Result<ImmutableArray<byte>> RecomputeLeaf(JsonValue.Object entry) =>
        CanonicalJson.Canonicalize(entry).Map(input => MerkleTree.LeafHash(input.Span));

    /// <summary>
    /// R11.29's binding: the entry describes <i>this</i> post, established by byte-identity of the
    /// canonical form the read served.
    ///
    /// <para>Without it the whole proof is about whatever leaf the Forum can prove. A leaf index the
    /// Forum chose is a proof about a document nobody is acting on, and it verifies perfectly.</para>
    /// </summary>
    public static bool EntryDescribes(LogEntryDocument entry, ProvenancePost post)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(post);

        return ClientJson.Object(entry.Entry, LogLeaf.PayloadMember) is { } payload
            && ClientJson.String(payload, "canonical") is { } canonical
            && string.Equals(canonical, post.Canonical, StringComparison.Ordinal);
    }

    /// <summary>
    /// R6.48's audit path, checked against a leaf recomputed from <paramref name="entry"/> and
    /// bound to <paramref name="post"/>.
    ///
    /// <para>Every failure below is a <see cref="CheckOutcome.Failed"/> rather than a
    /// <see cref="CheckOutcome.CouldNotCheck"/>, because each one means the material the Forum
    /// served disagrees with itself. The absent-material cases are decided above this method, where
    /// the fetching happens.</para>
    /// </summary>
    public static Check Inclusion(LogEntryDocument entry, InclusionProofDocument proof, ProvenancePost post)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(post);

        if (entry.LogIndex != proof.LogIndex)
            return Check.Failed(Text(
                $"the entry is leaf {entry.LogIndex} and the proof is about leaf {proof.LogIndex}"));

        if (!EntryDescribes(entry, post))
            return Check.Failed(
                "the log entry does not carry this post's canonical bytes, so the proof is about " +
                "some other leaf (R11.29)");

        if (!RecomputeLeaf(entry.Entry).TryGetValue(out var leaf, out var error))
            return Check.Failed($"the entry has no canonical form: {error!.Type}");

        var recomputed = LogEntries.Prefixed(leaf);

        if (!string.Equals(recomputed, proof.LeafHash, StringComparison.Ordinal))
            return Check.Failed(
                $"the entry hashes to {recomputed} and the proof is about {proof.LeafHash}");

        if (!string.Equals(recomputed, entry.LeafHash, StringComparison.Ordinal))
            return Check.Failed(
                $"the entry hashes to {recomputed} and the log-entry route reported {entry.LeafHash}");

        if (LogEntries.Unprefixed(proof.RootHash) is not { } root)
            return Check.Failed($"the proof's root is not a sha256 digest: {proof.RootHash}");

        var path = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(proof.AuditPath.Length);
        foreach (var node in proof.AuditPath)
        {
            if (LogEntries.Unprefixed(node) is not { } decoded)
                return Check.Failed($"the audit path holds something that is not a sha256 digest: {node}");

            path.Add(decoded);
        }

        return MerkleTree.VerifyInclusion(leaf.AsSpan(), proof.LogIndex, proof.TreeSize, path.ToImmutable(), root.AsSpan())
            ? Check.Verified(Text(
                $"leaf {proof.LogIndex} of {proof.TreeSize}, recomputed from the log entry, is under root {proof.RootHash}"))
            : Check.Failed(Text(
                $"the audit path does not carry leaf {proof.LogIndex} to root {proof.RootHash}"));
    }

    /// <summary>
    /// R6.49: whether a head is signed by a key the log itself published, and whether it covers the
    /// size and root a proof verified against.
    ///
    /// <para>A head is a different statement from a post and carries its own <c>typ</c>
    /// (<c>curia-head+jws</c>). The <c>typ</c> discipline is what stops a post signature being
    /// replayed as a head even when an operator has reused one key for both.</para>
    /// </summary>
    public static Check HeadSignature(SignedHeadDocument head, ImmutableArray<LogJwk> keys)
    {
        ArgumentNullException.ThrowIfNull(head);

        if (keys.IsDefaultOrEmpty)
            return Check.CouldNotCheck("the log publishes no keys, so nothing can verify this head");

        var match = keys.LastOrDefault(k => string.Equals(k.Key.Kid, head.Kid, StringComparison.Ordinal));
        if (match is null)
            return Check.Failed($"the log publishes no key with kid={head.Kid}");

        if (!SignatureCheck.Material(match.Key).TryGetValue(out var material, out var keyError))
            return Check.Failed($"the log's key kid={head.Kid} is unusable: {keyError!.Type}");

        if (!LogEntries.HeadCanonical(head.Head).TryGetValue(out var canonical, out var canonError))
            return Check.Failed($"the head has no canonical form: {canonError!.Type}");

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal),
            Verifiers,
            DetachedJws.HeadTyp);

        return jws.Verify(canonical, new JwsSignature(head.Signature), material!).TryGetValue(out _, out var verifyError)
            ? Check.Verified(Text($"signed by the log's kid={head.Kid} over tree size {head.TreeSize}"))
            : Check.Failed($"the head's signature does not verify: {verifyError!.Type}");
    }

    /// <summary>Whether a verified head commits to exactly the size and root a proof climbed to.</summary>
    public static Check HeadCovers(SignedHeadDocument head, long treeSize, string rootHash)
    {
        ArgumentNullException.ThrowIfNull(head);

        if (head.TreeSize != treeSize)
            return Check.Failed(Text(
                $"the signed head covers {head.TreeSize} leaves and the proof is against {treeSize}"));

        return string.Equals(head.RootHash, rootHash, StringComparison.Ordinal)
            ? Check.Verified(Text($"the signed head commits to root {rootHash} at tree size {treeSize}"))
            : Check.Failed($"the signed head's root is {head.RootHash} and the proof climbs to {rootHash}");
    }

    /// <summary>
    /// R6.23's consistency proof: whether the log holding <paramref name="to"/> is an append-only
    /// extension of the one holding <paramref name="from"/>.
    ///
    /// <para>The two roots come from the two <i>heads</i>, not from the proof, and the proof's own
    /// <c>from_root</c>/<c>to_root</c> are checked against them. A proof that carried its own roots
    /// unchallenged would let a Forum that had equivocated hand over a self-consistent proof between
    /// two forks it invented, which is exactly the fork the retained head exists to catch.</para>
    /// </summary>
    public static Check Consistency(
        SignedHeadDocument from, SignedHeadDocument to, ConsistencyProofDocument proof)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(proof);

        if (proof.FromSize != from.TreeSize || proof.ToSize != to.TreeSize)
            return Check.Failed(Text(
                $"the proof runs {proof.FromSize} -> {proof.ToSize} and the heads are " +
                $"{from.TreeSize} -> {to.TreeSize}"));

        if (!string.Equals(proof.FromRoot, from.RootHash, StringComparison.Ordinal))
            return Check.Failed($"the proof's from_root is {proof.FromRoot} and the retained head's is {from.RootHash}");

        if (!string.Equals(proof.ToRoot, to.RootHash, StringComparison.Ordinal))
            return Check.Failed($"the proof's to_root is {proof.ToRoot} and the served head's is {to.RootHash}");

        if (LogEntries.Unprefixed(proof.FromRoot) is not { } fromRoot
            || LogEntries.Unprefixed(proof.ToRoot) is not { } toRoot)
            return Check.Failed("a root on the consistency proof is not a sha256 digest");

        var path = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(proof.Path.Length);
        foreach (var node in proof.Path)
        {
            if (LogEntries.Unprefixed(node) is not { } decoded)
                return Check.Failed($"the consistency path holds something that is not a sha256 digest: {node}");

            path.Add(decoded);
        }

        return MerkleTree.VerifyConsistency(
            proof.FromSize, proof.ToSize, fromRoot.AsSpan(), toRoot.AsSpan(), path.ToImmutable())
            ? Check.Verified(Text(
                $"the log at {proof.ToSize} leaves extends the head this client retained at {proof.FromSize}"))
            : Check.Failed(Text(
                $"the log at {proof.ToSize} leaves is NOT an extension of the head this client " +
                $"retained at {proof.FromSize}. The log has equivocated, or one of the two heads is " +
                "not this log's (R6.24)"));
    }

    /// <summary>
    /// ES256 and EdDSA, the two the Forum issues. Built per call rather than shared: the adapters
    /// are stateless, and a static dictionary shared across threads is a cache nobody asked for.
    /// </summary>
    private static Dictionary<string, IContentVerifier> Verifiers => new(StringComparer.Ordinal)
    {
        ["ES256"] = new Es256Adapter(),
        ["EdDSA"] = new Ed25519Adapter(),
    };

    private static string Text(string value) => string.Create(CultureInfo.InvariantCulture, $"{value}");
}
