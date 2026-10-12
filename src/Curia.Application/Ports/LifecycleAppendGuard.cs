using Curia.Application.Projections;
using Curia.Domain;
using Curia.Domain.Primitives;

namespace Curia.Application.Ports;

/// <summary>
/// R4.38 (add.): the one classification of an append into the lifecycle kinds it carries, and the check
/// of those kinds against a gate. Both event-store adapters call <see cref="Check"/>, so the production
/// adapter and its in-memory double cannot disagree about what a lifecycle append is. Type and shape
/// only, never a payload (spec section 4.13).
/// </summary>
public static class LifecycleAppendGuard
{
    /// <summary>The event type a retirement appends (errata G21).</summary>
    public const string KeyRetiredType = "agent.key-retired";

    /// <summary>The event type an operator's compromise declaration appends (errata G21).</summary>
    public const string KeyCompromisedType = "agent.key-compromised";

    /// <summary>
    /// The kinds <paramref name="events"/> carries when appended at <paramref name="expectedVersion"/>:
    /// <c>agent.key-retired</c> is a retirement; <c>agent.key-compromised</c> a compromise; and
    /// <c>agent.key-bound</c> is a rotation unless the append is exactly
    /// <c>[agent.enrolled, agent.key-bound]</c> at <see cref="AggregateVersion.New"/>, which is what
    /// enrolment appends (errata G16). A mixed append carries every kind it contains.
    /// </summary>
    public static IReadOnlyList<LifecycleKind> Classify(AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var kinds = new List<LifecycleKind>();
        if (events.Any(e => IsType(e, KeyRetiredType)))
            kinds.Add(LifecycleKind.Retirement);
        if (events.Any(e => IsType(e, KeyCompromisedType)))
            kinds.Add(LifecycleKind.Compromise);

        var bindsAKey = events.Any(e => IsType(e, AgentStandingProjector.KeyBoundType));
        var isEnrolmentsPair = expectedVersion == AggregateVersion.New
            && events.Count == 2
            && IsType(events[0], AgentStandingProjector.EnrolledType)
            && IsType(events[1], AgentStandingProjector.KeyBoundType);
        if (bindsAKey && !isEnrolmentsPair)
            kinds.Add(LifecycleKind.Rotation);

        return kinds;
    }

    /// <summary>
    /// <see cref="DomainErrors.LifecycleAppendRefused"/> naming the first carried kind
    /// <paramref name="gate"/> does not enable, or null when the append carries none or all are enabled.
    /// </summary>
    public static Error? Check(ILifecycleAppendGate gate, AggregateVersion expectedVersion, IReadOnlyList<DomainEvent> events)
    {
        ArgumentNullException.ThrowIfNull(gate);

        foreach (var kind in Classify(expectedVersion, events))
        {
            if (!gate.Enables(kind))
                return DomainErrors.LifecycleAppendRefused(Name(kind));
        }

        return null;
    }

    private static bool IsType(DomainEvent e, string type) => string.Equals(e.Type.Value, type, StringComparison.Ordinal);

    private static string Name(LifecycleKind kind) => kind switch
    {
        LifecycleKind.Rotation => "rotation",
        LifecycleKind.Retirement => "retirement",
        LifecycleKind.Compromise => "compromise",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "not a lifecycle kind"),
    };
}
