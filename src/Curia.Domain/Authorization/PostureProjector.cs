using Curia.Domain.Credentials;
using Curia.Domain.Primitives;

namespace Curia.Domain.Authorization;

/// <summary>
/// R7.7's other half: the facts a tier is computed from come out of the event history, not out of
/// a token. <see cref="TierPolicy"/> makes a token-derived tier impossible to <i>construct</i>;
/// this makes the honest alternative available.
///
/// <para><b>Clock-free, deliberately.</b> Nothing here reads a
/// <see cref="TimeProvider"/> -- every instant it produces was already recorded on an event.
/// <c>AggregateSummaryProjector</c> records the reason and it applies unchanged: a projection that
/// consulted "now" would make R11.9's replay-rebuild drill tautological, because two runs would
/// differ only by when they ran and a test asserting they agree would pass by the accident of
/// running close together. The elapsed-time half of Table 11 lives in
/// <see cref="TierPolicy.Evaluate"/>, which takes the instant as an argument.</para>
///
/// <para><b>What it can and cannot see today.</b> Table 11 also counts questions, accepted
/// answers, verified findings and upheld flags. Those are §8 content events, which do not exist
/// yet -- the content domain is a later stage. This fold therefore produces the credential-derived
/// facts and leaves the counted ones at their defaults, which denies promotion rather than
/// granting it. That is the safe direction, and it is why <see cref="Fold"/> takes the counted
/// facts as an explicit argument instead of silently defaulting them: a caller has to decide what
/// it knows, rather than receiving a <see cref="PostureFacts"/> that looks complete and is not.</para>
/// </summary>
public static class PostureProjector
{
    /// <summary>
    /// Folds a credential's event history into the posture facts derivable from it, merging in the
    /// content-derived counts the caller supplies.
    /// </summary>
    /// <param name="history">
    /// The credential's transitions in order, exactly as <see cref="CredentialLifecycle.Project"/>
    /// consumes them -- so the state this returns and the state that returns cannot disagree.
    /// </param>
    /// <param name="counted">
    /// The §8-derived counts (questions, answers, findings, flags) and the manual T3 grant. Its
    /// <see cref="PostureFacts.CredentialState"/> and <see cref="PostureFacts.EnrolledAt"/> are
    /// ignored: those are this fold's output, and letting a caller supply them would reintroduce
    /// exactly the "tier from somewhere other than the log" path R7.7 forbids.
    /// </param>
    public static Result<PostureFacts> Fold(
        IReadOnlyList<CredentialTransitionedEvent> history,
        PostureFacts counted)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(counted);

        return CredentialLifecycle.Project(history).Map(state => counted with
        {
            CredentialState = state,
            EnrolledAt = EnrolledAt(history),

            // Not derivable from credential events: reaching T1 is a function of content counts
            // and elapsed time, so the caller carries it. Left as supplied rather than cleared,
            // because clearing it would silently deny every T2 promotion and look like policy.
            ReachedT1At = counted.ReachedT1At,
        });
    }

    /// <summary>
    /// When the credential first became <see cref="CredentialState.Active"/>. First, not last: an
    /// agent suspended and reinstated has not restarted its tenure, and Table 11's "≥ 48 hours"
    /// counts from enrollment. Taking the most recent activation instead would silently reset the
    /// clock on every reinstatement, which would turn a suspension into a demotion the published
    /// table never describes.
    /// </summary>
    private static DateTimeOffset? EnrolledAt(IReadOnlyList<CredentialTransitionedEvent> history)
    {
        foreach (var transition in history)
            if (transition.Trigger is CredentialTrigger.SuccessfulEnrollment)
                return transition.Timestamp;

        return null;
    }
}
