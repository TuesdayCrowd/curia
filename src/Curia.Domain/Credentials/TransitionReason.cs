using Curia.Domain.Primitives;

namespace Curia.Domain.Credentials;

/// <summary>
/// The free-text justification R4.21 requires on every credential lifecycle transition (the
/// "reason" in "actor, reason, and timestamp"). Validated the same way every other domain
/// identifier in this codebase is (<see cref="Curia.Domain.EventId"/>, <see cref="Curia.Domain.ActorId"/>,
/// ...): non-empty or it does not construct (CS-8).
///
/// Deliberately not an enum of Table 6's "Entered by" phrases -- those are already captured
/// precisely by <see cref="CredentialTrigger"/>. This field is the free-text elaboration a real
/// moderation action, anomaly trip, or owner action carries (e.g. "24 failed logins in 5 minutes"
/// alongside an <see cref="CredentialTrigger.AutomatedPostureTrip"/>), which no closed vocabulary
/// could usefully enumerate.
/// </summary>
public readonly record struct TransitionReason
{
    public string Value { get; }

    private TransitionReason(string value) => Value = value;

    public static Result<TransitionReason> Create(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Result<TransitionReason>.Fail(DomainErrors.EmptyIdentifier(nameof(TransitionReason)))
            : Result<TransitionReason>.Ok(new TransitionReason(value));
}
