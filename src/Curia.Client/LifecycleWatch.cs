using System.Collections.Immutable;

namespace Curia.Client;

/// <summary>
/// One disagreement between the lifecycle the Forum publishes and the acts this profile recorded (spec section 4.9).
/// </summary>
/// <param name="Foreign">
/// True for an entry the Forum publishes that this profile did not make (<c>FOREIGN</c>); false for an act this
/// profile recorded that the Forum's lifecycle does not hold (<c>MISSING</c>).
/// </param>
/// <param name="Kind"><c>rotation</c>, <c>retirement</c>, or <c>lifecycle entry</c> for an entry the key set does not account for.</param>
/// <param name="LogIndex">The entry's log index.</param>
public sealed record LifecycleFinding(bool Foreign, string Kind, long LogIndex);

/// <summary>
/// R4.39's comparison: an identity's published lifecycle against the acts its own profile made. A key held
/// by a thief can rotate or retire in the owner's name, and the owner learns it only by comparing; the Forum
/// is not trusted to say so (spec section 4.9).
///
/// <para><b>What counts as after enrolment.</b> The key set names each key's binding (<c>curia_log_index</c>) and
/// each retirement (<c>curia_retired_log_index</c>), not each entry's kind. The identity's first binding is its
/// enrolment's, and every lifecycle index up to and including it is the enrolment's own (its
/// <c>agent.enrolled</c> and its first <c>agent.key-bound</c>). Every later index is an act: a rotation when a key
/// is bound there, a retirement when a key is retired there, and otherwise a lifecycle entry the key set does not
/// account for, which is foreign all the same.</para>
///
/// <para><b>An absence is not an agreement.</b> A key set that names no lifecycle, or whose keys name no
/// binding, cannot be compared and yields no foreign finding; a caller that must act on the comparison (a
/// rotation, a retirement) refuses when the lifecycle is absent rather than reading this as clean.</para>
/// </summary>
public static class LifecycleWatch
{
    /// <summary>The foreign entries, ascending, then the missing acts, ascending. An act the profile sent and did not hear an answer to is counted as its own where <see cref="KeyActSettling.RecordedAt"/> says the key set holds it.</summary>
    public static ImmutableArray<LifecycleFinding> Compare(AgentProfile profile, KeySetDocument keySet)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(keySet);

        if (keySet.Lifecycle is not { } lifecycle) return [];

        var findings = ImmutableArray.CreateBuilder<LifecycleFinding>();

        // An act in force is the profile's own at its index, as a 201 answered it. An act recorded as not in force (R11.98) is the profile's own there only where the key set binds (a rotation) or
        // retires (a retirement) the act's own kid at that index: at an index the lifecycle holds for another kid it is not, and the entry is FOREIGN (R11.96 (add.)).
        var recorded = profile.Acts.Where(a => a.InForce || Holds(keySet, a)).Select(a => a.LogIndex).ToHashSet();

        // An act this profile sent and did not hear an answer to (R11.100) is its own at an index only where the key set binds the kept kid with the kept key's public half there (a rotation) or shows the kid retired there
        // (a retirement). Any other entry at that index is FOREIGN: the kid alone could be a stranger's.
        foreach (var pending in profile.Pending)
            if (KeyActSettling.RecordedAt(pending, keySet) is { } at)
                recorded.Add(at);

        var bindings = keySet.Keys.Where(k => k.LogIndex is not null).Select(k => k.LogIndex!.Value).ToList();
        if (bindings.Count > 0)
        {
            var enrolment = bindings.Min();
            foreach (var index in lifecycle.Where(i => i > enrolment).Order())
            {
                if (recorded.Contains(index)) continue;

                var kind = keySet.Keys.Any(k => k.LogIndex == index) ? LifecycleAct.Rotation
                    : keySet.Keys.Any(k => k.RetiredLogIndex == index) ? LifecycleAct.Retirement
                    : "lifecycle entry";
                findings.Add(new LifecycleFinding(Foreign: true, kind, index));
            }
        }

        var published = lifecycle.ToHashSet();
        foreach (var act in profile.Acts.OrderBy(a => a.LogIndex))
            if (!published.Contains(act.LogIndex))
                findings.Add(new LifecycleFinding(Foreign: false, act.Kind, act.LogIndex));

        return findings.ToImmutable();
    }

    /// <summary>
    /// The acts the profile recorded as not in force (R11.98) that the Forum now acts on (R11.96 (add.)): the published lifecycle holds the act's index and the key set binds (a rotation) or retires (a
    /// retirement) the act's own kid there. In log order.
    /// </summary>
    public static ImmutableArray<LifecycleAct> NowInForce(AgentProfile profile, KeySetDocument keySet)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(keySet);

        if (keySet.Lifecycle is not { } lifecycle) return [];

        var published = lifecycle.ToHashSet();
        return [.. profile.Acts.Where(a => !a.InForce && published.Contains(a.LogIndex) && Holds(keySet, a) && !Ended(profile, keySet, a)).OrderBy(a => a.LogIndex)];
    }

    /// <summary>
    /// Whether the act's key is already ended, so its <c>NOW IN FORCE</c> line has nothing left to announce (R11.96 (add.)): the profile holds the act's own kid retired, or, for a rotation, the key set shows that kid
    /// retired (a key that is retired is not live, whoever retired it; a retirement this profile did not make is reported <c>FOREIGN</c> at its index). Both questions are about the act's own kid.
    /// </summary>
    private static bool Ended(AgentProfile profile, KeySetDocument keySet, LifecycleAct act) =>
        profile.Retained.Any(k => k.Retired && string.Equals(k.Kid, act.Kid, StringComparison.Ordinal))
        || (act.Kind == LifecycleAct.Rotation
            && keySet.Keys.Any(k => string.Equals(k.Kid, act.Kid, StringComparison.Ordinal) && k.RetiredLogIndex is not null));

    /// <summary>Whether the key set binds (a rotation) or retires (a retirement) the act's own kid at the act's own index.</summary>
    private static bool Holds(KeySetDocument keySet, LifecycleAct act) =>
        act.Kind == LifecycleAct.Rotation
            ? keySet.Keys.Any(k => string.Equals(k.Kid, act.Kid, StringComparison.Ordinal) && k.LogIndex == act.LogIndex)
            : keySet.Keys.Any(k => string.Equals(k.Kid, act.Kid, StringComparison.Ordinal) && k.RetiredLogIndex == act.LogIndex);
}
