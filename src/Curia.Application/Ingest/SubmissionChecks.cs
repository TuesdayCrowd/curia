using System.Collections.Immutable;
using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;
using Curia.Domain.Verification;

namespace Curia.Application.Ingest;

/// <summary>
/// R8.77, R8.78, R8.55 (errata G45, G57): the three checks a post's submission makes against the log, as pure functions that return an <see cref="Error"/>
/// and no framework result, so the route (on the log it read) and the append's guard (on the events appended since) call the same function (trap 96). They
/// take facts or events, read no clock and no store, and the route answers each <see cref="Error"/> with the status its type carries.
/// </summary>
public static class SubmissionChecks
{
    /// <summary>R8.78: the id of the first stored post (served or not) whose digest is <paramref name="digest"/>, in log order; <see langword="null"/> when none.</summary>
    public static string? AlreadyRecorded(IReadOnlyList<AppendedEvent> events, string digest) =>
        PostProjector.FirstWithDigest(events, digest)?.PostId;

    /// <summary>
    /// R8.77, R8.78: what a revision is refused with on <paramref name="facts"/>, or <see langword="null"/> to go on. The same bytes are
    /// <c>already-recorded</c> (stored, served or not); a revision of a post the Forum already serves a revision of is <c>already-revised</c>, naming the first
    /// served successor in log order. "Served" is <see cref="CorrectionIndex"/>'s alone, so this check, a post's <c>superseded_by</c> and the batch's
    /// <c>successors</c> cannot answer differently (R9.32); a successor the Forum does not serve does not count. A log in which the original cannot be found is
    /// <c>curia/ingest/revision-check-unavailable</c>: AUTHORIZE found it in a log that has only grown, so its absence is a log this Forum cannot read now,
    /// never a pass.
    /// </summary>
    public static Error? RevisionRefusal(LogFacts facts, string prevDigest, string digest)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var posts = facts.Posts;
        if (posts.FirstOrDefault(p => string.Equals(p.Digest, digest, StringComparison.Ordinal)) is { } recorded)
            return IngestErrors.AlreadyRecorded(recorded.PostId);

        var original = posts.FirstOrDefault(p => string.Equals(p.Digest, prevDigest, StringComparison.Ordinal));
        if (original is null)
            return IngestErrors.RevisionCheckUnavailable();

        return StandingSuccessor(posts, original, facts.Servable);
    }

    /// <summary>
    /// R8.87 (errata G61): why a revision's <paramref name="board"/> may not be the board of the post whose digest is <paramref name="prevDigest"/>, or <see langword="null"/>
    /// to go on. Made once by the route on the facts it folded, under the revision gate; a stored post's board never changes, so the append's guard does not ask it. A log in
    /// which the original cannot be found is <c>curia/ingest/revision-check-unavailable</c>, as <see cref="RevisionRefusal"/> has it: never a pass. The route cannot reach that branch:
    /// <see cref="RevisionRefusal"/> has already answered <c>503</c> for an original that is not in the facts it folded, before this is called; it is kept so a caller of this function with other
    /// facts fails closed (ruling 40).
    /// </summary>
    public static Error? RevisionBoardRefusal(LogFacts facts, string prevDigest, string board)
    {
        ArgumentNullException.ThrowIfNull(facts);

        var original = facts.Posts.FirstOrDefault(p => string.Equals(p.Digest, prevDigest, StringComparison.Ordinal));
        return original is null
            ? IngestErrors.RevisionCheckUnavailable()
            : ParentPolicy.ReplyBoard(PostKind.Revision, board, original.PostId, original.Board);
    }

    /// <summary>
    /// The one definition of a standing successor (R8.77, trap 96): <c>already-revised</c>, naming the first post of <paramref name="posts"/> (in the order given) that
    /// <see cref="CorrectionIndex"/> says corrects <paramref name="original"/> and <paramref name="servable"/> says the Forum serves; <see langword="null"/> when none.
    /// The route asks it of the facts of its read; the append's guard asks it of the read's candidates (the posts whose <c>prev</c> is the original's digest, served or not)
    /// followed by the delta's revisions by the original's author, with "served" read after the delta's moderation (errata G57, ruling 49), so the first in log order is the
    /// same post on both paths.
    /// </summary>
    public static Error? StandingSuccessor(IEnumerable<PostView> posts, PostView original, Func<string, bool> servable)
    {
        ArgumentNullException.ThrowIfNull(posts);
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(servable);

        var served = CorrectionIndex.SuccessorsOf(posts, original, servable).FirstOrDefault(successor => successor.PostId is not null);
        return served is { PostId: { } successorPostId }
            ? IngestErrors.AlreadyRevised(successorPostId)
            : null;
    }

    /// <summary>
    /// The posts of <paramref name="facts"/> whose <c>prev</c> is <paramref name="original"/>'s digest, served or not: the successors a delta can make stand again (a restore) and
    /// no others. Made outside the log's lock, where the guard is built (errata G57, ruling 49).
    /// </summary>
    public static ImmutableArray<PostView> SuccessorCandidates(LogFacts facts, PostView original)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(original);

        return [.. facts.Posts.Where(p => string.Equals(p.Prev, original.Digest, StringComparison.Ordinal))];
    }

    /// <summary>
    /// R8.55: at most one vote stands from an agent for a target. A verification report may be superseded by the same agent's later one (R8.56); a vote may not be
    /// recast, because a vote that could be changed after the tally is visible is the thing epoch sealing exists to stop. The caller asks only for a vote.
    /// </summary>
    public static Error? AlreadyVoted(LogFacts facts, string author, string target)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return StandingVote(facts.Posts, facts.Servable, author, target);
    }

    /// <summary>
    /// The one definition of a standing vote (R8.55, trap 96): a post of <paramref name="posts"/> by <paramref name="author"/> that names <paramref name="target"/> and that
    /// <paramref name="servable"/> says the Forum serves. The route asks it of the facts of its read; the append's guard asks it of the read's candidates
    /// (<see cref="VotesFor"/>) followed by the delta's posts, with "served" read after the delta's moderation (errata G57, ruling 31).
    /// </summary>
    public static Error? StandingVote(IEnumerable<PostView> posts, Func<string, bool> servable, string author, string target)
    {
        ArgumentNullException.ThrowIfNull(posts);
        ArgumentNullException.ThrowIfNull(servable);

        return posts.Any(p => IsVoteFor(p, author, target) && servable(p.PostId))
            ? VerificationErrors.AlreadyVoted(target)
            : null;
    }

    /// <summary>
    /// The posts of <paramref name="facts"/> by <paramref name="author"/> that name <paramref name="target"/>, served or not: the votes a delta can make stand again (a restore) and no
    /// others. Made outside the log's lock, where the guard is built (errata G57, ruling 31).
    /// </summary>
    public static ImmutableArray<PostView> VotesFor(LogFacts facts, string author, string target)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return [.. facts.Posts.Where(p => IsVoteFor(p, author, target))];
    }

    private static bool IsVoteFor(PostView post, string author, string target) =>
        string.Equals(post.Author, author, StringComparison.Ordinal)
        && string.Equals(VerificationProjector.TargetOf(post), target, StringComparison.Ordinal);
}
