using CsCheck;
using Curia.Domain.Credentials;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Domain.Tests.Credentials;

/// <summary>
/// CsCheck properties over <see cref="CredentialLifecycle"/>: "a property that replaying any legal
/// transition sequence never yields an illegal state" (Stage A brief), plus a companion property
/// that <see cref="CredentialLifecycle.Project"/> never throws and always agrees with a manual
/// left-fold over <see cref="CredentialLifecycle.Transition"/>, for arbitrary -- not just legal --
/// trigger sequences.
/// </summary>
public sealed class CredentialLifecycleProperties
{
    private static readonly CredentialTrigger[] AllTriggers = Enum.GetValues<CredentialTrigger>();

    /// <summary>
    /// Every trigger that is currently a legal exit of <paramref name="from"/>, in declaration
    /// order. Used only to build legal-by-construction random walks below -- it is not a shortcut
    /// around <see cref="CredentialLifecycle.Transition"/>, which is still what every walk step
    /// calls and what proves each step legal.
    /// </summary>
    private static CredentialTrigger[] LegalTriggersFrom(CredentialState from) =>
        [.. AllTriggers.Where(t => CredentialLifecycle.Transition(from, t).IsOk)];

    /// <summary>
    /// Builds a legal walk of up to <paramref name="choices"/>.Count events from
    /// <see cref="CredentialState.Pending"/>, using each choice to select among the current
    /// state's legal triggers (stopping early once a terminal state is reached, since there is
    /// then nothing legal left to add). Every event this produces is, by construction, a legal
    /// exit of the state that preceded it -- CsCheck only ever has to supply plain integers.
    /// </summary>
    private static List<CredentialTransitionedEvent> WalkLegally(IReadOnlyList<int> choices)
    {
        var events = new List<CredentialTransitionedEvent>();
        var state = CredentialState.Pending;

        foreach (var choice in choices)
        {
            var options = LegalTriggersFrom(state);
            if (options.Length == 0)
                break; // terminal state -- nothing legal left to add

            var index = ((choice % options.Length) + options.Length) % options.Length;
            var trigger = options[index];

            events.Add(TestSupport.Event(trigger));
            state = TestSupport.Require(CredentialLifecycle.Transition(state, trigger));
        }

        return events;
    }

    [Fact]
    public void LegalWalksAlwaysProjectSuccessfully() =>
        Gen.Int.List[0, 12].Sample(choices => CredentialLifecycle.Project(WalkLegally(choices)).IsOk, iter: 500);

    [Fact]
    public void LegalWalksReachAStateThatAgreesWithTheWalksOwnFinalTransition() =>
        Gen.Int.List[0, 12].Sample(choices =>
        {
            var history = WalkLegally(choices);
            if (history.Count == 0)
                return CredentialLifecycle.Project(history).Match(s => s == CredentialState.Pending, _ => false);

            // Replaying every event but the last, then taking one more step "by hand," must land
            // on exactly what Project says the whole history landed on.
            var upToLast = CredentialLifecycle.Project([.. history.Take(history.Count - 1)]);
            var expected = upToLast.Bind(s => CredentialLifecycle.Transition(s, history[^1].Trigger));

            return CredentialLifecycle.Project(history).Match(
                actual => expected.Match(exp => exp == actual, _ => false),
                _ => false);
        }, iter: 500);

    [Fact]
    public void TerminalStatesReachedByALegalWalkAcceptNoFurtherTrigger() =>
        Gen.Int.List[0, 12].Sample(choices =>
        {
            var finalState = TestSupport.Require(CredentialLifecycle.Project(WalkLegally(choices)));

            var isTerminal = finalState is CredentialState.Retired or CredentialState.Compromised or CredentialState.Expired;
            return !isTerminal || AllTriggers.All(t => !CredentialLifecycle.Transition(finalState, t).IsOk);
        }, iter: 500);

    /// <summary>
    /// Total-function property over *arbitrary* (not filtered to legal) trigger sequences:
    /// <see cref="CredentialLifecycle.Project"/> never throws, and always agrees with a manual
    /// left-fold performed directly with <see cref="CredentialLifecycle.Transition"/> -- i.e.
    /// Project is not doing anything Transition itself, chained by hand, would not also do.
    /// </summary>
    [Fact]
    public void ProjectionNeverThrowsAndAgreesWithAManualFold()
    {
        var genTrigger = Gen.Int[0, AllTriggers.Length - 1].Select(i => AllTriggers[i]);

        genTrigger.List[0, 10].Sample(triggers =>
        {
            var history = triggers.Select(TestSupport.Event).ToList();

            var manual = Result<CredentialState>.Ok(CredentialState.Pending);
            foreach (var trigger in triggers)
                manual = manual.Bind(s => CredentialLifecycle.Transition(s, trigger));

            var projected = CredentialLifecycle.Project(history);

            return manual.Match(
                manualState => projected.Match(projectedState => manualState == projectedState, _ => false),
                _ => !projected.IsOk);
        }, iter: 500);
    }
}
