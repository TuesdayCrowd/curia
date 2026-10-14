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
    /// <summary>The foreign entries, ascending, then the missing acts, ascending.</summary>
    public static ImmutableArray<LifecycleFinding> Compare(AgentProfile profile, KeySetDocument keySet)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(keySet);

        if (keySet.Lifecycle is not { } lifecycle) return [];

        var findings = ImmutableArray.CreateBuilder<LifecycleFinding>();
        var recorded = profile.Acts.Select(a => a.LogIndex).ToHashSet();

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
}
