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
/// <para>Three legs, each by <see cref="SubmissionChecks"/>' own function (trap 96), over what was appended since the route read: the digest leg (every kind)
/// over the delta alone; a revision's served-successor leg and a vote's one-per-target leg over the route's facts followed by the delta, which is the
/// incremental fold (<see cref="LogFacts.Extend"/>). The facts of a revision or a vote are folded when <see cref="For"/> is called, outside the log's lock; the
/// function folds nothing but the delta, and a kind that checks only a digest builds no fold at all.</para>
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
            facts.Force();
            return new AppendGuard(facts.Position, delta =>
                Verdict(RecordedIn(delta, digest) ?? SubmissionChecks.RevisionRefusal(facts.Extend(delta), prev, digest)));
        }

        if (envelope.Kind is PostKind.Vote && envelope.Target is { } target)
        {
            return ForVote(facts, digest, submission.AuthorAgentId.Value, target);
        }

        return new AppendGuard(facts.Position, delta => Verdict(RecordedIn(delta, digest)));
    }

    /// <summary>The vote leg on its own, by the facts, the digest of the vote, its author and its target (the guard <see cref="For"/> builds for a vote; a probe asks it for any author and target).</summary>
    internal static AppendGuard ForVote(LogFacts facts, string digest, string author, string target)
    {
        facts.Force();
        return new AppendGuard(facts.Position, delta =>
            Verdict(RecordedIn(delta, digest) ?? SubmissionChecks.AlreadyVoted(facts.Extend(delta), author, target)));
    }

    private static GuardVerdict Verdict(Error? refusal) => refusal is null ? GuardVerdict.Proceed : GuardVerdict.Refuse(refusal);

    private static Error? RecordedIn(IReadOnlyList<AppendedEvent> delta, string digest) =>
        SubmissionChecks.AlreadyRecorded(delta, digest) is { } postId ? IngestErrors.AlreadyRecorded(postId) : null;
}
