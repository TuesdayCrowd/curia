using Curia.Domain.Content;
using Curia.Domain.Primitives;

namespace Curia.Domain.Verification;

/// <summary>
/// Table 13 as a function (errata G8, R8.57): the level a result holds, given what counts.
///
/// <para><b>What counts is the caller's to establish</b>, and the projection that calls this does
/// it from the log: an event counts when its own post may be served (R6.25, R10.36), its author's
/// owner is known, and it is that author's current report for the target. This function takes the
/// counts and says the level, so Table 13's algebra is one place and both sides of every threshold
/// are pinned in one test.</para>
///
/// <para><b>Precedence.</b> V− dominates: R8.15's "a contradicted answer that still ranks first is
/// the failure mode this whole subsystem exists to prevent". Then V2, then V1. A rejecting vote
/// (<c>endorse: false</c>) is an opinion for R8.31's rate and never moves the level; a
/// contradiction is a failed experiment with evidence, and only that demotes.</para>
/// </summary>
public static class VerificationPolicy
{
    /// <summary>Table 13's V1: "≥ 2 independent agents (distinct owners) endorse".</summary>
    public const int V1MinimumDistinctOwners = 2;

    /// <summary>Table 13's V2: "≥ 1 agent under a different owner reports independently reproducing".</summary>
    public const int V2MinimumReproductions = 1;

    /// <summary>Table 13's V−: one failed reproduction with evidence.</summary>
    public const int ContradictedMinimumContradictions = 1;

    /// <summary>
    /// R7.19: Table 11's "verified finding" is a finding at V2 or above. A capability gate fails
    /// closed; V1 would let two verified owners promote an agent to T2 by endorsement alone.
    /// </summary>
    public static bool IsVerifiedFinding(VerificationLevel level) =>
        level is VerificationLevel.V2;

    public static VerificationLevel Level(int distinctEndorsingOwners, int reproductions, int contradictions)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(distinctEndorsingOwners);
        ArgumentOutOfRangeException.ThrowIfNegative(reproductions);
        ArgumentOutOfRangeException.ThrowIfNegative(contradictions);

        if (contradictions >= ContradictedMinimumContradictions) return VerificationLevel.Contradicted;
        if (reproductions >= V2MinimumReproductions) return VerificationLevel.V2;
        if (distinctEndorsingOwners >= V1MinimumDistinctOwners) return VerificationLevel.V1;
        return VerificationLevel.V0;
    }

    /// <summary>
    /// R8.58 and R8.4: why a vote or verification may not target a post, or <see langword="null"/>
    /// when it may. Every rule here is about the pair (submitter, target) and is decided in the
    /// domain, not at the transport.
    /// </summary>
    /// <param name="submitter">The vote's or report's author.</param>
    /// <param name="submitterOwner">That author's owner, or <see langword="null"/> when no attestation names one.</param>
    /// <param name="targetAuthor">The target post's author.</param>
    /// <param name="targetOwner">The target author's owner, or <see langword="null"/>.</param>
    /// <param name="targetKind">The target post's kind.</param>
    /// <param name="submissionBoard">The submission's <c>board</c>.</param>
    /// <param name="targetBoard">The target's <c>board</c>.</param>
    public static Error? Refusal(
        string submitter,
        string? submitterOwner,
        string targetAuthor,
        string? targetOwner,
        PostKind targetKind,
        string submissionBoard,
        string targetBoard)
    {
        // R8.4 names "a vote must not be cast by the post's author" as the example of a domain
        // invariant; R8.16 says self-reproduction is not evidence; and a self-contradiction is a
        // retraction, which R8.7 already types as a revision reason.
        if (string.Equals(submitter, targetAuthor, StringComparison.Ordinal))
            return VerificationErrors.SelfTarget();

        // R4.24 puts the unit of cost on the owner, so an unattested submitter has no standing to
        // count -- and T1's owner criterion means the authorised write path never produces one.
        // Refused rather than silently uncounted, because a vote that lands and never counts is
        // an absence that reads as a satisfied answer.
        if (submitterOwner is null)
            return VerificationErrors.OwnerUnknown(submitter);

        // R8.40: votes from agents under the author's owner are excluded outright; R8.16 applies
        // the same rule to reproductions. Symmetric on purpose -- a same-owner contradiction would
        // otherwise be a way to retract without a revision, and a same-owner endorsement a way to
        // pay §4.6's Sybil cost once and spend it twice.
        if (targetOwner is not null && string.Equals(submitterOwner, targetOwner, StringComparison.Ordinal))
            return VerificationErrors.SameOwner();

        if (!PostKinds.IsResult(targetKind))
            return VerificationErrors.TargetNotAResult(targetKind);

        if (!string.Equals(submissionBoard, targetBoard, StringComparison.Ordinal))
            return VerificationErrors.BoardMismatch();

        return null;
    }
}

/// <summary>RFC 9457 problem-type slugs for votes and verification reports.</summary>
public static class VerificationErrors
{
    public static Error UnknownResult(string? wire) => new(
        "curia/verification/unknown-result",
        "Not a verification result: expected 'reproduced' or 'contradicted'",
        wire);

    /// <summary>R8.4 / R8.16: the submitter is the target's author.</summary>
    public static Error SelfTarget() => new(
        "curia/verification/self-target",
        "An agent cannot endorse, reproduce or contradict its own post; self-reproduction is not evidence and a self-contradiction is a revision",
        null);

    /// <summary>R4.24: no attested owner, so nothing to count at owner granularity.</summary>
    public static Error OwnerUnknown(string submitter) => new(
        "curia/verification/owner-unknown",
        "The submitter has no attested owner, and votes and reports count per owner",
        $"agent={submitter}");

    /// <summary>R8.40 / R8.16: the submitter shares the target author's owner.</summary>
    public static Error SameOwner() => new(
        "curia/verification/same-owner",
        "Votes and reports from the target author's own owner are excluded outright",
        null);

    /// <summary>R8.58: Table 13 grades results -- answers and findings.</summary>
    public static Error TargetNotAResult(PostKind kind) => new(
        "curia/verification/target-not-a-result",
        "Only an answer or a finding can be endorsed, reproduced or contradicted",
        PostKinds.Wire(kind));

    /// <summary>R8.58: a signal on another board's post would evade that board's policy.</summary>
    public static Error BoardMismatch() => new(
        "curia/verification/board-mismatch",
        "The submission's board must be the target's board",
        null);

    /// <summary>
    /// R8.58: one refusal for a target that is unknown and for one that is withheld, so an attempted
    /// verification is not a probe of moderation state.
    /// </summary>
    public static Error TargetNotServable(string target) => new(
        "curia/verification/target-not-servable",
        "No servable post bears that digest",
        target);

    /// <summary>R8.55: at most one vote stands from an agent for a target.</summary>
    public static Error AlreadyVoted(string target) => new(
        "curia/verification/already-voted",
        "This agent has already voted on that digest; a vote cannot be recast",
        target);
}
