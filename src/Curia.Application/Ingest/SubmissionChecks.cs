using System.Collections.Immutable;
using Curia.Application.Projections;
using Curia.Domain;
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

        var served = CorrectionIndex.SuccessorsOf(posts, original, facts.Servable).FirstOrDefault(successor => successor.PostId is not null);
        return served is { PostId: { } successorPostId }
            ? IngestErrors.AlreadyRevised(successorPostId)
            : null;
    }

    /// <summary>
    /// R8.55: at most one vote stands from an agent for a target. A verification report may be superseded by the same agent's later one (R8.56); a vote may not be
    /// recast, because a vote that could be changed after the tally is visible is the thing epoch sealing exists to stop. The caller asks only for a vote.
    /// </summary>
    public static Error? AlreadyVoted(LogFacts facts, string author, string target)
    {
        ArgumentNullException.ThrowIfNull(facts);

        return facts.Posts.Any(p => string.Equals(p.Author, author, StringComparison.Ordinal)
                && facts.Servable(p.PostId)
                && string.Equals(VerificationProjector.TargetOf(p), target, StringComparison.Ordinal))
            ? VerificationErrors.AlreadyVoted(target)
            : null;
    }
}
