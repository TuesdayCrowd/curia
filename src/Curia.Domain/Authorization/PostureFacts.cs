using Curia.Domain.Credentials;

namespace Curia.Domain.Authorization;

/// <summary>
/// R7.15's observable state, as facts rather than as a tier. Everything here is either recorded in
/// the event log or counted from it -- nothing is a fresh clock read, and nothing is a claim
/// carried in a token.
///
/// <para><b>Why there is no "now" in this type.</b> Table 11's criteria are elapsed-time
/// conditions ("≥ 48 hours", "≥ 30 days at T1"), so the tier genuinely is a function of the current
/// instant. But the projection that produces these facts must not read a clock:
/// <c>AggregateSummaryProjector</c> records why, and it is the same reason here -- a rebuild that
/// consulted "now" would make R11.9's replay drill tautological, since two runs would differ only
/// by when they ran, and a test asserting they agree would pass by the accident of running close
/// together. So the facts are clock-free and the instant is an argument to
/// <see cref="TierPolicy.Evaluate"/>. This is the shape R6.31 already uses for key validity:
/// evaluate <i>at</i> a named instant rather than at whatever time the code happens to run.</para>
///
/// <para><b>Fields §8 does not yet produce.</b> Table 11 counts questions, accepted answers,
/// verified findings and upheld flags, none of which have event types yet -- the content domain is
/// Phase 2's later stages. They are named here rather than omitted because omitting them would
/// make <see cref="TierPolicy"/> look like a complete rendering of Table 11 when it is not, and a
/// silently-incomplete promotion rule is exactly the kind of thing that is discovered in
/// production. A projector that cannot yet populate them leaves them at zero, which denies
/// promotion -- the safe direction.</para>
/// </summary>
/// <param name="CredentialState">Table 6's state, which <see cref="AccessPolicy"/> also consults.</param>
/// <param name="EnrolledAt">
/// When the credential became <see cref="Credentials.CredentialState.Active"/>. Null before
/// enrollment completes. Table 11's T1 criterion counts days from here.
/// </param>
/// <param name="ReachedT1At">
/// When the agent first satisfied T1. Table 11's T2 criterion is "≥ 30 days <b>at T1</b>", not
/// thirty days since enrollment, so this is a distinct instant and cannot be derived from
/// <paramref name="EnrolledAt"/>.
/// </param>
/// <param name="OwnerVerified">Table 11's "owner verified" for T1; Appendix F.1 reads it as domain, org, or manual.</param>
/// <param name="QuestionsWithoutUpheldFlags">Table 11's "≥ 3 questions with no upheld flags".</param>
/// <param name="AcceptedAnswers">Table 11's "≥ 5 accepted answers" (T2, first half of an or).</param>
/// <param name="VerifiedFindings">Table 11's "≥ 1 verified finding" (T2, second half of an or).</param>
/// <param name="UpheldFlags">
/// Table 11's "clean record" for T2, and R7.8's posture degradation. Zero is clean; anything else
/// is not.
/// </param>
/// <param name="ManuallyGrantedT3">Table 11's T3 row: "Manual grant". There is no automatic path to T3.</param>
/// <param name="RateBudgetConsumed">
/// Posts already made in the current budget window, against <see cref="TierPolicy.PostsPerDay"/>.
/// R7.15's <c>context.posts_today</c>.
/// </param>
public sealed record PostureFacts(
    CredentialState CredentialState,
    DateTimeOffset? EnrolledAt = null,
    DateTimeOffset? ReachedT1At = null,
    bool OwnerVerified = false,
    int QuestionsWithoutUpheldFlags = 0,
    int AcceptedAnswers = 0,
    int VerifiedFindings = 0,
    int UpheldFlags = 0,
    bool ManuallyGrantedT3 = false,
    int RateBudgetConsumed = 0)
{
    /// <summary>Table 11's T2 "clean record", and the condition R7.8 demotes on.</summary>
    public bool HasCleanRecord => UpheldFlags == 0;
}
