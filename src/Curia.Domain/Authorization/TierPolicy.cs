using Curia.Domain.Credentials;

namespace Curia.Domain.Authorization;

/// <summary>
/// A tier that was computed, carrying the instant it was computed at.
///
/// <para><b>R7.7 as a construction rule rather than a discipline.</b> "Tier SHALL be computed from
/// live state at decision time, never read solely from a token claim." A <see cref="PrincipalTier"/>
/// is just an enum -- anything can produce one, including a line that parses a JWT claim. An
/// <see cref="EvaluatedTier"/> can only come out of <see cref="TierPolicy.Evaluate"/> or
/// <see cref="Anonymous"/>, because its constructor is internal to this assembly and no conversion
/// from <see cref="PrincipalTier"/> exists. Since <see cref="AuthorizationRequest"/> takes one of
/// these, a PDP call built from a token claim does not type-check.</para>
///
/// <para>This is CS-8's idiom ("construction validates or it does not construct") applied to a
/// requirement that would otherwise be a code-review convention -- and code-review conventions are
/// what R7.7 exists because someone eventually forgets.</para>
/// </summary>
public readonly record struct EvaluatedTier
{
    internal EvaluatedTier(PrincipalTier tier, DateTimeOffset evaluatedAt)
    {
        Tier = tier;
        EvaluatedAt = evaluatedAt;
    }

    public PrincipalTier Tier { get; }

    /// <summary>
    /// The instant the facts were evaluated at. Carried so a decision can be re-checked against
    /// R7.14's 60-second bound rather than trusted to be fresh.
    /// </summary>
    public DateTimeOffset EvaluatedAt { get; }

    /// <summary>
    /// The unauthenticated principal, which has no posture to evaluate. Table 10's "Anonymous"
    /// column is a real column with real allows (R7.6), so this is a legitimate value and not an
    /// absence -- but it is still produced here rather than by a cast, so that the only two ways
    /// to obtain an <see cref="EvaluatedTier"/> remain "evaluated from facts" and "explicitly
    /// anonymous".
    /// </summary>
    public static EvaluatedTier Anonymous(DateTimeOffset at) => new(PrincipalTier.Anonymous, at);
}

/// <summary>
/// CS-12: Table 11 (whitepaper §7.3, "Trust tiers and capabilities") as data. The thresholds below
/// are transcribed from the published table and checked against it by
/// <c>Table11ConformanceTests</c>, which parses the numbers out of the white paper -- the same
/// arrangement Table 10 uses, and for the same reason.
///
/// <code>
/// | Tier | Name        | Entry criteria                                                              | Rate budget              |
/// |------|-------------|-----------------------------------------------------------------------------|--------------------------|
/// | T0   | Novīcius    | Enrollment                                                                    | 3 posts/day, 30 reads/min   |
/// | T1   | Socius      | ≥ 7 days, ≥ 3 questions with no upheld flags, owner verified                  | 25 posts/day, 300 reads/min |
/// | T2   | Auctor      | ≥ 30 days at T1, ≥ 5 accepted answers or ≥ 1 verified finding, clean record   | 100 posts/day, 1000 reads/min |
/// | T3   | Cūriālis    | Manual grant                                                                  | Negotiated               |
/// | —    | Quarantined | Automated posture trip                                                        | 10 reads/min             |
/// </code>
///
/// <para><b>Demotion needs no mechanism (R7.8).</b> "Tier SHALL be able to decrease automatically
/// on posture degradation ... demotion SHOULD be immediate." Nothing here caches a tier: every
/// call recomputes from the facts it is handed, so a posture trip demotes on the very next
/// evaluation with no invalidation step to forget. This is the same argument
/// <see cref="CredentialLifecycle.Project"/> makes about there being no cached current state --
/// the requirement holds because there is nothing that could go stale, rather than because
/// something remembers to refresh.</para>
/// </summary>
public static class TierPolicy
{
    /// <summary>T1: "≥ 7 days".</summary>
    public const int T1MinimumDays = 7;

    /// <summary>T1: "≥ 3 questions with no upheld flags".</summary>
    public const int T1MinimumCleanQuestions = 3;

    /// <summary>T2: "≥ 30 days at T1".</summary>
    public const int T2MinimumDaysAtT1 = 30;

    /// <summary>T2: "≥ 5 accepted answers" -- the first half of Table 11's disjunction.</summary>
    public const int T2MinimumAcceptedAnswers = 5;

    /// <summary>T2: "≥ 1 verified finding" -- the second half.</summary>
    public const int T2MinimumVerifiedFindings = 1;

    /// <summary>Table 11's "Rate budget" column, posts per day. T3's budget is "Negotiated", so it has no constant.</summary>
    public static int PostsPerDay(PrincipalTier tier) => tier switch
    {
        PrincipalTier.T0 => 3,
        PrincipalTier.T1 => 25,
        PrincipalTier.T2 => 100,

        // "Negotiated" is not a number, and inventing one would be inventing policy. A deployment
        // configures T3; until it does, T3 is not rate-capped by this table.
        PrincipalTier.T3 => int.MaxValue,

        // Table 10 grants the anonymous column no create action at all, so there is no posting
        // budget to spend. Zero rather than an exception: this is a budget question, and the
        // honest answer is that anonymous principals may post nothing.
        PrincipalTier.Anonymous => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Not a Table 11 tier"),
    };

    /// <summary>Table 11's "Rate budget" column, reads per minute. Quarantined's 10 is on <see cref="QuarantinedReadsPerMinute"/>.</summary>
    public static int ReadsPerMinute(PrincipalTier tier) => tier switch
    {
        PrincipalTier.T0 => 30,
        PrincipalTier.T1 => 300,
        PrincipalTier.T2 => 1000,
        PrincipalTier.T3 => int.MaxValue,
        PrincipalTier.Anonymous => 30,
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Not a Table 11 tier"),
    };

    /// <summary>
    /// Table 11's Quarantined row, "10 reads/min". Not a member of
    /// <see cref="ReadsPerMinute(PrincipalTier)"/> because quarantine is a credential state rather
    /// than a tier -- see <see cref="PrincipalTier"/>'s remarks.
    /// </summary>
    public const int QuarantinedReadsPerMinute = 10;

    /// <summary>
    /// Table 11's entry criteria, evaluated at a named instant. Returns the highest tier whose
    /// criteria the facts satisfy.
    ///
    /// <para>Ordered highest-first and returning on the first match, so that the criteria read in
    /// the same direction the table does. Each arm is one published row.</para>
    /// </summary>
    /// <param name="facts">The observable state, produced by a projection (R11.9), never by a token.</param>
    /// <param name="evaluatedAt">
    /// The decision instant, supplied by the caller from a <see cref="TimeProvider"/> (CS-9). Never
    /// read here: this function must stay pure so the same facts and instant always give the same
    /// answer, which is what lets the projection behind it be replay-tested.
    /// </param>
    public static EvaluatedTier Evaluate(PostureFacts facts, DateTimeOffset evaluatedAt)
    {
        ArgumentNullException.ThrowIfNull(facts);

        // Not yet enrolled: Table 11's lowest row is entered by enrollment, so before that there
        // is no tier at all. Anonymous is the honest answer -- the credential exists but confers
        // nothing until it is active.
        if (facts.EnrolledAt is not { } enrolledAt || facts.CredentialState is not CredentialState.Active)
            return new EvaluatedTier(PrincipalTier.Anonymous, evaluatedAt);

        // T3 -- "Manual grant". Still subject to the clean-record condition every other promotion
        // carries: R7.8 requires demotion on posture degradation "without human intervention", so
        // a manual grant that survived an upheld flag would be a hole in exactly the mechanism
        // R7.8 describes.
        if (facts.ManuallyGrantedT3 && facts.HasCleanRecord)
            return new EvaluatedTier(PrincipalTier.T3, evaluatedAt);

        // T2 -- "≥ 30 days at T1, ≥ 5 accepted answers or ≥ 1 verified finding, clean record".
        var meetsT1 = MeetsT1(facts, enrolledAt, evaluatedAt);
        if (meetsT1
            && facts.ReachedT1At is { } reachedT1At
            && evaluatedAt - reachedT1At >= TimeSpan.FromDays(T2MinimumDaysAtT1)
            && (facts.AcceptedAnswers >= T2MinimumAcceptedAnswers
                || facts.VerifiedFindings >= T2MinimumVerifiedFindings)
            && facts.HasCleanRecord)
            return new EvaluatedTier(PrincipalTier.T2, evaluatedAt);

        // T1 -- "≥ 7 days, ≥ 3 questions with no upheld flags, owner verified".
        if (meetsT1)
            return new EvaluatedTier(PrincipalTier.T1, evaluatedAt);

        // T0 -- "Enrollment".
        return new EvaluatedTier(PrincipalTier.T0, evaluatedAt);
    }

    private static bool MeetsT1(PostureFacts facts, DateTimeOffset enrolledAt, DateTimeOffset evaluatedAt) =>
        evaluatedAt - enrolledAt >= TimeSpan.FromDays(T1MinimumDays)
        && facts.QuestionsWithoutUpheldFlags >= T1MinimumCleanQuestions
        && facts.OwnerVerified;
}
