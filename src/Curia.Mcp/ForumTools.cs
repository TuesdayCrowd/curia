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

    /// <summary>
    /// R11.41: the parenthesis of <see cref="Passage.AcceptedStatement"/> (the Forum's word, what no signature covers, what an
    /// acceptance checks), so the resolution line and the passage line that names the same post cannot drift apart.
    /// </summary>
    private static readonly string AcceptedBasis = Passage.AcceptedStatement[Passage.AcceptedStatement.IndexOf('(', StringComparison.Ordinal)..];

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

    /// <summary>
    /// One JWKS fetch per author per call, not per post. A thread is usually a handful of authors
    /// and a great many posts, and the alternative dials the Forum once per post.
    /// </summary>
    private async Task<ImmutableArray<Passage>> PassagesAsync(
        IReadOnlyList<ProvenancePost> posts, CancellationToken cancellationToken)
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

            Remember(post);

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

        var read = await _forum.GetPostAsync(postId, _marking, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var post, out var refusal)) throw Refused(refusal!);

        // R11.38: the thread is the root's, found by following parent upward. The route serves the subtree under any
        // id, so the walk is this adapter's. Ancestors are kept for their Parent alone; nothing is rendered from them.
        var top = post!;
        string? unreadableParent = null;
        var repeats = false;
        var visited = new HashSet<string>(StringComparer.Ordinal) { top.PostId };
        var steps = 0;
        while (top.Parent is { Length: > 0 } parent && steps < MaxAncestorSteps)
        {
            steps++;
            var up = await _forum.GetPostAsync(parent, _marking, cancellationToken).ConfigureAwait(false);
            if (!up.TryGetValue(out var ancestor, out _))
            {
                unreadableParent = parent;
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

        var frame = new FrameBuilder();
        if (!threadRead)
        {
            var why = !thread.IsOk ? failure!.Summary
                : members.IsDefaultOrEmpty ? "the Forum served no posts for it"
                : "the thread the Forum served did not include the post";
            frame.Line($"NOT READ the thread beginning at {top.PostId} could not be read, so the post alone is shown: {why}");
        }
        else if (unreadableParent is null && !repeats && cutAt is null)
        {
            frame.Line($"thread root={top.PostId} posts={members.Length}");

            // R11.41: the line after the root line says how the thread stands. The Forum's word, from the members it
            // served, never a conclusion drawn from their content; the ids are the Forum's, so display literals. Written
            // only here: where the shown thread is cut or unread, the accepted answer may be above what is shown and
            // "none" would be a guess. More than one is a Forum fault (the fold keeps one per thread); each is listed.
            var acceptedIds = members.Where(m => m.Accepted).Select(m => m.PostId).ToArray();
            if (acceptedIds.Length == 0)
                frame.Line($"accepted none {new OwnText("(the Forum served no post of this thread as accepted)")}");
            else
                frame.Line($"accepted {new OwnText(string.Join(", ", acceptedIds.Select(id => "answer=" + DisplayLiteral.Of(id))))} {new OwnText(AcceptedBasis)}");
        }

        if (unreadableParent is not null)
            frame.Line($"CUT the parent {unreadableParent} of post {top.PostId} could not be read, so the thread shown begins at {top.PostId} and is not the whole thread");
        else if (repeats)
            frame.Line($"CUT the parent chain repeats at a post already read, so it has no root; the thread shown begins at {top.PostId} and is not the whole thread");
        else if (cutAt is not null)
            frame.Line($"CUT the upward walk stopped after {MaxAncestorSteps} steps without reaching a root, so the thread shown begins at {top.PostId}, whose parent {cutAt} was not followed");

        var preamble = frame.ToString();

        var posts = threadRead ? members : [post];

        return Rendered(await PassagesAsync(posts, cancellationToken).ConfigureAwait(false), preamble);
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

        // R11.45: a pin that is present and blank is refused; absent is no pin.
        if (ArgumentGuards.PresentValue(expectedDigest, nameof(expectedDigest)) is { } refusedPin) return refusedPin;

        // R11.29: the document this session's read put in the model's context, when there is one.
        var read = _served.TryGetValue(postId, out var remembered);

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
    /// R11.49's <c>curia_recheck</c>. <b>Seam, Task 0 of PR 2b:</b> it answers an empty text block and is registered nowhere, so
    /// a probe that calls it fails by assertion and not by compile error; Task 2 replaces it.
    /// </summary>
    internal Task<CallToolResult> RecheckAsync(string[] digests, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(digests);
        ArgumentNullException.ThrowIfNull(_forum);

        return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = string.Empty }] });
    }

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
        if (_served.ContainsKey(post.PostId)) return;

        _served[post.PostId] = post;
        _servedOrder.Enqueue(post.PostId);

        while (_servedOrder.Count > RememberedPosts) _served.Remove(_servedOrder.Dequeue());
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
            .Append($"source={page.Floor.Source} applies_to={new OwnText(string.Join(",", page.Floor.AppliesTo.Select(DisplayLiteral.Of)))} ")
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
