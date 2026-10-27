using System.Collections.Immutable;
using Curia.Domain.Content;
using Curia.Domain.Serving;

namespace Curia.Application.Projections;

/// <summary>
/// R9.32 (errata G32): which posts correct another, defined once. R9.28's sentence is the rule: a correction is a post of kind
/// <c>revision</c> whose <c>prev</c> names the original's digest and whose author is the original's author. A comment, or a
/// revision by someone else, that names the digest corrects nothing, and saying otherwise would tell a reader the original
/// stands corrected when no one with standing said so. <see cref="CitationCheck"/> (the batch route's <c>successors</c>) and
/// the <c>provenance.superseded_by</c> member of every served post both ask <see cref="IsCorrection"/>, so the two cannot
/// answer differently.
///
/// <para>The index is built once per request (<see cref="Fold"/>): a board of N posts must not be N scans of the log. It is a
/// pure fold over the post read model, with no clock and no store, as <see cref="AcceptanceProjector"/> is.</para>
/// </summary>
public sealed class CorrectionIndex
{
    private readonly ILookup<string, PostView> _revisionsByPrev;
    private readonly IReadOnlyDictionary<string, PostView> _byDigest;
    private readonly Func<string, bool> _servable;

    private CorrectionIndex(
        ILookup<string, PostView> revisionsByPrev,
        IReadOnlyDictionary<string, PostView> byDigest,
        Func<string, bool> servable)
    {
        _revisionsByPrev = revisionsByPrev;
        _byDigest = byDigest;
        _servable = servable;
    }

    /// <summary>
    /// An index that lists nothing. It exists for the falsification rows and for nothing else: a caller that builds a response
    /// must pass an index it folded, so a forgotten pass-through cannot read as "nothing supersedes this".
    /// </summary>
    public static CorrectionIndex Empty { get; } = new(
        Enumerable.Empty<PostView>().ToLookup(p => p.PostId, StringComparer.Ordinal),
        new Dictionary<string, PostView>(StringComparer.Ordinal),
        static _ => false);

    /// <summary>R9.28's rule: <paramref name="candidate"/> corrects <paramref name="original"/>.</summary>
    public static bool IsCorrection(PostView candidate, PostView original)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(original);

        return string.Equals(candidate.Prev, original.Digest, StringComparison.Ordinal)
            && PostKinds.TryParse(candidate.Kind, out var kind) && kind is PostKind.Revision
            && string.Equals(candidate.Author, original.Author, StringComparison.Ordinal);
    }

    /// <summary>The index of a request's posts.</summary>
    /// <param name="posts">The post read model, in seq order.</param>
    /// <param name="servable">R10.36's serving filter, by post id.</param>
    public static CorrectionIndex Fold(ImmutableArray<PostView> posts, Func<string, bool> servable)
    {
        ArgumentNullException.ThrowIfNull(servable);

        // Keyed by prev alone; the author and kind are decided by IsCorrection in For, so there is one rule and no second copy.
        var byPrev = (posts.IsDefault ? [] : posts)
            .Where(p => p.Prev is not null)
            .ToLookup(p => p.Prev!, StringComparer.Ordinal);
        var byDigest = new Dictionary<string, PostView>(StringComparer.Ordinal);
        foreach (var post in posts.IsDefault ? [] : posts)
        {
            byDigest.TryAdd(post.Digest, post);
        }

        return new CorrectionIndex(byPrev, byDigest, servable);
    }

    /// <summary>
    /// The revisions that supersede <paramref name="original"/>, in log order. One hop, with no head: a fork is both branches,
    /// because the Forum has no standing to pick one (R9.10, property P6). A revision the serving path will not serve is
    /// listed by digest alone: its id is the one thing a withheld post's absence keeps (R10.36).
    /// </summary>
    public ImmutableArray<Successor> For(PostView original)
    {
        ArgumentNullException.ThrowIfNull(original);

        return [.. _revisionsByPrev[original.Digest]
            .Where(p => IsCorrection(p, original))
            .Select(p => new Successor(p.Digest, IsServed(p) ? p.PostId : null))];
    }

    /// <summary>
    /// R9.43's <c>fork</c>: where <paramref name="post"/> is a correction (<see cref="IsCorrection"/>) of the post its
    /// <c>prev</c> names and that post has more than one successor, the other successors, in log order, the post's own excluded;
    /// otherwise empty. It is computed through <see cref="For"/> and <see cref="IsCorrection"/>, not a second filter, so
    /// <c>superseded_by</c>, <c>successors</c> and <c>fork</c> cannot answer differently.
    /// </summary>
    public ImmutableArray<Successor> ForkOf(PostView post)
    {
        ArgumentNullException.ThrowIfNull(post);

        if (post.Prev is null
            || !_byDigest.TryGetValue(post.Prev, out var original)
            || !IsCorrection(post, original))
        {
            return [];
        }

        var successors = For(original);
        return successors.Length < 2
            ? []
            : [.. successors.Where(s => !string.Equals(s.Digest, post.Digest, StringComparison.Ordinal))];
    }

    private bool IsServed(PostView post) =>
        PostKinds.TryParse(post.Kind, out var kind) && PostKinds.IsServedToReaders(kind) && _servable(post.PostId);
}
