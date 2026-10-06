using System.Globalization;
using System.Text;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// R6.52's three checks over one served post, and R6.54's fourth (errata G16), each reported
/// separately.
/// </summary>
/// <param name="Digest">
/// SHA-256 over bytes this client re-canonicalized, in the <c>sha256:</c> form the wire uses, so a
/// reader can compare it against the digest a read printed. Null when the served document has no
/// canonical form at all.
/// </param>
/// <param name="AnchorTreeSize">
/// The tree size the inclusion proof was anchored against, when there was one. Null says no anchor
/// was reached, which is why <see cref="Inclusion"/> then reports that it could not be checked.
/// </param>
/// <param name="KeyBinding">
/// R6.54: whether the key that signed the post is the key the log bound to its author before the
/// post, checked under the key the binding entry carries rather than one a key set serves.
/// </param>
public sealed record PostVerification(
    string PostId,
    Check Signature,
    Check Inclusion,
    Check Consistency,
    Check KeyBinding,
    string? Digest,
    long? LogIndex,
    long? AnchorTreeSize)
{
    /// <summary>
    /// One word for the four, for a caller that wants a single answer.
    ///
    /// <para><b>Any failure is a failure.</b> A post whose signature verifies and whose inclusion
    /// proof does not is not partly authentic.</para>
    ///
    /// <para><b>Verified needs the key's binding as well as the signature (R6.54).</b> The signature
    /// check verifies under a key the Forum's key set serves; the binding check is what shows that key
    /// is the one the log bound to the author before the post. Without it a Forum that substituted a
    /// key, and signed under it, would be verified by every reader. So a post whose key the log names
    /// only by <c>kid</c> -- an identity enrolled before R4.34 -- is not established.</para>
    ///
    /// <para><b>Consistency is conditional and its absence does not deny the rest.</b> R6.52 asks
    /// for it "where the client retains an earlier signed head for the same log" -- on a first read
    /// there is no earlier head, so the check is inapplicable rather than skipped, and letting that
    /// downgrade the verdict would mean no first read could ever verify. The line still says
    /// <i>could not be checked</i>, because that is what happened; only the summary is permitted to
    /// look past it, and only when nothing failed.</para>
    /// </summary>
    public CheckOutcome Overall =>
        Signature.Outcome is CheckOutcome.Failed
        || Inclusion.Outcome is CheckOutcome.Failed
        || Consistency.Outcome is CheckOutcome.Failed
        || KeyBinding.Outcome is CheckOutcome.Failed
            ? CheckOutcome.Failed
            : Signature.Outcome is CheckOutcome.Verified
              && Inclusion.Outcome is CheckOutcome.Verified
              && KeyBinding.Outcome is CheckOutcome.Verified
                ? CheckOutcome.Verified
                : CheckOutcome.CouldNotCheck;

    /// <summary>
    /// The four checks as four lines, plus the summary.
    ///
    /// <para><b>No post body and no log entry appears here, and none may.</b> This renders verdicts
    /// about a post, not the post: R6.51 forbids surfacing a log entry, and this method is the one
    /// place a fetched entry could have leaked into a result.</para>
    ///
    /// <para><b>Not every string here is this client's own, and saying otherwise would be the
    /// project's own failure mode.</b> The lines name values from the material under check -- the
    /// post's id, an identifier or <c>kid</c> an agent chose, an entry type or digest the log holds,
    /// the Forum's problem document -- each inside a sentence this client framed. Each such value is
    /// written by <see cref="Check.Quote"/> as a display literal (R10.64), the problem document's
    /// words where <see cref="Refusal.Summary"/> composed them, so none can end its line or begin
    /// another: this text is read line by line, by
    /// a model through <c>curia_verify</c> (R11.29), and a value that began a line could forge one of
    /// this client's verdicts. The Forum's slugs are otherwise passed through unaltered, because a
    /// client that rewrote a <c>curia/</c> slug into its own vocabulary would leave its reader unable
    /// to search the specification for what happened.</para>
    /// </summary>
    public string Render()
    {
        var builder = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        builder.Append(culture, $"post       {Check.Quote(PostId)}\n");
        builder.Append(culture, $"digest     {Digest ?? "(no canonical form)"}   (computed here, from the served document)\n");
        builder.Append(culture, $"log_index  {(LogIndex is { } index ? index.ToString(culture) : "(not served)")}\n");
        builder.Append(culture, $"anchor     {(AnchorTreeSize is { } size ? $"signed head at tree size {size}" : "(none reached)")}\n");
        builder.Append('\n');
        builder.Append(culture, $"signature   {Signature.Describe}\n");
        builder.Append(culture, $"inclusion   {Inclusion.Describe}\n");
        builder.Append(culture, $"consistency {Consistency.Describe}\n");
        builder.Append(culture, $"key         {KeyBinding.Describe}\n");
        builder.Append('\n');

        builder.Append(Overall switch
        {
            CheckOutcome.Verified =>
                "VERIFIED. This client checked the signature over bytes it re-canonicalized itself, "
                + "recomputed the log leaf from the log's own entry rather than accepting the "
                + "digest the Forum served for it, and found the signing key bound to the author in "
                + "the log before the post.\n",
            CheckOutcome.Failed =>
                "FAILED. At least one check ran and did not hold. Read the failing line above: it "
                + "names the predicate, and a failure here is a claim about the material, not about "
                + "this client's ability to reach it.\n",
            CheckOutcome.CouldNotCheck =>
                "NOT ESTABLISHED. Nothing failed, and something could not be checked. This is not a "
                + "verification and must not be read as one; the line above says what was missing.\n",
            _ =>
                "NOT ESTABLISHED. Nothing failed, and something could not be checked. This is not a "
                + "verification and must not be read as one; the line above says what was missing.\n",
        });

        return builder.ToString();
    }
}

/// <summary>
/// R11.29's verification: the three checks of R6.52, and R6.54's fourth, run against a post a read
/// has already served.
///
/// <para><b>The subject is a served post, deliberately.</b> Verification is a claim about the thing
/// in the reader's context, and a tool that verified an artifact the caller never read would confirm
/// something true about a document nobody is acting on.</para>
///
/// <para><b>The log entry is fetched, and that is the requirement.</b> R6.46's leaf is the whole
/// <i>event</i> -- <c>actor_id</c>, <c>aggregate_id</c>, <c>event_id</c>, <c>event_type</c>,
/// <c>payload</c>, <c>server_ts</c> -- and a served post carries none of the first four, so a leaf
/// cannot be rebuilt from a read. The only alternative to fetching the entry is trusting the
/// <c>leaf_hash</c> the Forum served, and R6.52 says that does not shorten the check, it skips it.
/// R11.29 permits the fetch as proof material, subject to R6.51: the entry is bound to the post by
/// byte-identity of the canonical form the read served, and is never returned.</para>
///
/// <para><b>Every absence is an absence.</b> A key set that will not fetch, a log with no signed
/// head, a leaf the latest head does not yet cover, a Forum that serves no proof -- each is
/// <see cref="CheckOutcome.CouldNotCheck"/>, never a failure and never a pass. R6.52 forbids the
/// collapse in both directions, and the collapse was live in this tree until this type existed:
/// both the CLI and the MCP adapter turned an unreachable JWKS into an empty key array, so a
/// network fault was reported as "the author's JWKS carries no key matching the post's kid".</para>
/// </summary>
public sealed class PostVerifier
{
    private readonly ForumClient _forum;
    private readonly HeadStore _heads;

    public PostVerifier(ForumClient forum, HeadStore heads)
    {
        ArgumentNullException.ThrowIfNull(forum);
        ArgumentNullException.ThrowIfNull(heads);

        _forum = forum;
        _heads = heads;
    }

    public async Task<ForumResult<PostVerification>> VerifyAsync(
        string postId, MarkingMode marking, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);

        var read = await _forum.GetPostAsync(postId, marking, ct).ConfigureAwait(false);
        if (!read.TryGetValue(out var post, out var refusal))
            return ForumResult<PostVerification>.Refused(refusal!);

        return ForumResult<PostVerification>.Ok(
            await VerifyAsync(post!, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// The four checks against a post the caller already holds -- the same post object a read
    /// returned, so no second fetch can substitute a different document for the one being verified.
    /// </summary>
    public async Task<PostVerification> VerifyAsync(ProvenancePost post, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(post);

        var (signature, digest, signingKey) = await SignatureAsync(post, ct).ConfigureAwait(false);

        var head = await AnchorAsync(ct).ConfigureAwait(false);
        var inclusion = await InclusionAsync(post, head, ct).ConfigureAwait(false);
        var consistency = await ConsistencyAsync(head, ct).ConfigureAwait(false);
        var keyBinding = await KeyBindingAsync(post, head, inclusion, signature, signingKey, ct).ConfigureAwait(false);

        return new PostVerification(
            post.PostId,
            signature,
            inclusion.Check,
            consistency,
            keyBinding,
            digest,
            post.LogIndex,
            head.Head?.TreeSize);
    }

    /// <summary>
    /// R6.52's first check, and the one place the JWKS collapse is closed: a key set this client
    /// could not fetch is reported as unfetched, never as a key that does not exist. Also returns the
    /// served key the signature names, whose <c>curia_log_index</c> is where R6.54's check looks.
    /// </summary>
    private async Task<(Check Check, string? Digest, ForumJwk? SigningKey)> SignatureAsync(ProvenancePost post, CancellationToken ct)
    {
        // Canonicalized once, whatever happens to the key set: the digest is a fact about the
        // document this client re-derived, and it stays reportable when the keys do not arrive.
        var unkeyed = SignatureCheck.Verify(post, []);

        var author = post.Provenance.Author;
        if (string.IsNullOrEmpty(author))
            return (Check.CouldNotCheck(
                "the provenance envelope names no author, so no key set can be fetched"), unkeyed.PrefixedDigest, null);

        var fetched = await _forum.GetJwksAsync(author, ct).ConfigureAwait(false);
        if (!fetched.TryGetValue(out var keys, out var refusal))
            return (Check.CouldNotCheck(
                $"the author's key set could not be fetched ({Check.Quote(refusal!.Error.Type)}): {refusal.Summary}. "
                + "This is a fault reaching the keys, not a statement about the signature."), unkeyed.PrefixedDigest, null);

        if (keys.IsDefaultOrEmpty)
            return (Check.CouldNotCheck($"the Forum published no keys at all for {Check.Quote(author)}"), unkeyed.PrefixedDigest, null);

        var verdict = SignatureCheck.Verify(post, keys);
        var signingKey = keys.FirstOrDefault(k => string.Equals(k.Kid, verdict.Kid, StringComparison.Ordinal));
        return (verdict.Verified
            ? Check.Verified(verdict.Detail + $" (kid={Check.Quote(verdict.Kid)})")
            : Check.Failed(verdict.Detail), verdict.PrefixedDigest, signingKey);
    }

    /// <summary>
    /// R6.54's check (errata G16): the key that signed the post, as the log records it, found where
    /// the key set says the log binds it, proven under the same signed head as the post, and before
    /// it.
    ///
    /// <para><b>The key set only says where to look.</b> A Forum that names the wrong leaf, or none,
    /// cannot make this pass: the leaf is recomputed from the entry the log serves, proven to the
    /// head this client verified, and the post's signature, as the log holds it, is checked under the
    /// key that leaf carries. What a lying key set can do is make the check impossible, by naming no
    /// leaf, which is reported as could-not-be-checked, or make it fail, by naming a leaf that binds
    /// something else.</para>
    ///
    /// <para><b>Absent is not failed, and this client's absences are its own.</b> It fetches nothing
    /// for this check without a head it verified, since nothing fetched could be proven under one;
    /// and a document the Forum serves that does not parse as its route's is one this client did not
    /// receive, as R6.52's other lines already treat it -- a truncated body and a proxy's page parse
    /// no better than a forgery, and R6.52 exists so that a network fault never reads as an attack.
    /// Everything this client does hold is checked before any absence is reported: the post's own
    /// record before the key set is consulted; each proof held to the signed head before its entry's
    /// type, identity or order is read; and a proof in hand held to the head before its entry's
    /// absence is reported, so a proof the head does not commit to is failed whatever else is
    /// absent.</para>
    /// </summary>
    private async Task<Check> KeyBindingAsync(
        ProvenancePost post, Anchor anchor, PostInclusion inclusion, Check signature, ForumJwk? signingKey, CancellationToken ct)
    {
        // No head, or one that did not verify: the inclusion line's verdict, for the same reason.
        if (anchor.Head is not { } head) return anchor.Verdict;

        // The post's author, kid and signature are read from the log's own record of it, which the
        // inclusion check fetched. Where it could not, neither can this check -- unless what the
        // inclusion check did hold already failed, as a proof the head does not commit to does,
        // which fails this check too.
        if (inclusion.Entry is not { } postEntry || inclusion.Proof is not { } postProof)
            return inclusion.Check is { Outcome: CheckOutcome.Failed }
                ? inclusion.Check
                : Check.CouldNotCheck(
                    "the post's own log entry and proof were not in hand under the signed head (the "
                    + "inclusion line says why), and this check reads the post's author, kid and "
                    + "signature from them");

        var record = ActaCheck.PostOfRecord(post, postEntry, postProof, head);
        if (record.Outcome is not CheckOutcome.Verified) return record;

        // No key resolved from the key set at all: say why the binding cannot be looked for, and not
        // that the key set names no leaf, which would describe a key set that may never have arrived.
        if (signingKey is null)
            return Check.CouldNotCheck(signature.Outcome is CheckOutcome.CouldNotCheck
                ? "the author's key set did not arrive, or held no keys (the signature line says which), so the key's binding cannot be looked for"
                : "no key in the author's key set answers to the kid the served signature names (the signature line says why), so the key's binding cannot be looked for");

        if (signingKey.LogIndex is not { } keyIndex)
            return Check.CouldNotCheck(
                "the author's key set names no log leaf for the key this post names, so the key's binding cannot be found (R6.54)");

        var proof = await _forum.GetInclusionProofAsync(keyIndex, head.TreeSize, ct).ConfigureAwait(false);
        if (!proof.TryGetValue(out var keyProof, out var proofRefusal))
            return Check.CouldNotCheck(Invariant(
                $"a proof for the key's binding at leaf {keyIndex} could not be fetched: {proofRefusal!.Summary}"));

        // The proof in hand is held to the head before the entry's absence is reported: a proof the
        // head does not commit to is the Forum contradicting its own head, failed whatever else is
        // absent (R6.54), and the comparison needs nothing the missing entry would supply.
        var entry = await _forum.GetLogEntryAsync(keyIndex, ct).ConfigureAwait(false);
        if (!entry.TryGetValue(out var keyEntry, out var entryRefusal))
            return ActaCheck.HeadCovers(head, keyProof!.TreeSize, keyProof.RootHash) is { Outcome: CheckOutcome.Failed } keyOffHead
                ? keyOffHead
                : Check.CouldNotCheck(Invariant(
                    $"the key's binding entry at leaf {keyIndex} could not be fetched: {entryRefusal!.Summary}"));

        return ActaCheck.KeyBinding(post, postEntry, postProof, keyEntry!, keyProof!, head);
    }

    /// <summary>
    /// The signed head to anchor against, with its signature checked here rather than taken from the
    /// response's <c>signature_valid</c> -- which is the Forum re-verifying itself.
    /// </summary>
    private async Task<Anchor> AnchorAsync(CancellationToken ct)
    {
        var fetched = await _forum.GetLogHeadAsync(ct).ConfigureAwait(false);
        if (!fetched.TryGetValue(out var head, out var refusal))
        {
            return new Anchor(null, Check.CouldNotCheck(refusal!.Kind is RefusalKind.NotFound
                ? "this log has published no signed head yet, so there is nothing to anchor a proof "
                  + "to. The Forum holds no log key (R11.7); an operator publishes heads with "
                  + "'curia-operator sign-head'."
                : $"the signed head could not be fetched: {refusal.Summary}"));
        }

        var keys = await _forum.GetLogJwksAsync(ct).ConfigureAwait(false);
        if (!keys.TryGetValue(out var published, out var keysRefusal))
            return new Anchor(null, Check.CouldNotCheck($"the log's key set could not be fetched: {keysRefusal!.Summary}"));

        var verified = ActaCheck.HeadSignature(head!, published);
        return new Anchor(verified.Outcome is CheckOutcome.Verified ? head : null, verified);
    }

    /// <summary>
    /// R6.52's second check. The proof is re-requested against the head's own size when the one the
    /// post carried is against a different one -- which is the normal case rather than an anomaly:
    /// a post accepted since the operator last signed gets a proof against the whole log, with
    /// <c>head_signed: false</c>, and treating that as a failure would report an attack every time a
    /// signing schedule fell behind.
    /// </summary>
    private async Task<PostInclusion> InclusionAsync(ProvenancePost post, Anchor anchor, CancellationToken ct)
    {
        if (post.LogIndex is not { } logIndex || post.InclusionProof is null)
            return new(Check.CouldNotCheck(
                "this Forum served the post with no log index or no inclusion proof, so there is "
                + "nothing to check it against"));

        // A head that FAILED to verify is a failure of this check, not an absence of it. Something
        // signed that head and it was not this log's published key, which is a claim about the
        // material rather than about this client's reach -- and reporting it as "could not be
        // checked" would under-report an attack, the same collapse R6.52 forbids running the other
        // way. Only a head that could not be reached, or a log with none, is an absence.
        if (anchor.Head is not { } head) return new(anchor.Verdict);

        if (logIndex >= head.TreeSize)
            return new(Check.CouldNotCheck(
                Invariant($"leaf {logIndex} is not covered by the signed head at tree size {head.TreeSize}.")
                + " The post is in the log and no published head commits to it yet; a later head will."));

        var proof = post.InclusionProof;
        if (proof.TreeSize != head.TreeSize)
        {
            var reproven = await _forum.GetInclusionProofAsync(logIndex, head.TreeSize, ct).ConfigureAwait(false);
            if (!reproven.TryGetValue(out var against, out var refusal))
                return new(Check.CouldNotCheck(Invariant(
                    $"the post's proof is against tree size {proof.TreeSize} and the signed head covers {head.TreeSize}; a proof against the head's size could not be fetched: {refusal!.Summary}")));

            proof = against;
        }

        // The proof in hand, at the head's size, is held to the head before the entry's absence is
        // reported, as R6.54 holds the key's: a root the head does not sign is failed whatever else
        // is absent.
        var entry = await _forum.GetLogEntryAsync(logIndex, ct).ConfigureAwait(false);
        if (!entry.TryGetValue(out var leaf, out var entryRefusal))
            return ActaCheck.HeadCovers(head, proof!.TreeSize, proof.RootHash) is { Outcome: CheckOutcome.Failed } postOffHead
                ? new(postOffHead)
                : new(Check.CouldNotCheck(
                    $"the log entry the leaf is computed from could not be fetched: {entryRefusal!.Summary}"));

        // Handed on whatever the verdict: R6.54's check reads the post from this entry, and derives
        // this verdict again before it reads anything else.
        var included = ActaCheck.Inclusion(leaf!, proof!, post);
        if (included.Outcome is not CheckOutcome.Verified) return new(included, leaf, proof);

        var covers = ActaCheck.HeadCovers(head, proof!.TreeSize, proof.RootHash);
        return new(covers.Outcome is CheckOutcome.Verified
            ? Check.Verified($"{included.Detail}, and {covers.Detail}")
            : covers, leaf, proof);
    }

    /// <summary>
    /// R6.53's third check, and the only one that can detect a fork.
    ///
    /// <para><b>The retained head is replaced only after a proof verifies</b>, and never after one
    /// fails: the retained head is the only evidence that the log equivocated, and refreshing on the
    /// failure is how a client that detected a fork forgets it.</para>
    ///
    /// <para><b>A served head older than the retained one is not automatically an attack.</b> A
    /// replica can lag. So the proof is requested in the direction it exists -- from the smaller
    /// size to the larger -- and a log that still extends the older head is consistent, merely
    /// stale, and the retained head stands.</para>
    /// </summary>
    private async Task<Check> ConsistencyAsync(Anchor anchor, CancellationToken ct)
    {
        if (anchor.Head is not { } head) return anchor.Verdict;

        // A head this client cannot read is not the same as one it never had. R6.53 permits
        // replacing a retained head only after a consistency proof verifies, and a file that is
        // present and unreadable may be a retained head an attacker has damaged -- overwriting it
        // is precisely the "client that detected a fork forgets it" outcome, arrived at through
        // corruption rather than through a failed proof. So only the absence of a file is a first
        // read; anything else is reported and the file is left alone.
        if (_heads.State(_forum.Forum) is HeadStore.RetainedHead.Unreadable)
            return Check.CouldNotCheck(
                $"a head is retained for {Check.Quote(_forum.Forum.GetLeftPart(UriPartial.Authority))} and could "
                + "not be read. It is left exactly as it is: replacing it would discard the only "
                + "evidence this client has of what the log looked like before (R6.53). Inspect "
                + $"{Check.Quote(_heads.DirectoryFor(_forum.Forum))} by hand.");

        var retained = _heads.Read(_forum.Forum);
        if (retained is null)
        {
            _heads.Write(_forum.Forum, head);
            return Check.CouldNotCheck(Invariant(
                $"this client had retained no earlier head for {Check.Quote(_forum.Forum.GetLeftPart(UriPartial.Authority))}, so there was nothing to compare against. The head at tree size {head.TreeSize} is now retained, and the next verification will check the log against it (R6.53)."));
        }

        if (retained.TreeSize == head.TreeSize)
        {
            return string.Equals(retained.RootHash, head.RootHash, StringComparison.Ordinal)
                ? Check.Verified(Invariant(
                    $"the served head is the one this client retained, at tree size {head.TreeSize}"))
                : Check.Failed(Invariant(
                    $"two different roots at tree size {head.TreeSize}: this client retained {Check.Quote(retained.RootHash)} and the Forum now serves {Check.Quote(head.RootHash)}. The log has equivocated (R6.24). The retained head is kept."));
        }

        var (from, to) = retained.TreeSize < head.TreeSize ? (retained, head) : (head, retained);

        var fetched = await _forum.GetConsistencyProofAsync(from.TreeSize, to.TreeSize, ct).ConfigureAwait(false);
        if (!fetched.TryGetValue(out var proof, out var refusal))
            return Check.CouldNotCheck(Invariant(
                $"a consistency proof from {from.TreeSize} to {to.TreeSize} could not be fetched: {refusal!.Summary}. The retained head is kept."));

        var consistent = ActaCheck.Consistency(from, to, proof!);
        if (consistent.Outcome is not CheckOutcome.Verified) return consistent;

        if (retained.TreeSize < head.TreeSize)
        {
            _heads.Write(_forum.Forum, head);
            return Check.Verified(consistent.Detail + "; the newer head is now the retained one");
        }

        return Check.Verified(
            consistent.Detail
            + ". The Forum served an older head than this client retains -- a lagging replica rather "
            + "than a rewrite -- so the retained head stands.");
    }

    /// <summary>
    /// Invariant formatting for the numbers in these messages. Sizes and indices are the same digits
    /// in every culture, but CS-9's sibling rule applies: the formatting is stated rather than
    /// inherited from whatever the host happens to be running under.
    /// </summary>
    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);

    /// <summary>The head a proof will be anchored to, and why there is none when there is none.</summary>
    private readonly record struct Anchor(SignedHeadDocument? Head, Check Verdict);

    /// <summary>R6.52's second check, and the post's own entry and proof it ran over, when it had both.</summary>
    private readonly record struct PostInclusion(
        Check Check, LogEntryDocument? Entry = null, InclusionProofDocument? Proof = null);
}
