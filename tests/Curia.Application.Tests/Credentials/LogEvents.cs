using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;

namespace Curia.Application.Tests.Credentials;

/// <summary>
/// The one place the PR N probes (errata G68) build an <see cref="AppendedEvent"/>: a log read back from a conformance vector, a log key's publication, a filler where a vector holds no entry. A reader of a log
/// constructs the events it serves because there is no store behind it to hand them out; it writes nothing. A test double, not a producer; listed in CS-15's intended write surface so the widening is a recorded decision.
/// </summary>
internal static class LogEvents
{
    internal static AppendedEvent At(long index, string aggregate, DateTimeOffset serverTimestamp, string eventId, string type, string actor, Curia.Canon.Json.JsonValue payload)
    {
        T Require<T>(Result<T> result) => result.Match(v => v, e => throw new InvalidOperationException($"entry {index}: {e.Type}"));
        return new AppendedEvent(
            Require(EventSequence.From(index + 1)),
            Require(AggregateId.Create(aggregate)),
            ServerTimestamp.At(serverTimestamp),
            new DomainEvent(Require(EventId.Create(eventId)), Require(EventType.Create(type)), Require(ActorId.Create(actor)), payload));
    }
}
