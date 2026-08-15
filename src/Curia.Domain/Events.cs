using Curia.Canon.Json;
using Curia.Domain.Primitives;

namespace Curia.Domain;

/// <summary>
/// An event proposed for the append-only log, before the store has accepted it. Deliberately
/// carries no <see cref="EventSequence"/> and no server timestamp -- Appendix D's <c>seq</c> and
/// <c>server_ts</c> are assigned by the store, and a type that has not been through
/// <see cref="Curia.Application.Ports.IEventStore.AppendAsync"/> has no business claiming either,
/// so there is no nullable field here for a caller to misread as "not yet set." See
/// <see cref="AppendedEvent"/> for the type that carries both, and only after a real append.
/// </summary>
/// <param name="Id">The caller-assigned <c>event_id</c> (idempotency key; R11.12).</param>
/// <param name="Type">The event's kind (<c>event_type</c>).</param>
/// <param name="Actor">Who caused it (<c>actor_id</c>); <see langword="null"/> for system-generated events.</param>
/// <param name="Payload">
/// The event body (<c>payload</c>). Typed as <see cref="JsonValue"/> -- Curia.Canon's own JSON
/// tree -- rather than pulling a general-purpose JSON library into the domain (R11.1); a
/// caller builds it the same way <c>Curia.Canon</c> itself builds envelope documents.
/// </param>
public sealed record DomainEvent(EventId Id, EventType Type, ActorId? Actor, JsonValue Payload);

/// <summary>
/// A <see cref="DomainEvent"/> the store has actually persisted: it carries the
/// <see cref="EventSequence"/> and <see cref="ServerTimestamp"/> Appendix D's <c>seq</c> and
/// <c>server_ts</c> columns record, and the <see cref="AggregateId"/> stream it landed in.
///
/// <see cref="ServerTimestamp"/> here is the wrapper type of that name, not a bare
/// <see cref="DateTimeOffset"/> -- this is the one place Appendix D's <c>server_ts</c> column
/// enters the domain, and it must not be constructible from, or confused with, a
/// <see cref="DateTimeOffset"/> read out of an envelope's <c>created_at</c>.
///
/// The constructor is <see langword="internal"/>, not merely undocumented-but-public: only code
/// in an assembly this project's <c>Curia.Domain.csproj</c> explicitly grants
/// <c>InternalsVisibleTo</c> can mint one, so a value that says "the store assigned this" cannot
/// be fabricated by an arbitrary Application-layer component and handed to, say, a replay
/// consumer as if it had really been persisted (R11.9's rebuild-by-replay depends on every event
/// it processes being real). This is CS-15's write-surface narrowing applied to the *read* side:
/// the store's write door narrows who can call Append; this narrows who can manufacture something
/// that looks like Append's result. See the Stage 1 report for what this guarantee does and does
/// not cover -- in short, "internal" is an assembly-wide grant, not a single-call-site one, so it
/// is Stage 4's architecture tests that close the rest.
/// </summary>
public sealed record AppendedEvent
{
    public EventSequence Seq { get; }
    public AggregateId AggregateId { get; }
    public ServerTimestamp ServerTimestamp { get; }
    public DomainEvent Event { get; }

    internal AppendedEvent(EventSequence seq, AggregateId aggregateId, ServerTimestamp serverTimestamp, DomainEvent @event)
    {
        Seq = seq;
        AggregateId = aggregateId;
        ServerTimestamp = serverTimestamp;
        Event = @event;
    }
}
