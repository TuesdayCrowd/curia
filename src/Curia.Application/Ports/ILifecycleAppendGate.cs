namespace Curia.Application.Ports;

/// <summary>
/// The three lifecycle kinds an identity's key can undergo beyond its enrolment (errata G21): a
/// rotation, a retirement, and an operator's compromise declaration. All three exist from the first
/// day, so the guard's surface does not grow when a kind is built (spec section 4.13).
/// </summary>
public enum LifecycleKind
{
    Rotation,
    Retirement,
    Compromise,
}

/// <summary>
/// R4.38 (add.): whether a lifecycle kind may be appended to the log. The owner answered "Q2: No."
/// (2026-10-11): the lifecycle formats are not approved to freeze, so no lifecycle entry beyond an
/// enrolment's pair reaches a retained log until the owner lifts the guard. The production adapter
/// enables a kind only when the approval record embedded in the build approves it and the runtime
/// opt-in names it; until the lift it enables nothing.
/// </summary>
public interface ILifecycleAppendGate
{
    /// <summary>True only when <paramref name="kind"/> may be appended.</summary>
    bool Enables(LifecycleKind kind);
}
