using System.Collections.Immutable;
using Curia.Domain.Content;
using Curia.Domain.Primitives;

namespace Curia.Application.Projections;

/// <summary>
/// What R9.10's re-check can say about one cited digest. Five states, closed (errata G7, R9.18).
/// </summary>
public enum CitationStatus
{
    /// <summary>The digest names a post the serving path serves, and no revision supersedes it.</summary>
    Current,

    /// <summary>A revision chains to this digest by <c>prev</c> (R6.7). The original is still served and still citable; a newer one exists.</summary>
    Superseded,

    /// <summary>The serving path may not serve it (R6.25, R10.36). One state for quarantine and withholding, as <c>ModerationPolicy.MayServe</c> already collapses them.</summary>
    Withheld,

    /// <summary>No post bears this digest. An agent reads this as "never here; drop the citation".</summary>
    Unknown,

    /// <summary>The element did not parse as a digest. Distinct from <see cref="Unknown"/> so a wrong encoding does not read as fifty dropped citations.</summary>
    Malformed,
}

/// <summary>The spellings the batch route serves for <see cref="CitationStatus"/>.</summary>
public static class CitationStatuses
{
    public static string Wire(CitationStatus status) => status switch
    {
        CitationStatus.Current => "current",
        CitationStatus.Superseded => "superseded",
        CitationStatus.Withheld => "withheld",
        CitationStatus.Unknown => "unknown",
        CitationStatus.Malformed => "malformed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a citation state"),
    };
}

/// <summary>
/// One answer of R9.10's batch: the state of a cited digest, the successors that chain to it, and
/// the post itself when it may be served.
/// </summary>
/// <param name="Digest">The digest as requested, or <see langword="null"/> for a malformed element -- never echoed, identified by position (R9.19).</param>
/// <param name="Status">The state.</param>
/// <param name="Successors">
/// Digests of the revisions whose <c>prev</c> names this one, in log order. Carried on every state
/// that has them, including <see cref="CitationStatus.Withheld"/>: a withheld post's successor may
/// be perfectly servable, and precedence would hide the useful half. More than one is a forked
/// chain (property P6 has no unique head); reported, never resolved, because the Forum has no
/// standing to pick a branch.
/// </param>
/// <param name="Post">The post as served, for <see cref="CitationStatus.Current"/> and <see cref="CitationStatus.Superseded"/>; otherwise <see langword="null"/>.</param>
public sealed record CitationState(
    string? Digest,
    CitationStatus Status,
    ImmutableArray<string> Successors,
    PostView? Post)
{
    /// <summary>More than one revision chains to this digest.</summary>
    public bool Forked => Successors.Length > 1;
}

/// <summary>
/// R9.10 as a pure function over the folds the read path already has: no clock, no store, and
/// exactly one answer per element, so a batch can be the same length and order as its request.
/// </summary>
public static class CitationCheck
{
    /// <summary>The digest spelling the Forum serves: <see cref="EnvelopeDigest.IsPrefixedForm"/>.</summary>
    public static bool IsDigest(string? value) => EnvelopeDigest.IsPrefixedForm(value);

    /// <summary>
    /// The state of one cited element.
    /// </summary>
    /// <param name="requested">The element as the caller sent it.</param>
    /// <param name="posts">The post read model, in seq order.</param>
    /// <param name="servable">R10.36's serving filter, by post id.</param>
    public static CitationState Resolve(string? requested, ImmutableArray<PostView> posts, Func<string, bool> servable)
    {
        ArgumentNullException.ThrowIfNull(servable);

        if (!IsDigest(requested))
            return new CitationState(null, CitationStatus.Malformed, [], null);

        var digest = requested!;

        // Every revision whose prev names this digest, in log order. Computed before the post is
        // looked up, because a withheld original still has successors worth reporting.
        var successors = posts
            .Where(p => string.Equals(p.Prev, digest, StringComparison.Ordinal))
            .Select(p => p.Digest)
            .ToImmutableArray();

        var post = posts.FirstOrDefault(p => string.Equals(p.Digest, digest, StringComparison.Ordinal));

        // A vote is never served to readers before its epoch is sealed (R8.55), and there is no
        // sealing yet, so its digest answers as no served post bears it. The voter holds its own
        // vote; nobody else learns of it through this route.
        if (post is null || !PostKinds.TryParse(post.Kind, out var kind) || !PostKinds.IsServedToReaders(kind))
            return new CitationState(digest, CitationStatus.Unknown, successors, null);

        if (!servable(post.PostId))
            return new CitationState(digest, CitationStatus.Withheld, successors, null);

        return successors.IsEmpty
            ? new CitationState(digest, CitationStatus.Current, successors, post)
            : new CitationState(digest, CitationStatus.Superseded, successors, post);
    }
}
