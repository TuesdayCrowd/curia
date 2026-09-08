using System.Collections.Immutable;
using System.Globalization;
using Curia.Client;
using Curia.Domain.Serving;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Curia.Mcp;

/// <summary>
/// Stage 2's read surface: <c>curia_search</c> and <c>curia_read</c>, both anonymous (R11.17).
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
/// </summary>
internal sealed class ForumTools(ForumClient forum, MarkingMode marking, HeadStore heads)
{
    /// <summary>
    /// How many served posts this session keeps so <c>curia_verify</c> can verify the document the
    /// model actually read. A stdio session's reach is what one conversation reads; the cap exists
    /// so a long one cannot grow without bound, and the oldest entry goes first.
    /// </summary>
    private const int RememberedPosts = 256;

    private readonly ForumClient _forum = forum;
    private readonly MarkingMode _marking = marking;
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
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);

        var read = await _forum.GetPostAsync(postId, _marking, cancellationToken).ConfigureAwait(false);
        if (!read.TryGetValue(out var post, out var refusal)) throw Refused(refusal!);

        // The thread the post sits in, so a reader evaluating an answer can see what it answers.
        // A thread that cannot be fetched leaves the post itself readable rather than failing the
        // call: the post is what was asked for.
        var root = post!.Parent is { Length: > 0 } parent ? parent : post.PostId;
        var thread = await _forum.GetThreadAsync(root, _marking, cancellationToken).ConfigureAwait(false);

        var posts = thread.TryGetValue(out var siblings, out _) && !siblings.IsDefaultOrEmpty
            ? siblings
            : [post];

        return Rendered(await PassagesAsync(posts, cancellationToken).ConfigureAwait(false));
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
        string postId, string? expectedDigest, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(postId);

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

        var pinned = Pinned(expectedDigest, verification);

        return new CallToolResult
        {
            Content = [new TextContentBlock { Text = subject + pinned + verification.Render() }],

            // The transport's own failure flag stays false: a post that fails verification is a
            // successful answer to "is this post sound?", and marking it an error would put the one
            // verdict a reader most needs into the channel clients are most likely to retry or
            // discard. The text says FAILED in words that cannot be misread instead.
            IsError = false,
        };
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
    /// </summary>
    private static string Pinned(string? expectedDigest, PostVerification verification)
    {
        if (string.IsNullOrWhiteSpace(expectedDigest)) return string.Empty;

        return string.Equals(expectedDigest, verification.Digest, StringComparison.Ordinal)
            ? $"pinned      to the digest you supplied, {expectedDigest}\n"
            : $"pinned      FAILED. You asked about {expectedDigest} and the Forum served "
              + $"{verification.Digest ?? "(no canonical form)"} under this id. These are different "
              + "documents. Nothing below is about the one you asked about.\n";
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
        SearchCriteria criteria, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(criteria);

        if (!criteria.ToRequest().TryGetValue(out var request, out var invalid))
            throw new McpException(invalid!.Detail is { Length: > 0 } detail
                ? $"{invalid.Title}. {detail}"
                : invalid.Title);

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
                    Uri = "curia://post/" + passage.Post.PostId,
                    MimeType = "text/plain",
                    Text = passage.Render(),
                },
            });
        }

        return new CallToolResult { Content = content };
    }

    /// <summary>
    /// R9.24 (revised): the floor a search was answered at, the surface whose configuration supplied
    /// it, whether that was the published default, and the kinds it applied to. A floor a caller
    /// cannot read back is one it cannot distinguish from an empty corpus.
    /// </summary>
    private static string Floor(SearchPage page) => string.Create(
        CultureInfo.InvariantCulture,
        $"surface={page.Floor.Surface} min_verification={page.Floor.MinVerification} " +
        $"source={page.Floor.Source} applies_to={string.Join(",", page.Floor.AppliesTo)} " +
        $"model={page.Model} results={page.Results.Length}");

    /// <summary>
    /// A refusal reaches the model as an <see cref="McpException"/>, whose message the SDK forwards
    /// verbatim; any other exception is replaced with "An error occurred invoking …". That
    /// replacement would discard the distinction the client exists to preserve — an authorization
    /// refusal is never retryable and a rate-budget one is retryable tomorrow — and hand a model one
    /// undifferentiated failure. <see cref="Refusal.Summary"/> carries the remedy with the reason.
    /// </summary>
    private static McpException Refused(Refusal refusal) => new(refusal.Summary);
}
