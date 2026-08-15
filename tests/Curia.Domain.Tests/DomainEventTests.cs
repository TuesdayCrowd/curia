using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Domain.Tests;

public sealed class DomainEventTests
{
    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException(e.Type));

    [Fact]
    public void DomainEventCarriesNoSeqOrServerTimestamp()
    {
        // The type itself has no such members -- there is nothing to assert is "null" or
        // "not yet set" because the concept does not exist on an unappended event. This test
        // exists mainly as documentation: if a future edit added a nullable Seq to DomainEvent,
        // it would not fail this test, which is exactly why the brief asked for two distinct
        // types instead.
        var domainEvent = new DomainEvent(
            Require(EventId.Create("evt-1")),
            Require(EventType.Create("test.event")),
            Actor: null,
            Payload: new JsonValue.Object([]));

        Assert.Equal("evt-1", domainEvent.Id.Value);
        Assert.Null(domainEvent.Actor);
    }

    [Fact]
    public void DomainEventsWithEqualFieldsAreEqual()
    {
        var payload = new JsonValue.Object([]);
        var a = new DomainEvent(Require(EventId.Create("x")), Require(EventType.Create("t")), null, payload);
        var b = new DomainEvent(Require(EventId.Create("x")), Require(EventType.Create("t")), null, payload);

        Assert.Equal(a, b);
    }

    /// <summary>
    /// AppendedEvent's constructor is internal (CS-15's read-side guarantee: only an assembly
    /// Curia.Domain.csproj has granted InternalsVisibleTo can mint one). This project has that
    /// grant so it can test the type's shape directly; Curia.Application.Tests has the same
    /// grant because that is where the R11.4 in-memory adapter actually constructs these.
    /// </summary>
    [Fact]
    public void AppendedEventCarriesSeqAggregateIdServerTimestampAndTheOriginalEvent()
    {
        var domainEvent = new DomainEvent(
            Require(EventId.Create("evt-1")), Require(EventType.Create("test.event")), null, new JsonValue.Object([]));
        var seq = Require(EventSequence.From(7));
        var aggregateId = Require(AggregateId.Create("agg-1"));
        var serverTimestamp = ServerTimestamp.At(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var appended = new AppendedEvent(seq, aggregateId, serverTimestamp, domainEvent);

        Assert.Equal(seq, appended.Seq);
        Assert.Equal(aggregateId, appended.AggregateId);
        Assert.Equal(serverTimestamp, appended.ServerTimestamp);
        Assert.Same(domainEvent, appended.Event);
    }
}
