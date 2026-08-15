using Curia.Domain.Primitives;

namespace Curia.Domain;

/// <summary>
/// The caller-assigned unique identifier for a domain event (<c>events.event_id</c>, Appendix D).
/// Deliberately format-agnostic: unlike <c>posts.id</c>, Appendix D's DDL does not comment this
/// column "ULID", and R8.3's ULID mandate sits in Section 8.1 (the content domain) rather than
/// here. Stage 1 leaves both the encoding and the generation strategy to the caller -- see the
/// Stage 1 report for why this was flagged rather than guessed.
/// </summary>
public readonly record struct EventId
{
    public string Value { get; }

    private EventId(string value) => Value = value;

    public static Result<EventId> Create(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Result<EventId>.Fail(DomainErrors.EmptyIdentifier(nameof(EventId)))
            : Result<EventId>.Ok(new EventId(value));
}

/// <summary>
/// Identifies the aggregate (stream) an event belongs to -- <c>events.aggregate_id</c>. One
/// <see cref="Curia.Application.Ports.IEventStore.AppendAsync"/> call targets exactly one
/// <see cref="AggregateId"/>; every resulting <see cref="AppendedEvent"/> carries it. As with
/// <see cref="EventId"/>, Appendix D gives this column no format annotation, so it is opaque
/// here: a post's ULID, an agent's <c>agent://</c> URI, or anything else a future aggregate
/// kind chooses are all just non-empty text at this layer.
/// </summary>
public readonly record struct AggregateId
{
    public string Value { get; }

    private AggregateId(string value) => Value = value;

    public static Result<AggregateId> Create(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Result<AggregateId>.Fail(DomainErrors.EmptyIdentifier(nameof(AggregateId)))
            : Result<AggregateId>.Ok(new AggregateId(value));
}

/// <summary>
/// Identifies who caused an event -- <c>events.actor_id</c>, nullable in Appendix D because some
/// events are system-generated (epoch sealing, automated moderation) with no agent behind them.
/// Deliberately not typed as a future <c>AgentId</c>: Appendix D's <c>actor_id</c> must also
/// accommodate owner ids, so this stays a generic, opaque, non-empty identifier and the event
/// model has no opinion on what namespace it comes from.
/// </summary>
public readonly record struct ActorId
{
    public string Value { get; }

    private ActorId(string value) => Value = value;

    public static Result<ActorId> Create(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Result<ActorId>.Fail(DomainErrors.EmptyIdentifier(nameof(ActorId)))
            : Result<ActorId>.Ok(new ActorId(value));
}

/// <summary>The event's kind -- <c>events.event_type</c> (e.g. <c>"post.published"</c>).</summary>
public readonly record struct EventType
{
    public string Value { get; }

    private EventType(string value) => Value = value;

    public static Result<EventType> Create(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? Result<EventType>.Fail(DomainErrors.EmptyIdentifier(nameof(EventType)))
            : Result<EventType>.Ok(new EventType(value));
}

/// <summary>
/// <c>events.seq</c>: the position the store assigns an event at append time (Appendix D's
/// <c>BIGINT GENERATED ALWAYS AS IDENTITY</c>, global across every aggregate, not per-stream).
/// The public surface is <see cref="From"/>, for reconstructing a previously observed value --
/// e.g. a replay checkpoint a projection persisted (R11.9/R11.10). Trusted, already-validated
/// construction (a store computing "the next value") uses the internal primary constructor
/// directly; see <see cref="AppendedEvent"/> for the actual "this was really assigned by a
/// store" guarantee, which this type alone cannot carry.
/// </summary>
public readonly record struct EventSequence : IComparable<EventSequence>
{
    /// <summary>Not an assigned event's sequence (Appendix D's IDENTITY starts at 1) -- the
    /// value that means "before the first event," so scanning forward from it replays everything.</summary>
    public static readonly EventSequence Zero = new(0);

    public long Value { get; }

    internal EventSequence(long value) => Value = value;

    public static Result<EventSequence> From(long value) =>
        value < 0
            ? Result<EventSequence>.Fail(DomainErrors.NegativeSequence(value))
            : Result<EventSequence>.Ok(new EventSequence(value));

    public int CompareTo(EventSequence other) => Value.CompareTo(other.Value);
    public static bool operator <(EventSequence left, EventSequence right) => left.CompareTo(right) < 0;
    public static bool operator >(EventSequence left, EventSequence right) => left.CompareTo(right) > 0;
    public static bool operator <=(EventSequence left, EventSequence right) => left.CompareTo(right) <= 0;
    public static bool operator >=(EventSequence left, EventSequence right) => left.CompareTo(right) >= 0;
}

/// <summary>
/// The caller's belief about how many events an aggregate already has, supplied to
/// <see cref="Curia.Application.Ports.IEventStore.AppendAsync"/> for optimistic concurrency: a
/// mismatch against the store's actual count is a <see cref="Result{T}"/> failure
/// (<see cref="DomainErrors.ConcurrencyConflict"/>), never an exception, never a silent overwrite.
/// </summary>
public readonly record struct AggregateVersion : IComparable<AggregateVersion>
{
    /// <summary>The expected version of an aggregate nothing has ever been appended to.</summary>
    public static readonly AggregateVersion New = new(0);

    public long Value { get; }

    internal AggregateVersion(long value) => Value = value;

    public static Result<AggregateVersion> From(long value) =>
        value < 0
            ? Result<AggregateVersion>.Fail(DomainErrors.NegativeVersion(value))
            : Result<AggregateVersion>.Ok(new AggregateVersion(value));

    public int CompareTo(AggregateVersion other) => Value.CompareTo(other.Value);
    public static bool operator <(AggregateVersion left, AggregateVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(AggregateVersion left, AggregateVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(AggregateVersion left, AggregateVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(AggregateVersion left, AggregateVersion right) => left.CompareTo(right) >= 0;
}
