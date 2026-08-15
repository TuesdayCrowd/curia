namespace Curia.Domain.Credentials;

/// <summary>
/// R4.21's "append-only event carrying actor, reason, and timestamp" for one credential lifecycle
/// transition.
///
/// Deliberately does not carry the resulting <see cref="CredentialState"/>. Table 6 is a pure
/// function of (state, trigger), so storing the destination alongside the trigger would let a
/// corrupted or hand-edited event disagree with the table it is supposed to be a record of;
/// instead <see cref="CredentialLifecycle.Project"/> is the only thing that ever computes "what
/// state did this lead to," by re-deriving it, every time, from <see cref="Trigger"/> and
/// whatever state the fold has reached so far -- never from a value read off the event itself.
/// This is also what makes "the current state is a projection, never a stored field" (R4.21) true
/// by construction here: no field on this type, or on any aggregate built from a sequence of them,
/// is "the current state" a caller could read directly. Projecting the whole history is the only
/// way to find out, which is exactly what lets a per-request PDP consultation (R4.22) see a
/// suspension or quarantine the instant its triggering event is appended -- there is no cached
/// claim anywhere in this layer for such a consultation to have to invalidate.
/// </summary>
/// <param name="Trigger">Which Table 6 edge fired.</param>
/// <param name="Actor">
/// Who caused it; <see langword="null"/> for a system-generated trigger such as
/// <see cref="CredentialTrigger.AutomatedPostureTrip"/> -- mirrors <see cref="DomainEvent.Actor"/>'s
/// identical nullability, for the identical reason (some events have no agent or owner behind them).
/// </param>
/// <param name="Reason">Free-text elaboration of why.</param>
/// <param name="Timestamp">
/// When the store recorded this event. Supplied by the caller: this type has no ambient clock
/// access of its own (CS-9) -- whatever appends these events reads the time from a
/// <see cref="TimeProvider"/>, not from here, exactly as <see cref="AppendedEvent.ServerTimestamp"/>
/// is a plain field rather than something this layer computes.
/// </param>
public sealed record CredentialTransitionedEvent(
    CredentialTrigger Trigger,
    ActorId? Actor,
    TransitionReason Reason,
    DateTimeOffset Timestamp);
