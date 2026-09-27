using System.Collections.Immutable;
using System.Globalization;
using System.Text;
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

    /// <summary>
    /// A value this client did not write -- a string the Forum served, or one an agent put in the
    /// log -- as a detail carries it: a JSON string literal, so nothing inside it can end the line it
    /// sits on or begin another.
    ///
    /// <para><b>Why every such value, and why one helper.</b> A detail is a line a reader reads, often
    /// a model through <c>curia_verify</c> (R11.29), and the values it names come from the material
    /// under check: an entry type, an agent's identifier, a <c>kid</c>, a digest, a Forum problem
    /// document. Printed raw, a value holding a newline begins a line that reads as this client's
    /// own, a forged <c>verified:</c> or <c>VERIFIED.</c>. Quoted, it stays inside the literal:
    /// <c>"</c> and <c>\</c> are escaped with a backslash, and every control or format character,
    /// both Unicode separators and half a surrogate pair as <c>\u</c> and four hex digits. One helper,
    /// so no site escapes less than another. A null value is the absence <c>(none)</c>, unquoted,
    /// which no quoted value can be mistaken for.</para>
    /// </summary>
    public static string Quote(string? value)
    {
        if (value is null) return "(none)";

        var quoted = new StringBuilder(value.Length + 2).Append('"');
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '"' or '\\')
                quoted.Append('\\').Append(c);
            else if (Escaped(value, i))
                quoted.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
            else
                quoted.Append(c);
        }

        return quoted.Append('"').ToString();
    }

    /// <summary>
    /// Whether <see cref="Quote"/> writes the character at <paramref name="i"/> as an escape: a
    /// control or format character, a line or paragraph separator, or a surrogate without its pair.
    /// </summary>
    private static bool Escaped(string value, int i)
    {
        var category = char.GetUnicodeCategory(value[i]);
        if (category is UnicodeCategory.Control or UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
            return true;

        if (category is not UnicodeCategory.Surrogate) return false;

        return char.IsHighSurrogate(value[i])
            ? !char.IsSurrogatePair(value, i)
            : i == 0 || !char.IsHighSurrogate(value[i - 1]);
    }
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

        return ProofHolds(entry, proof);
    }

    /// <summary>
    /// R6.54 (errata G16), the post's half: the log's own record of the served post, from which the
    /// check reads the post's author, <c>kid</c> and signature -- never from what the Forum served
    /// beside it.
    ///
    /// <para><b>Everything here needs no key material, and all of it is checked before the key's
    /// binding is looked for</b>, so a post whose own record fails is reported failed even where the
    /// key set names no leaf. In order: the entry is bound to the served post and proven under
    /// <paramref name="head"/> (R6.52, R11.29); it is a <c>post.accepted</c>, the log's record of an
    /// acceptance rather than some other entry carrying the same bytes; its envelope passes ADMIT and
    /// its signature's protected header parses; and the author the Forum served the post as is the
    /// author that envelope names, which the signature covers. The last is the one check a reader
    /// holding log documents alone cannot make, and the reason a client can report a post failed
    /// where <c>curia-testis log author</c>, handed the same log, reports it verified: a Forum that
    /// served alice's post as mallory's has misattributed it, whoever the log says holds the key.</para>
    /// </summary>
    public static Check PostOfRecord(
        ProvenancePost post, LogEntryDocument postEntry, InclusionProofDocument postProof, SignedHeadDocument head)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(postEntry);
        ArgumentNullException.ThrowIfNull(postProof);
        ArgumentNullException.ThrowIfNull(head);

        return ReadPostOfRecord(post, postEntry, postProof, head).Verdict;
    }

    /// <summary>
    /// R6.54 (errata G16): the key that signed <paramref name="post"/>, as the log records it, is the
    /// key the log bound to its author at a leaf before the post's -- checked against the key the
    /// binding entry carries, never against a key a key set serves, and with both leaves proven under
    /// <paramref name="head"/>.
    ///
    /// <para><b>The post's author, <c>kid</c> and signature are the log's</b> (<see cref="PostOfRecord"/>):
    /// the envelope and signature <paramref name="postEntry"/> carries, which R11.29 binds to the
    /// served post by its canonical bytes. The provenance's <c>author</c> and the served signature are
    /// the Forum's word beside the log, as the key set is; a check that took either could be steered
    /// by a Forum that served one post and logged another's key.</para>
    ///
    /// <para><b>What each outcome means.</b> <i>Verified</i>: the post's record holds, and
    /// <paramref name="keyEntry"/> is an <c>agent.key-bound</c> entry of the post's author naming the
    /// post's <c>kid</c>, proven under the same head, before the post, and the post as the log holds
    /// it verifies under the key it carries. <i>Could not be checked</i>: every check that needs no
    /// more held, and the log holds no key for the post from before it -- the author's binding of the
    /// <c>kid</c> sits at or after the post, or it is the author's <c>agent.enrolled</c>, which names
    /// the <c>kid</c> and carries no key: all an identity enrolled before R4.34 has, and a reader
    /// cannot tell when one was made. That is the log's silence about the key the post was accepted
    /// under, not a contradiction of it: a forger binds first at no cost, and what lands here is
    /// history older than its binding. <i>Failed</i>: anything else, and it is decided first -- a
    /// proof that does not hold or that the head does not commit to, an entry of another type,
    /// author, stream or <c>kid</c>, an entry that binds no usable key, or a signature that does not
    /// verify under the bound key -- because each is the served material disagreeing with itself or
    /// with the head. An entry's order or type is read only once its leaf is proven under the head,
    /// because a leaf the head does not hold says nothing about the log. The absent-material cases
    /// are decided above this method, where the fetching happens.</para>
    /// </summary>
    public static Check KeyBinding(
        ProvenancePost post, LogEntryDocument postEntry, InclusionProofDocument postProof,
        LogEntryDocument keyEntry, InclusionProofDocument keyProof, SignedHeadDocument head)
    {
        ArgumentNullException.ThrowIfNull(post);
        ArgumentNullException.ThrowIfNull(postEntry);
        ArgumentNullException.ThrowIfNull(postProof);
        ArgumentNullException.ThrowIfNull(keyEntry);
        ArgumentNullException.ThrowIfNull(keyProof);
        ArgumentNullException.ThrowIfNull(head);

        var (record, logged) = ReadPostOfRecord(post, postEntry, postProof, head);
        if (logged is null) return record;

        if (keyEntry.LogIndex != keyProof.LogIndex)
            return Check.Failed(Text(
                $"the key's entry is leaf {keyEntry.LogIndex} and its proof is about leaf {keyProof.LogIndex}"));

        var proven = ProofHolds(keyEntry, keyProof);
        if (proven.Outcome is not CheckOutcome.Verified) return proven;

        var keyCovers = HeadCovers(head, keyProof.TreeSize, keyProof.RootHash);
        if (keyCovers.Outcome is not CheckOutcome.Verified) return keyCovers;

        var type = ClientJson.String(keyEntry.Entry, LogLeaf.EventTypeMember);
        var aggregate = ClientJson.String(keyEntry.Entry, LogLeaf.AggregateIdMember);
        var payload = ClientJson.Object(keyEntry.Entry, LogLeaf.PayloadMember);
        var boundAgent = payload is null ? null : ClientJson.String(payload, "agent_id");
        var boundKid = payload is null ? null : ClientJson.String(payload, "kid");

        var enrolled = string.Equals(type, EnrolledType, StringComparison.Ordinal);
        if (!enrolled && !string.Equals(type, KeyBoundType, StringComparison.Ordinal))
            return Check.Failed(Text(
                $"the entry at leaf {keyEntry.LogIndex} is of type {Check.Quote(type)}, not {KeyBoundType} or {EnrolledType}, so it binds no key"));

        if (!string.Equals(aggregate, logged.Author, StringComparison.Ordinal)
            || !string.Equals(boundAgent, logged.Author, StringComparison.Ordinal)
            || !string.Equals(boundKid, logged.Kid, StringComparison.Ordinal))
            return Check.Failed(Text(
                $"the entry at leaf {keyEntry.LogIndex} ({Check.Quote(type)}) is an entry for {Check.Quote(boundAgent)} kid={Check.Quote(boundKid)} in stream {Check.Quote(aggregate)}, and the log holds this post as written by {Check.Quote(logged.Author)} under kid={Check.Quote(logged.Kid)}"));

        if (keyEntry.LogIndex >= logged.Index)
            return Check.CouldNotCheck(Text(
                $"kid={Check.Quote(boundKid)} is bound to {Check.Quote(logged.Author)} at leaf {keyEntry.LogIndex}, which is not before this post at leaf {logged.Index}, so the log holds no key for it from before the post"));

        if (enrolled)
            return Check.CouldNotCheck(Text(
                $"leaf {keyEntry.LogIndex} is the enrollment of {Check.Quote(logged.Author)} naming kid={Check.Quote(boundKid)}, which carries no key (all an identity enrolled before R4.34 has), so which key signed cannot be established from the log"));

        if ((payload is null ? null : ClientJson.Object(payload, "jwk")) is not { } jwk
            || ForumDocuments.ReadJwk(jwk) is not { } bound
            || !string.Equals(bound.Kid, boundKid, StringComparison.Ordinal))
            return Check.Failed(Text($"leaf {keyEntry.LogIndex} binds kid={Check.Quote(boundKid)} and carries no usable key under it"));

        var verdict = SignatureCheck.Verify(post with { Signature = logged.Signature }, [bound]);
        return verdict.Verified
            ? Check.Verified(Text(
                $"kid={Check.Quote(boundKid)} is the key the log bound to {Check.Quote(logged.Author)} at leaf {keyEntry.LogIndex}, before this post at leaf {logged.Index}, the post as the log holds it verifies under the key that leaf carries, and {keyCovers.Detail}"))
            : Check.Failed(Text(
                $"the post, as the log holds it, does not verify under the key the log bound to {Check.Quote(logged.Author)} at leaf {keyEntry.LogIndex}: {verdict.Detail}"));
    }

    private const string PostAcceptedType = "post.accepted";
    private const string KeyBoundType = "agent.key-bound";
    private const string EnrolledType = "agent.enrolled";

    /// <summary>The post as the log records it: its envelope's author, its signature's <c>kid</c>, the signature, and its leaf.</summary>
    private sealed record LoggedPost(string Author, string Kid, string Signature, long Index);

    /// <summary>
    /// <see cref="PostOfRecord"/>'s checks, in its order, and the post they establish; the post is
    /// null exactly when the verdict is not verified.
    /// </summary>
    private static (Check Verdict, LoggedPost? Post) ReadPostOfRecord(
        ProvenancePost post, LogEntryDocument postEntry, InclusionProofDocument postProof, SignedHeadDocument head)
    {
        var included = Inclusion(postEntry, postProof, post);
        if (included.Outcome is not CheckOutcome.Verified) return (included, null);

        var covers = HeadCovers(head, postProof.TreeSize, postProof.RootHash);
        if (covers.Outcome is not CheckOutcome.Verified) return (covers, null);

        var type = ClientJson.String(postEntry.Entry, LogLeaf.EventTypeMember);
        if (!string.Equals(type, PostAcceptedType, StringComparison.Ordinal))
            return (Check.Failed(Text(
                $"the post's own leaf {postProof.LogIndex} is of type {Check.Quote(type)}, not {PostAcceptedType}: it carries the post's bytes and is not the log's record of the post's acceptance")), null);

        var payload = ClientJson.Object(postEntry.Entry, LogLeaf.PayloadMember);
        var canonical = payload is null ? null : ClientJson.String(payload, "canonical");
        var signature = payload is null ? null : ClientJson.String(payload, "signature");
        if (canonical is null || signature is null)
            return (Check.Failed("the post's own log entry carries no envelope or no signature"), null);

        // ADMIT, as the signature check does, so no author is read from a document with two answers.
        if (!JsonReader.Parse(Encoding.UTF8.GetBytes(canonical), AdmitLimits.Default).TryGetValue(out var tree, out var admitError)
            || tree is not JsonValue.Object envelope)
            return (Check.Failed($"the envelope the post's own log entry carries is not one ADMIT accepts: {admitError?.Type ?? "not an object"}"), null);

        if (ClientJson.String(envelope, "author") is not { Length: > 0 } author)
            return (Check.Failed("the envelope the post's own log entry carries names no author"), null);

        if (!DetachedJws.ReadProtectedHeader(new JwsSignature(signature)).TryGetValue(out var header, out var headerError))
            return (Check.Failed($"the signature the post's own log entry carries has no readable protected header: {headerError!.Type}"), null);

        if (!string.Equals(post.Provenance.Author, author, StringComparison.Ordinal))
            return (Check.Failed(Text(
                (string.IsNullOrEmpty(post.Provenance.Author)
                    ? "the Forum served this post with no author"
                    : $"the Forum served this post as written by {Check.Quote(post.Provenance.Author)}")
                + $", and the envelope its log entry carries, which the signature covers, names {Check.Quote(author)}")), null);

        return (Check.Verified(Text(
            $"leaf {postProof.LogIndex} is the log's record of this post's acceptance: the envelope of {Check.Quote(author)}, signed under kid={Check.Quote(header!.Kid)}")),
            new LoggedPost(author, header.Kid, signature, postProof.LogIndex));
    }

    /// <summary>
    /// R6.48's audit path, over a leaf recomputed from <paramref name="entry"/>: the leaf the proof
    /// and the entry route each state must be the recomputed one, and the path must carry it to the
    /// proof's root. Shared by the post's check and the key's, so the two cannot come apart.
    /// </summary>
    private static Check ProofHolds(LogEntryDocument entry, InclusionProofDocument proof)
    {
        if (!RecomputeLeaf(entry.Entry).TryGetValue(out var leaf, out var error))
            return Check.Failed($"the entry has no canonical form: {error!.Type}");

        var recomputed = LogEntries.Prefixed(leaf);

        if (!string.Equals(recomputed, proof.LeafHash, StringComparison.Ordinal))
            return Check.Failed(
                $"the entry hashes to {recomputed} and the proof is about {Check.Quote(proof.LeafHash)}");

        if (!string.Equals(recomputed, entry.LeafHash, StringComparison.Ordinal))
            return Check.Failed(
                $"the entry hashes to {recomputed} and the log-entry route reported {Check.Quote(entry.LeafHash)}");

        if (LogEntries.Unprefixed(proof.RootHash) is not { } root)
            return Check.Failed($"the proof's root is not a sha256 digest: {Check.Quote(proof.RootHash)}");

        var path = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(proof.AuditPath.Length);
        foreach (var node in proof.AuditPath)
        {
            if (LogEntries.Unprefixed(node) is not { } decoded)
                return Check.Failed($"the audit path holds something that is not a sha256 digest: {Check.Quote(node)}");

            path.Add(decoded);
        }

        return MerkleTree.VerifyInclusion(leaf.AsSpan(), proof.LogIndex, proof.TreeSize, path.ToImmutable(), root.AsSpan())
            ? Check.Verified(Text(
                $"leaf {proof.LogIndex} of {proof.TreeSize}, recomputed from the log entry, is under root {Check.Quote(proof.RootHash)}"))
            : Check.Failed(Text(
                $"the audit path does not carry leaf {proof.LogIndex} to root {Check.Quote(proof.RootHash)}"));
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
            return Check.Failed($"the log publishes no key with kid={Check.Quote(head.Kid)}");

        if (!SignatureCheck.Material(match.Key).TryGetValue(out var material, out var keyError))
            return Check.Failed($"the log's key kid={Check.Quote(head.Kid)} is unusable: {keyError!.Type}");

        if (!LogEntries.HeadCanonical(head.Head).TryGetValue(out var canonical, out var canonError))
            return Check.Failed($"the head has no canonical form: {canonError!.Type}");

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner>(StringComparer.Ordinal),
            Verifiers,
            DetachedJws.HeadTyp);

        return jws.Verify(canonical, new JwsSignature(head.Signature), material!).TryGetValue(out _, out var verifyError)
            ? Check.Verified(Text($"signed by the log's kid={Check.Quote(head.Kid)} over tree size {head.TreeSize}"))
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
            ? Check.Verified(Text($"the signed head commits to root {Check.Quote(rootHash)} at tree size {treeSize}"))
            : Check.Failed($"the signed head's root is {Check.Quote(head.RootHash)} and the proof climbs to {Check.Quote(rootHash)}");
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
            return Check.Failed($"the proof's from_root is {Check.Quote(proof.FromRoot)} and the retained head's is {Check.Quote(from.RootHash)}");

        if (!string.Equals(proof.ToRoot, to.RootHash, StringComparison.Ordinal))
            return Check.Failed($"the proof's to_root is {Check.Quote(proof.ToRoot)} and the served head's is {Check.Quote(to.RootHash)}");

        if (LogEntries.Unprefixed(proof.FromRoot) is not { } fromRoot
            || LogEntries.Unprefixed(proof.ToRoot) is not { } toRoot)
            return Check.Failed("a root on the consistency proof is not a sha256 digest");

        var path = ImmutableArray.CreateBuilder<ImmutableArray<byte>>(proof.Path.Length);
        foreach (var node in proof.Path)
        {
            if (LogEntries.Unprefixed(node) is not { } decoded)
                return Check.Failed($"the consistency path holds something that is not a sha256 digest: {Check.Quote(node)}");

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
