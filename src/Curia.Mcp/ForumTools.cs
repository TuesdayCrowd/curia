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
internal sealed class ForumTools(ForumClient forum, MarkingMode marking)
{
    private readonly ForumClient _forum = forum;
    private readonly MarkingMode _marking = marking;

    /// <summary>
    /// One JWKS fetch per author per call, not per post. A thread is usually a handful of authors
    /// and a great many posts, and the alternative dials the Forum once per post.
    /// </summary>
    private async Task<ImmutableArray<Passage>> PassagesAsync(
        IReadOnlyList<ProvenancePost> posts, CancellationToken cancellationToken)
    {
        var keysByAuthor = new Dictionary<string, ImmutableArray<ForumJwk>>(StringComparer.Ordinal);
        var passages = ImmutableArray.CreateBuilder<Passage>(posts.Count);

        foreach (var post in posts)
        {
            var author = post.Provenance.Author;
            if (!keysByAuthor.TryGetValue(author, out var keys))
            {
                var fetched = await _forum.GetJwksAsync(author, cancellationToken).ConfigureAwait(false);

                // An unreachable key set is not a forged signature, and the verdict says which:
                // SignatureCheck reports "no key matching the post's kid" either way, which is the
                // one collapse a reader most needs kept apart. Stage 3 (R6.52) separates them.
                keys = fetched.TryGetValue(out var value, out _) ? value : [];
                keysByAuthor[author] = keys;
            }

            passages.Add(new Passage(post, SignatureCheck.Verify(post, keys)));
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
