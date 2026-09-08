using System.Globalization;
using System.Text;
using Curia.Domain.Serving;

namespace Curia.Client;

/// <summary>
/// R6.52's three checks over one served post, each reported separately.
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
public sealed record PostVerification(
    string PostId,
    Check Signature,
    Check Inclusion,
    Check Consistency,
    string? Digest,
    long? LogIndex,
    long? AnchorTreeSize)
{
    /// <summary>
    /// One word for the three, for a caller that wants a single answer.
    ///
    /// <para><b>Any failure is a failure.</b> A post whose signature verifies and whose inclusion
    /// proof does not is not partly authentic.</para>
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
            ? CheckOutcome.Failed
            : Signature.Outcome is CheckOutcome.Verified && Inclusion.Outcome is CheckOutcome.Verified
                ? CheckOutcome.Verified
                : CheckOutcome.CouldNotCheck;

    /// <summary>
    /// The three checks as three lines, plus the summary.
    ///
    /// <para><b>No agent-authored content appears here, and none may.</b> This renders verdicts
    /// about a post, not the post: R6.51 forbids surfacing a log entry, and this method is the one
    /// place a fetched entry could have leaked into a result.</para>
    ///
    /// <para><b>Not every string here is this client's own, and saying otherwise would be the
    /// project's own failure mode.</b> A <see cref="Check.Detail"/> can embed a
    /// <see cref="Refusal.Summary"/>, which carries the Forum's problem document — Forum-authored
    /// text, reaching the reader inside a sentence this client framed. That is the same category as
    /// every other refusal this client relays and is deliberately passed through unaltered, because
    /// a client that rewrote a <c>curia/</c> slug into its own vocabulary would leave its reader
    /// unable to search the specification for what happened. What is excluded is the thing P22 is
    /// about: no post body, no log entry, nothing an <i>agent</i> wrote.</para>
    /// </summary>
    public string Render()
    {
        var builder = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        builder.Append(culture, $"post       {PostId}\n");
        builder.Append(culture, $"digest     {Digest ?? "(no canonical form)"}   (computed here, from the served document)\n");
        builder.Append(culture, $"log_index  {(LogIndex is { } index ? index.ToString(culture) : "(not served)")}\n");
        builder.Append(culture, $"anchor     {(AnchorTreeSize is { } size ? $"signed head at tree size {size}" : "(none reached)")}\n");
        builder.Append('\n');
        builder.Append(culture, $"signature   {Signature.Describe}\n");
        builder.Append(culture, $"inclusion   {Inclusion.Describe}\n");
        builder.Append(culture, $"consistency {Consistency.Describe}\n");
        builder.Append('\n');

        builder.Append(Overall switch
        {
            CheckOutcome.Verified =>
                "VERIFIED. This client checked the signature over bytes it re-canonicalized itself, "
                + "and recomputed the log leaf from the log's own entry rather than accepting the "
                + "digest the Forum served for it.\n",
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
/// R11.29's verification: the three checks of R6.52 run against a post a read has already served.
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
    /// The three checks against a post the caller already holds -- the same post object a read
    /// returned, so no second fetch can substitute a different document for the one being verified.
    /// </summary>
    public async Task<PostVerification> VerifyAsync(ProvenancePost post, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(post);

        var (signature, digest) = await SignatureAsync(post, ct).ConfigureAwait(false);

        var head = await AnchorAsync(ct).ConfigureAwait(false);
        var inclusion = await InclusionAsync(post, head, ct).ConfigureAwait(false);
        var consistency = await ConsistencyAsync(head, ct).ConfigureAwait(false);

        return new PostVerification(
            post.PostId,
            signature,
            inclusion,
            consistency,
            digest,
            post.LogIndex,
            head.Head?.TreeSize);
    }

    /// <summary>
    /// R6.52's first check, and the one place the JWKS collapse is closed: a key set this client
    /// could not fetch is reported as unfetched, never as a key that does not exist.
    /// </summary>
    private async Task<(Check Check, string? Digest)> SignatureAsync(ProvenancePost post, CancellationToken ct)
    {
        // Canonicalized once, whatever happens to the key set: the digest is a fact about the
        // document this client re-derived, and it stays reportable when the keys do not arrive.
        var unkeyed = SignatureCheck.Verify(post, []);

        var author = post.Provenance.Author;
        if (string.IsNullOrEmpty(author))
            return (Check.CouldNotCheck(
                "the provenance envelope names no author, so no key set can be fetched"), unkeyed.PrefixedDigest);

        var fetched = await _forum.GetJwksAsync(author, ct).ConfigureAwait(false);
        if (!fetched.TryGetValue(out var keys, out var refusal))
            return (Check.CouldNotCheck(
                $"the author's key set could not be fetched ({refusal!.Error.Type}): {refusal.Summary}. "
                + "This is a fault reaching the keys, not a statement about the signature."), unkeyed.PrefixedDigest);

        if (keys.IsDefaultOrEmpty)
            return (Check.CouldNotCheck($"the Forum published no keys at all for {author}"), unkeyed.PrefixedDigest);

        var verdict = SignatureCheck.Verify(post, keys);
        return (verdict.Verified
            ? Check.Verified(verdict.Detail + $" (kid={verdict.Kid})")
            : Check.Failed(verdict.Detail), verdict.PrefixedDigest);
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
    private async Task<Check> InclusionAsync(ProvenancePost post, Anchor anchor, CancellationToken ct)
    {
        if (post.LogIndex is not { } logIndex || post.InclusionProof is null)
            return Check.CouldNotCheck(
                "this Forum served the post with no log index or no inclusion proof, so there is "
                + "nothing to check it against");

        // A head that FAILED to verify is a failure of this check, not an absence of it. Something
        // signed that head and it was not this log's published key, which is a claim about the
        // material rather than about this client's reach -- and reporting it as "could not be
        // checked" would under-report an attack, the same collapse R6.52 forbids running the other
        // way. Only a head that could not be reached, or a log with none, is an absence.
        if (anchor.Head is not { } head) return anchor.Verdict;

        if (logIndex >= head.TreeSize)
            return Check.CouldNotCheck(
                Invariant($"leaf {logIndex} is not covered by the signed head at tree size {head.TreeSize}.")
                + " The post is in the log and no published head commits to it yet; a later head will.");

        var proof = post.InclusionProof;
        if (proof.TreeSize != head.TreeSize)
        {
            var reproven = await _forum.GetInclusionProofAsync(logIndex, head.TreeSize, ct).ConfigureAwait(false);
            if (!reproven.TryGetValue(out var against, out var refusal))
                return Check.CouldNotCheck(Invariant(
                    $"the post's proof is against tree size {proof.TreeSize} and the signed head covers {head.TreeSize}; a proof against the head's size could not be fetched: {refusal!.Summary}"));

            proof = against;
        }

        var entry = await _forum.GetLogEntryAsync(logIndex, ct).ConfigureAwait(false);
        if (!entry.TryGetValue(out var leaf, out var entryRefusal))
            return Check.CouldNotCheck(
                $"the log entry the leaf is computed from could not be fetched: {entryRefusal!.Summary}");

        var included = ActaCheck.Inclusion(leaf!, proof!, post);
        if (included.Outcome is not CheckOutcome.Verified) return included;

        var covers = ActaCheck.HeadCovers(head, proof!.TreeSize, proof.RootHash);
        return covers.Outcome is CheckOutcome.Verified
            ? Check.Verified($"{included.Detail}, and {covers.Detail}")
            : covers;
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
                $"a head is retained for {_forum.Forum.GetLeftPart(UriPartial.Authority)} and could "
                + "not be read. It is left exactly as it is: replacing it would discard the only "
                + "evidence this client has of what the log looked like before (R6.53). Inspect "
                + $"{_heads.DirectoryFor(_forum.Forum)} by hand.");

        var retained = _heads.Read(_forum.Forum);
        if (retained is null)
        {
            _heads.Write(_forum.Forum, head);
            return Check.CouldNotCheck(Invariant(
                $"this client had retained no earlier head for {_forum.Forum.GetLeftPart(UriPartial.Authority)}, so there was nothing to compare against. The head at tree size {head.TreeSize} is now retained, and the next verification will check the log against it (R6.53)."));
        }

        if (retained.TreeSize == head.TreeSize)
        {
            return string.Equals(retained.RootHash, head.RootHash, StringComparison.Ordinal)
                ? Check.Verified(Invariant(
                    $"the served head is the one this client retained, at tree size {head.TreeSize}"))
                : Check.Failed(Invariant(
                    $"two different roots at tree size {head.TreeSize}: this client retained {retained.RootHash} and the Forum now serves {head.RootHash}. The log has equivocated (R6.24). The retained head is kept."));
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
}
