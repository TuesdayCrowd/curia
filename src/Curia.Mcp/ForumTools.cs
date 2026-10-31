using System.Collections.Immutable;
using System.Globalization;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Serving;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Curia.Mcp;

/// <summary>
/// The adapter's tools (R11.17): <c>curia_search</c> and <c>curia_read</c>, both anonymous, and
/// <c>curia_verify</c> here; <c>curia_ask</c>, <c>curia_answer</c>, <c>curia_comment</c>,
/// <c>curia_revise</c>, <c>curia_resolve</c> and <c>curia_flag</c> in <c>WriteTools.cs</c>.
///
/// <para>Every method is a call into <see cref="ForumClient"/>. R11.16 (revised) forbids this
/// adapter deciding a question the application layer decides or re-deriving a rule it already
/// applies, and having no path to the Forum except through the reference client is how that stays
/// true by construction rather than by review — including the URL, because two surfaces building
/// the same request independently is how they come to disagree about what it means.</para>
///
/// <para><b>Marking is requested, never applied.</b> R11.28: marking is a serving-boundary
/// transformation (R6.16), and the boundary between the Forum's words and an author's is drawn by
/// the party that knows where it is. An adapter marking locally would draw it from its own parse,
/// and the parse is what an attacker attacks. So the mode travels to the Forum and the
/// already-delimited, already-marked span comes back.</para>
///
/// <para><b>The write tools live in <c>WriteTools.cs</c></b>, a second part of this class, so that a
/// duplicate refusal's answers go through the same rendering, the same per-author key fetch and the
/// same served-post memory as a read: an answer <c>curia_ask</c> hands back is one
/// <c>curia_verify</c> can then check as the document the model was shown (R11.29).</para>
///
/// <para><b>What a result says of itself (errata G27).</b> A search states its cursor, or that it is the last
/// page, and the terms of each hit when asked (R11.39); <c>curia_read</c> returns the whole thread, its first
/// block the adapter's own naming the root or where the shown thread begins (R11.38); a call whose arguments do
/// not match the schema never reaches these methods (R11.37). A <c>curia_verify</c> whose pin names another document answers an
/// error and no verdict (R11.42), and a value the schema admits and a tool cannot act on (a blank identifier, a null item)
/// is an <c>isError</c> result beginning <c>NOT SENT</c> from <c>ArgumentGuards</c> (R11.43, errata G29).</para>
///
/// <para><b>How a thread stands (errata G28).</b> <c>curia_read</c>'s first block follows the root line with an
/// <c>accepted</c> line, the post the Forum serves as accepted or that it served none, and says nothing of it where the
/// shown thread is cut or not read (R11.41); a passage prints an acceptance and a count of the items it could not read
/// through <c>Passage</c> (R10.79, R8.66).</para>
/// </summary>
internal sealed partial class ForumTools(
    ForumClient forum, MarkingMode marking, HeadStore heads, ForumWriter? writer = null)
{
    /// <summary>
    /// How many served posts this session keeps so <c>curia_verify</c> can verify the document the
    /// model actually read. A stdio session's reach is what one conversation reads; the cap exists
    /// so a long one cannot grow without bound, and the oldest entry goes first.
    /// </summary>
    private const int RememberedPosts = 256;

    /// <summary>R11.38: how many ancestors <c>curia_read</c> follows before it says the thread is cut.</summary>
    private const int MaxAncestorSteps = 64;

    private readonly ForumClient _forum = forum;
    private readonly MarkingMode _marking = marking;
    private readonly ForumWriter? _writer = writer;

    /// <summary>Whether an identity is configured, and so whether the write tools are offered at all.</summary>
    internal bool CanWrite => _writer is not null;
    private readonly PostVerifier _verifier = new(forum, heads);

    /// <summary>
    /// The posts this session has served, by id, in the order they were served.
    ///
    /// <para><b>R11.29's subject, held rather than re-fetched.</b> "<c>curia_verify</c> SHALL take
    /// as its subject a post a read has already served", because "a tool that verifies an artifact
    /// the caller never read confirms something true about a document nobody is acting on". A tool
    /// that only takes an id re-asks the Forum, and the Forum may answer differently: serve one
    /// validly signed document to <c>curia_read</c> and another, also validly signed by the same
    /// agent under the same id, to <c>curia_verify</c>, and the verdict is true of a document the
    /// model never saw. Demonstrated against a decoy Forum before this existed.</para>
    /// </summary>
    private readonly Dictionary<string, ProvenancePost> _served = new(StringComparer.Ordinal);

    private readonly Queue<string> _servedOrder = new();

    /// <summary>Guards <see cref="_served"/> and <see cref="_servedOrder"/>: a host may issue parallel tool calls on one session (R11.61's case), and a dictionary is not safe under two writers.</summary>
    private readonly object _servedLock = new();

    /// <summary>
    /// One JWKS fetch per author per call, not per post. A thread is usually a handful of authors
    /// and a great many posts, and the alternative dials the Forum once per post.
    /// </summary>
    private async Task<ImmutableArray<Passage>> PassagesAsync(
        IReadOnlyList<ProvenancePost> posts, CancellationToken cancellationToken, bool remember = true)
    {
        var keysByAuthor = new Dictionary<string, ImmutableArray<ForumJwk>>(StringComparer.Ordinal);
        var passages = ImmutableArray.CreateBuilder<Passage>(posts.Count);

        // An unreachable key set is not a forged signature. Keeping the refusal rather than
        // flattening it to an empty array is what lets the verdict say which (R6.52): an empty array
        // reaches SelectKey as "no key matching the post's kid", so a host being down was reported
        // to the model as a statement about the author.
        var refusalByAuthor = new Dictionary<string, Refusal>(StringComparer.Ordinal);

        foreach (var post in posts)
        {
            var author = post.Provenance.Author;
            if (!keysByAuthor.ContainsKey(author) && !refusalByAuthor.ContainsKey(author))
            {
                var fetched = await _forum.GetJwksAsync(author, cancellationToken).ConfigureAwait(false);
                if (fetched.TryGetValue(out var value, out var refusal)) keysByAuthor[author] = value;
                else refusalByAuthor[author] = refusal!;
            }

            if (remember) Remember(post);

            passages.Add(new Passage(
                post,
                refusalByAuthor.TryGetValue(author, out var unreachable)
                    ? SignatureCheck.Unreachable(post, unreachable)
                    : SignatureCheck.Verify(post, keysByAuthor[author])));
        }

        return passages.ToImmutable();
    }

    /// <summary>
    /// R11.17's <c>curia_read</c>: "Fetch post + thread + provenance". The provenance envelope
    /// reaches the model as the Forum served it (R11.18, "unmodified"), never reconstructed.
    /// </summary>
    internal async Task<CallToolResult> ReadAsync(string postId, CancellationToken cancellationToken)
    {
        if (ArgumentGuards.Identifier(postId, nameof(postId)) is { } refused) return refused;
        if (ArgumentGuards.StorableIdentifier(postId, nameof(postId)) is { } unstorable) return unstorable;

        var read = await _forum.GetPostAsync(postId, _marking, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var post, out var refusal)) throw Refused(refusal!);

        // R11.38: the thread is the root's, found by following parent upward. The route serves the subtree under any
        // id, so the walk is this adapter's. Ancestors are kept for their Parent alone; nothing is rendered from them.
        var top = post!;
        string? unreadableParent = null;
        var parentWithheld = false;
        var repeats = false;
        var visited = new HashSet<string>(StringComparer.Ordinal) { top.PostId };
        var steps = 0;
        while (top.Parent is { Length: > 0 } parent && steps < MaxAncestorSteps)
        {
            steps++;
            var up = await _forum.GetPostAsync(parent, _marking, cancellationToken).ConfigureAwait(false);
            if (!up.TryGetValue(out var ancestor, out var upRefusal))
            {
                unreadableParent = parent;

                // R11.54: only the Forum's own curia/posts/withheld makes the line say withheld; every other cause stays "could not be read".
                parentWithheld = IsWithheldPost(upRefusal!);
                break;
            }

            // A chain that returns to a post already read has no root to reach: say so rather than spend the bound.
            if (!visited.Add(ancestor!.PostId))
            {
                repeats = true;
                break;
            }

            top = ancestor;
        }

        // Neither an unreadable ancestor, a repeat nor the bound reaches a root: the thread shown begins at `top` and says so.
        var cutAt = top.Parent is { Length: > 0 } unfollowed && !repeats ? unfollowed : null;

        // A thread that cannot be fetched leaves the post itself readable rather than failing the call: the post is
        // what was asked for, and the first block says the rest was not read. A thread that does not include the post
        // is not the post's thread, so it is treated the same way.
        var thread = await _forum.GetThreadAsync(top.PostId, _marking, cancellationToken).ConfigureAwait(false);
        var members = thread.TryGetValue(out var served, out var failure) ? served! : default;
        var threadRead = !members.IsDefaultOrEmpty && members.Any(m => string.Equals(m.PostId, post.PostId, StringComparison.Ordinal));

        var posts = threadRead ? members : [post];
        var passages = await PassagesAsync(posts, cancellationToken).ConfigureAwait(false);

        // R11.66 (errata G50): for a post of kind revision each failure line and the CUT line add where the Forum places a revision, made from the post's own
        // passage: its signed prev is named only where that passage verified, so the note is built after the passages and never from a document that did not.
        OwnText? placement = null;
        if (string.Equals(post.Kind, "revision", StringComparison.Ordinal))
        {
            var own = passages.First(p => string.Equals(p.Post.PostId, post.PostId, StringComparison.Ordinal));
            placement = Supersession.RevisionPlacementNote(own.Outcome is CheckOutcome.Verified ? own.Envelope?.Prev : null, own.Outcome is CheckOutcome.Verified);
        }

        var note = placement is { } placed ? new OwnText("; " + placed.Text) : new OwnText(string.Empty);

        var frame = new FrameBuilder();
        if (!threadRead)
        {
            // R11.66: two causes, two words. A thread the Forum could not be asked for or served nothing for was not read; one it served, which does not list
            // the post, was read, and is not the post's thread.
            if (!thread.IsOk || members.IsDefaultOrEmpty)
            {
                var why = !thread.IsOk ? failure!.Summary : "the Forum served no posts for it";
                frame.Line($"NOT READ the thread beginning at {top.PostId} could not be read, so the post alone is shown: {why}{note}");
            }
            else
            {
                frame.Line($"NOT LISTED the thread beginning at {top.PostId} was read and does not list the post, so the post alone is shown{note}");
            }
        }
        else if (unreadableParent is null && !repeats && cutAt is null)
        {
            frame.Line($"thread root={top.PostId} posts={members.Length}");

            // R11.41, R11.60: the line after the root line says how the thread stands, from the thread root's own word (not the members'
            // flags); the ids are the Forum's, so display literals. Written only here: where the shown thread is cut or unread, the accepted
            // answer may be above what is shown and "none" would be a guess.
            AcceptedLine(frame, top);

            // R11.62: and, from the root's own superseded_by and the members the thread holds, what became of the root and the text that stands.
            // Beneath the accepted line and only here, for the same reason; silence for an unrevised root, as the passage's line is.
            if (Supersession.RootLine(top, members) is { } revision) frame.Line($"{revision}");
        }

        if (unreadableParent is not null)
            frame.Line($"CUT the parent {unreadableParent} of post {top.PostId} {(parentWithheld ? "is withheld" : "could not be read")}, so the thread shown begins at {top.PostId} and is not the whole thread{note}");
        else if (repeats)
            frame.Line($"CUT the parent chain repeats at a post already read, so it has no root; the thread shown begins at {top.PostId} and is not the whole thread{note}");
        else if (cutAt is not null)
            frame.Line($"CUT the upward walk stopped after {MaxAncestorSteps} steps without reaching a root, so the thread shown begins at {top.PostId}, whose parent {cutAt} was not followed{note}");

        var preamble = frame.ToString();

        return Rendered(passages, preamble);
    }

    /// <summary>
    /// R11.41, R11.60 (errata G44): the first block's <c>accepted</c> line, stated from the thread root's own word and never from the members' flags
    /// (a post is accepted relative to its own thread, and a thread listing may hold a post another thread's asker accepted). A question's
    /// <c>accepted_answer</c> is present (the post by kind and id, or by kind and digest where the Forum does not serve it), <c>null</c> (none), or absent
    /// (<c>NOT STATED</c>); a root that is not a question has no member by design and says none. R10.82: where the member lists revisions the line says so
    /// from that list, and an absent list is <c>NOT STATED</c>, never silence. The ids and digests are the Forum's, so display literals.
    /// </summary>
    private static void AcceptedLine(FrameBuilder frame, ProvenancePost root)
    {
        const string NoneWords = "(the Forum served no post of this thread as accepted)";
        if (!string.Equals(root.Kind, "question", StringComparison.Ordinal))
        {
            frame.Line($"accepted none {new OwnText(NoneWords)}");
            return;
        }

        var member = root.Provenance.AcceptedAnswer;
        if (member is null)
        {
            frame.Line($"accepted {new OwnText(AnswerStanding.NotStatedWords)}");
            return;
        }

        if (member.IsNone)
        {
            frame.Line($"accepted none {new OwnText(NoneWords)}");
            return;
        }

        var noun = member.Kind ?? "post";
        var named = member.PostId is { } id
            ? $"{noun}={DisplayLiteral.Of(id)}"
            : $"the {noun} with digest {DisplayLiteral.Of(member.Digest!)} {Supersession.DigestOnlyMark}";
        var kindNote = member.Kind is null
            ? " (kind NOT STATED: the Forum served the accepted post without saying whether it is an answer or a revision)"
            : string.Empty;
        var statement = member.SupersededBy.IsDefault
            ? $" {Supersession.NotStatedShort} (the Forum served {named} without a word on whether it has been revised, so do not take it as unrevised)"
            : Supersession.IsSuperseded(member.SupersededBy)
                ? " " + AnswerStanding.RevisionStatement(named, member.SupersededBy, "curia_resolve").Text
                : string.Empty;
        frame.Line($"accepted {new OwnText(named + kindNote + statement)} {new OwnText(AnswerStanding.Basis)}");
    }

    /// <summary>
    /// R11.17's <c>curia_verify</c>: "Verify a signature/inclusion proof locally", and R11.29's
    /// three outcomes reported distinctly.
    ///
    /// <para><b>Verdicts only.</b> The verification fetches R6.46's log entry, because the leaf
    /// cannot be rebuilt from anything a read serves — but an entry carries an author's body with no
    /// provenance envelope, no delimiters, no marking and no moderation filter (R6.51), so returning
    /// one would deliver the single representation property P22 does not cover straight into this
    /// model's context, and would re-serve withheld content while doing it. Nothing that comes back
    /// from this method contains any part of a post. That is asserted, not intended:
    /// <c>PropertyP22ToolResultTests</c> runs the tool against a Forum whose entry carries a marker
    /// and fails if the marker appears in the result.</para>
    ///
    /// <para><b>One content block, and it is this adapter's own words.</b> The read tools wrap each
    /// post in its own <c>EmbeddedResourceBlock</c> because R10.56 requires one addressable item per
    /// post; there are no posts here, so there is one block and no boundary to draw.</para>
    /// </summary>
    /// <param name="expectedDigest">
    /// The <c>digest</c> a read printed for this post, when the caller holds one. Supplied, it pins
    /// the subject across sessions: a Forum that serves a different document under the same id is
    /// caught even when this session did not perform the read. Omitted, the subject is whatever
    /// this session read, or — failing that — what the Forum serves now, and the result says which.
    /// </param>
    internal async Task<CallToolResult> VerifyAsync(
        string postId, string? expectedDigest = null, CancellationToken cancellationToken = default)
    {
        if (ArgumentGuards.Identifier(postId, nameof(postId)) is { } refused) return refused;
        if (ArgumentGuards.StorableIdentifier(postId, nameof(postId)) is { } unstorable) return unstorable;

        // R11.45: a pin that is present and blank is refused; absent is no pin.
        if (ArgumentGuards.PresentValue(expectedDigest, nameof(expectedDigest)) is { } refusedPin) return refusedPin;

        // R11.52: and a pin that is present is in the published form; any other spelling is refused, never folded, and nothing is compared.
        if (ArgumentGuards.DigestForm(expectedDigest, nameof(expectedDigest)) is { } refusedForm) return refusedForm;

        // R11.29: the document this session's read put in the model's context, when there is one.
        ProvenancePost? remembered;
        bool read;
        lock (_servedLock) read = _served.TryGetValue(postId, out remembered);

        var verification = read
            ? await _verifier.VerifyAsync(remembered!, cancellationToken).ConfigureAwait(false)
            : await FetchedAsync(postId, cancellationToken).ConfigureAwait(false);

        var subject = read
            ? "subject     the document this session's read served, verified as that object rather "
              + "than re-fetched\n"
            : "subject     WARNING: no read in this session served this post, so it was fetched now. "
              + "This verdict is about the document the Forum served a moment ago, which is not "
              + "necessarily the one you are acting on. Pass the digest a read printed to pin it.\n";

        var (pinned, pinFailed) = Pinned(expectedDigest, verification);

        // R11.42: a pin that is not the document answers an error and no verdict. Nothing below the pin line is
        // about the document the caller named, and a result that goes on to render checks and close "VERIFIED."
        // would put that sentence in front of a model that asked about something else.
        if (pinFailed)
            return new CallToolResult { Content = [new TextContentBlock { Text = subject + pinned }], IsError = true };

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = subject + pinned + verification.Render() }],

            // The transport's own failure flag stays false for an unsound post: a post that fails verification is a
            // successful answer to "is this post sound?", and marking it an error would put the one verdict a
            // reader most needs into the channel clients are most likely to retry or discard. The text says FAILED
            // in words that cannot be misread instead. A document that is not the one named (R11.42, above) is a
            // different case: there is no answer to the question asked, so it is an error.
            IsError = false,
        };
    }

    /// <summary>
    /// R11.49's <c>curia_recheck</c>: the digests an agent holds (a citation is a digest, R8.57), turned into a standing and a post id
    /// by R9.10's batch, one line per digest in the order sent (R9.34, <see cref="CitationLines"/>, which the CLI verb composes from too).
    ///
    /// <para><b>Ids and standings, one block, this adapter's own words.</b> There are no posts here, so there is one block and no
    /// boundary to draw (R10.56); nothing an author wrote can reach it, since a line is built from the state, the ids, the successors and
    /// the disputed mark and never from a title, a tag, a body or a span (P22).</para>
    ///
    /// <para><b>The Forum's word, checked by computation where it can be.</b> A response that answers other than what was sent is an
    /// error and no standing. A post the Forum names has its digest recomputed from the canonical form it served, and a mismatch is a
    /// <c>FAILED</c> line with no post id. The result is a successful answer carrying that loud line, as <c>curia_verify</c> answers
    /// for a signature that does not hold: a transport error flag is the channel clients most often retry or discard, and the line says
    /// FAILED in words that cannot be misread. The block ends by saying that every line is the Forum's word as of the call and that
    /// no signature and no inclusion proof was checked, which <c>curia_verify</c> does.</para>
    ///
    /// <para><b>A post named is not a post read.</b> This does not call <c>Remember</c>, so a later <c>curia_verify</c> of a named id says
    /// that no read served it (R11.29): the model's context never held that document. The Forum's cap on a batch is the Forum's own
    /// and is relayed in its refusal; it is not guessed here.</para>
    /// </summary>
    internal async Task<CallToolResult> RecheckAsync(string[] digests, CancellationToken cancellationToken = default)
    {
        if (ArgumentGuards.NotEmpty(digests, nameof(digests)) is { } empty) return empty;
        if (ArgumentGuards.Items(digests, nameof(digests), blankToo: true) is { } refused) return refused;

        var batch = await _forum.BatchAsync(digests, _marking, cancellationToken).ConfigureAwait(false);
        if (!batch.TryGetValue(out var items, out var refusal)) throw Refused(refusal!);

        var report = CitationLines.For(digests, items);
        if (report.Error is { } mismatch)
        {
            // R9.34: an error and no standing, in this client's own words (the counts and a position are the request's, never the Forum's text).
            return new CallToolResult
            {
                Content = [new TextContentBlock { Text = $"{mismatch.Title}: {mismatch.Detail}." }],
                IsError = true,
            };
        }

        var text = string.Join('\n', report.Lines.Select(line => line.Text.Text)) + "\n" + RecheckClosing;
        return new CallToolResult { Content = [new TextContentBlock { Text = text }], IsError = false };
    }

    /// <summary>R11.49: what the last line of a recheck says about the weight of the others.</summary>
    private const string RecheckClosing =
        "Every line above is the Forum's word as of the call; no signature and no inclusion proof was checked here, and curia_verify checks those.";

    /// <summary>The post as the Forum serves it now, for a subject this session has not read.</summary>
    private async Task<PostVerification> FetchedAsync(string postId, CancellationToken cancellationToken)
    {
        var verified = await _verifier.VerifyAsync(postId, _marking, cancellationToken).ConfigureAwait(false);
        if (!verified.TryGetValue(out var verification, out var refusal)) throw Refused(refusal!);

        return verification!;
    }

    /// <summary>
    /// Whether the document verified is the one whose digest the caller named.
    ///
    /// <para>The only defence available when the read happened somewhere this session cannot see —
    /// another session, another client, an earlier conversation. A mismatch is stated in the
    /// strongest terms the result has, because everything below it is true of the wrong
    /// document.</para>
    ///
    /// <para>Composed through the frame, so the digest the caller gave and the one the Forum served
    /// are each a display literal (R10.63): neither is this adapter's words.</para>
    /// </summary>
    private static (string Text, bool Failed) Pinned(string? expectedDigest, PostVerification verification)
    {
        if (string.IsNullOrWhiteSpace(expectedDigest)) return (string.Empty, false);

        var frame = new FrameBuilder();
        if (string.Equals(expectedDigest, verification.Digest, StringComparison.Ordinal))
            return (frame.Line($"pinned      to the digest you supplied, {expectedDigest}").ToString(), false);

        const string Verdict = "No verdict was reached and no check is reported: every one would be about the other document. Read the post again to get the digest it now has, or call without `expectedDigest` to verify what the Forum serves now.";
        return ((verification.Digest is { } served
                ? frame.Line($"pinned      FAILED. You asked about {expectedDigest} and the Forum served {served} under this id. These are different documents. {Verdict}")
                : frame.Line($"pinned      FAILED. You asked about {expectedDigest} and the Forum served no canonical form under this id. {Verdict}"))
            .ToString(), true);
    }

    /// <summary>Keeps the served post, evicting the oldest once the session's cap is reached.</summary>
    private void Remember(ProvenancePost post)
    {
        lock (_servedLock)
        {
            if (_served.ContainsKey(post.PostId)) return;

            _served[post.PostId] = post;
            _servedOrder.Enqueue(post.PostId);

            while (_servedOrder.Count > RememberedPosts) _served.Remove(_servedOrder.Dequeue());
        }
    }

    /// <summary>
    /// R11.17's <c>curia_search</c>, taking R9.25's criteria record. A member the Forum cannot
    /// honour comes back as a refusal naming it and reaches the model as one.
    /// </summary>
    internal async Task<CallToolResult> SearchAsync(
        SearchCriteria? criteria = null, CancellationToken cancellationToken = default)
    {
        criteria ??= new SearchCriteria();

        // R11.43: a null item of an array member is refused by its path before the criteria are read.
        if (ArgumentGuards.Items(criteria.Kinds.IsDefault ? null : criteria.Kinds, "criteria.kinds") is { } refused) return refused;
        if (ArgumentGuards.Items(criteria.Tags.IsDefault ? null : criteria.Tags, "criteria.tags") is { } refusedTags) return refusedTags;

        // R11.45: a criterion that is present and blank is refused by its dotted name; an empty `query` stays valid (R9.25).
        if (ArgumentGuards.PresentValue(criteria.Board, "criteria.board") is { } refusedBoard) return refusedBoard;
        if (ArgumentGuards.PresentValue(criteria.Author, "criteria.author") is { } refusedAuthor) return refusedAuthor;
        if (ArgumentGuards.PresentValue(criteria.MinVerification, "criteria.min_verification") is { } refusedFloor) return refusedFloor;

        // R11.52: the Forum reads a blank cursor as no cursor and answers page one, so a pager that carries its cursor in a variable would
        // loop; the adapter refuses it by name. Absent (null) is no cursor.
        if (ArgumentGuards.PresentValue(criteria.Cursor, "criteria.cursor") is { } refusedCursor) return refusedCursor;

        // R11.57: U+0000 and an unpaired surrogate are refused in every string criterion that reaches the query, by name. Without it the
        // Forum reads the NUL, matches nothing, and an empty result is shown for a filter that could never match.
        if (ArgumentGuards.StorableIdentifier(criteria.Query, "criteria.query") is { } unstorableQuery) return unstorableQuery;
        if (ArgumentGuards.StorableIdentifier(criteria.Board, "criteria.board") is { } unstorableBoard) return unstorableBoard;
        if (ArgumentGuards.StorableIdentifier(criteria.Author, "criteria.author") is { } unstorableAuthor) return unstorableAuthor;
        if (ArgumentGuards.StorableItems(criteria.Tags.IsDefault ? null : criteria.Tags, "criteria.tags") is { } unstorableTags) return unstorableTags;

        // The criterion's own words can come from a post the model read, so they are quoted like any
        // value this adapter did not write (R10.63, errata G17).
        if (!criteria.ToRequest().TryGetValue(out var request, out var invalid))
            throw new McpException(invalid!.Detail is { Length: > 0 } detail
                ? Said($"{invalid.Title}. {detail}")
                : Said($"{invalid.Title}"));

        var found = await _forum.SearchAsync(request!, _marking, cancellationToken).ConfigureAwait(false);
        if (!found.TryGetValue(out var page, out var refusal)) throw Refused(refusal!);

        var posts = page!.Results.Select(r => r.Post).ToArray();
        return Rendered(await PassagesAsync(posts, cancellationToken).ConfigureAwait(false), Floor(page));
    }

    /// <summary>
    /// R10.56: one addressable item per post, each with its own provenance envelope and its own
    /// boundary, so the number of delimited spans never falls below the number of posts. Appendix
    /// L.2's C5 requires a consumer process passages in isolation and then aggregate, and §10.7
    /// records that doing so cut injection attack success from over 90% to roughly 10%. Fusing them
    /// into one span is not a rendering preference — it removes that option downstream, and no
    /// consumer can undo it, because once the passages share a frame nothing recovers which
    /// sentence carried which author's signature.
    ///
    /// <para>Each post becomes an <c>EmbeddedResourceBlock</c> with its own <c>uri</c>, which is
    /// what "separately addressable" means over this transport. The reference client satisfies C5
    /// differently — one string, each passage inside its own delimited span — and G12 records that a
    /// single transport string is not the defect. This is the stronger form because the transport
    /// offers it.</para>
    ///
    /// <para>The <c>uri</c> carries the post id the Forum served, which the model reads beside the
    /// passage, so it is percent-encoded (R10.63, errata G17): a display literal cannot sit in a URI,
    /// and an id holding a line break began a line of what the model read.</para>
    /// </summary>
    private static CallToolResult Rendered(ImmutableArray<Passage> passages, string? preamble = null)
    {
        var content = new List<ContentBlock>(passages.Length + 1);

        // The adapter's own words sit outside every post's boundary, never interleaved with one.
        if (preamble is { Length: > 0 })
            content.Add(new TextContentBlock { Text = preamble });

        foreach (var passage in passages)
        {
            content.Add(new EmbeddedResourceBlock
            {
                Resource = new TextResourceContents
                {
                    Uri = "curia://post/" + Uri.EscapeDataString(passage.Post.PostId),
                    MimeType = "text/plain",
                    Text = passage.Render(),
                },
            });
        }

        return new CallToolResult { Content = content };
    }

    /// <summary>R9.30: a count the Forum served, in the invariant culture, or <c>not stated</c> (the adapter's own words, so unquoted); a missing count is never <c>0</c>.</summary>
    private static OwnText Count(int? served) =>
        new(served is { } count ? count.ToString(CultureInfo.InvariantCulture) : "not stated");

    /// <summary>
    /// R9.33: the request, the clamp and the published default, each as the Forum served it and <c>not stated</c> where it did
    /// not. <c>clamped=false</c> is printed, so a request that was not raised and an older Forum read differently.
    /// </summary>
    private static OwnText ClampWords(RetrievalFloorDocument floor)
    {
        var requested = !floor.RequestedMinVerificationStated ? "not stated" : floor.RequestedMinVerification is { } r ? DisplayLiteral.Of(r) : "none";
        var clamped = floor.Clamped is { } c ? (c ? "true" : "false") : "not stated";
        var published = floor.PublishedDefault is { } p ? DisplayLiteral.Of(p) : "not stated";
        return new($"requested={requested} clamped={clamped} published_default={published}");
    }

    /// <summary>
    /// R9.24 (revised): the floor a search was answered at, the surface whose configuration supplied
    /// it, whether that was the published default, and the kinds it applied to. A floor a caller
    /// cannot read back is one it cannot distinguish from an empty corpus.
    /// </summary>
    private static string Floor(SearchPage page)
    {
        var frame = new FrameBuilder()
            .Append($"surface={page.Floor.Surface} min_verification={page.Floor.MinVerification} ")
            .Append($"source={page.Floor.Source} applies_to={FloorKinds.Render(page.Floor.AppliesTo, ",")} not_applicable_to={FloorKinds.Render(page.Floor.NotApplicableTo, ",")} ")
            .Append($"{ClampWords(page.Floor)} ")
            .Append($"model={page.Model} results={page.Results.Length} ")
            .Append($"excluded_by_floor={Count(page.Floor.ExcludedByFloor)} excluded_disputed={Count(page.Floor.ExcludedDisputed)}");

        // R11.39: what the next call needs. The cursor is the Forum's, so it is a display literal; with none the
        // model is told it is on the last page, which a missing word could not say.
        if (page.NextCursor is { Length: > 0 } cursor)
            frame.Line($" next_cursor={cursor}");
        else
            frame.Line(" no_further_pages (this is the last page)");

        // R9.8, asked for: each hit's terms under its post id, written as the CLI writes them.
        foreach (var hit in page.Results)
        {
            if (hit.Why is not { } why) continue;
            frame.Line($"post_id={hit.Post.PostId}");
            SearchScore.WriteWhy(frame, why);
        }

        return frame.ToString();
    }

    /// <summary>A line or sentence of this adapter's own, with every served value quoted.</summary>
    private static string Said(FrameText text) => text.ToString();

    /// <summary>
    /// A refusal reaches the model as an <see cref="McpException"/>, whose message the SDK delivers
    /// after its prefix "An error occurred invoking '&lt;tool&gt;': ", so the refusal's words are kept;
    /// any other exception is replaced with "An error occurred invoking …" alone. That
    /// replacement would discard the distinction the client exists to preserve — an authorization
    /// refusal is never retryable and a rate-budget one is retryable tomorrow — and hand a model one
    /// undifferentiated failure. <see cref="Refusal.Summary"/> carries the remedy with the reason.
    /// </summary>
    private static McpException Refused(Refusal refusal) => new(refusal.Summary);
}
