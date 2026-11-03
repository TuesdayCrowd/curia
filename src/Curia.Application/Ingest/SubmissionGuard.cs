using System.Collections.Immutable;
using Curia.Application.Projections;
using Curia.Application.Ports;
using Curia.Canon;
using Curia.Domain;
using Curia.Domain.Content;
using Curia.Domain.Primitives;

namespace Curia.Application.Ingest;

/// <summary>
/// R8.86 (errata G57): the guard a post's append carries. Built from the facts the route's checks ran on, so the position is those facts' own and there is no
/// argument to get wrong (finding 5): <see cref="AppendGuard.After"/> is <see cref="LogFacts.Position"/>.
///
/// <para>Three legs, each by <see cref="SubmissionChecks"/>' own function (trap 96), over what was appended since the route read: the digest leg (every kind) over the
/// delta alone; a revision's served-successor leg over the read's candidates (the posts whose <c>prev</c> is the original's digest, served or not, found when
/// <see cref="For"/> is called) followed by a view of each revision of the delta by the original's author; and a vote's one-per-target leg over the read's candidates
/// (the author's votes for the target, served or not, likewise) followed by a view of each vote of the delta by the submitting author. "Served" is read after the delta's
/// moderation records (errata G57, rulings 31 and 49). Neither leg reads a post of the read under the lock, copies the read's posts or builds a view of a post it
/// cannot use: <see cref="PostProjector.Extend(ImmutableArray{PostView}, IReadOnlyList{AppendedEvent}, Func{string, string, bool})"/> reads an event's author and kind first and
/// builds nothing for the rest, so the work under the lock is the delta's events read once and the submitter's own revisions or votes among them. The facts of a revision or a vote are
/// folded when <see cref="For"/> is called, outside the log's lock; a kind that checks only a digest builds no fold at all.</para>
///
/// <para>A <see cref="GuardVerdict.Refuse"/> carries the error the route's own check returns, so the append answers it as the route would have.</para>
/// </summary>
public static class SubmissionGuard
{
    /// <summary>The guard for <paramref name="submission"/>, decided on <paramref name="facts"/>.</summary>
    public static AppendGuard For(LogFacts facts, VerifiedSubmission submission)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(submission);

        var digest = Digests.Sha256(submission.Canonical).ToPrefixed();
        var envelope = submission.Envelope;

        if (envelope.Kind is PostKind.Revision && envelope.Prev is { Length: > 0 } prev)
        {
            return ForRevision(facts, digest, prev);
        }

        if (envelope.Kind is PostKind.Vote && envelope.Target is { } target)
        {
            return ForVote(facts, digest, submission.AuthorAgentId.Value, target);
        }

        return new AppendGuard(facts.Position, delta => Verdict(RecordedIn(delta, digest)));
    }

    /// <summary>
    /// The successor leg on its own, by the facts, the digest of the revision and the digest it revises. The original and the candidates (the read's posts whose <c>prev</c> is
    /// the original's digest, served or not) are found when this is called, outside the log's lock; the function builds a view of the delta's revisions by the original's author
    /// and no other post, and asks <see cref="SubmissionChecks.StandingSuccessor"/> of the candidates followed by those views, with "served" read after the delta's moderation.
    /// An original that is not among the facts (the route has refused that already) leaves nothing a refusal could be made from, and the function proceeds.
    /// </summary>
    internal static AppendGuard ForRevision(LogFacts facts, string digest, string prev)
    {
        facts.Force();
        var original = facts.Posts.FirstOrDefault(p => string.Equals(p.Digest, prev, StringComparison.Ordinal));
        if (original is null)
            return new AppendGuard(facts.Position, delta => Verdict(RecordedIn(delta, digest)));

        var candidates = SubmissionChecks.SuccessorCandidates(facts, original);
        var author = original.Author;
        var revision = PostKinds.Wire(PostKind.Revision);
        Func<string, string, bool> keep = (postAuthor, kind) =>
            string.Equals(postAuthor, author, StringComparison.Ordinal) && string.Equals(kind, revision, StringComparison.Ordinal);
        return new AppendGuard(facts.Position, delta =>
            Verdict(RecordedIn(delta, digest) ?? SubmissionChecks.StandingSuccessor(
                candidates.Concat(PostProjector.Extend(ImmutableArray<PostView>.Empty, delta, keep)),
                original,
                facts.ServableAfter(delta))));
    }

    /// <summary>The vote leg on its own, by the facts, the digest of the vote, its author and its target (the guard <see cref="For"/> builds for a vote; a probe asks it for any author and target).</summary>
    internal static AppendGuard ForVote(LogFacts facts, string digest, string author, string target)
    {
        facts.Force();
        var candidates = SubmissionChecks.VotesFor(facts, author, target);
        var vote = PostKinds.Wire(PostKind.Vote);
        Func<string, string, bool> keep = (postAuthor, kind) =>
            string.Equals(postAuthor, author, StringComparison.Ordinal) && string.Equals(kind, vote, StringComparison.Ordinal);
        return new AppendGuard(facts.Position, delta =>
            Verdict(RecordedIn(delta, digest) ?? SubmissionChecks.StandingVote(
                candidates.Concat(PostProjector.Extend(ImmutableArray<PostView>.Empty, delta, keep)),
                facts.ServableAfter(delta),
                author,
                target)));
    }

    private static GuardVerdict Verdict(Error? refusal) => refusal is null ? GuardVerdict.Proceed : GuardVerdict.Refuse(refusal);

    private static Error? RecordedIn(IReadOnlyList<AppendedEvent> delta, string digest) =>
        SubmissionChecks.AlreadyRecorded(delta, digest) is { } postId ? IngestErrors.AlreadyRecorded(postId) : null;
}
