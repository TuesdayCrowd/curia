using Curia.Domain;
using Xunit;

namespace Curia.Domain.Tests;

public sealed class IdentifierTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EventIdRejectsEmptyOrWhitespace(string? value)
    {
        var result = EventId.Create(value!);
        Assert.False(result.IsOk);
        result.Match(_ => throw new InvalidOperationException("expected failure"), e =>
        {
            Assert.Equal("curia/domain/empty-identifier", e.Type);
            return 0;
        });
    }

    [Fact]
    public void EventIdAcceptsAnyNonEmptyString() =>
        Assert.Equal("evt-1", EventId.Create("evt-1").Match(v => v.Value, e => throw new InvalidOperationException(e.Type)));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\t\n")]
    public void AggregateIdRejectsEmptyOrWhitespace(string? value) =>
        Assert.False(AggregateId.Create(value!).IsOk);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ActorIdRejectsEmptyOrWhitespace(string? value) =>
        Assert.False(ActorId.Create(value!).IsOk);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void EventTypeRejectsEmptyOrWhitespace(string? value) =>
        Assert.False(EventType.Create(value!).IsOk);

    [Fact]
    public void EventSequenceFromRejectsNegativeValues()
    {
        Assert.True(EventSequence.From(0).IsOk);
        Assert.True(EventSequence.From(1).IsOk);
        Assert.False(EventSequence.From(-1).IsOk);
    }

    [Fact]
    public void EventSequenceOrdersByValue()
    {
        var one = EventSequence.From(1).Match(v => v, e => throw new InvalidOperationException(e.Type));
        var oneAgain = EventSequence.From(1).Match(v => v, e => throw new InvalidOperationException(e.Type));
        var two = EventSequence.From(2).Match(v => v, e => throw new InvalidOperationException(e.Type));

        Assert.True(one < two);
        Assert.True(two > one);
        Assert.True(one <= oneAgain);
        Assert.True(one >= oneAgain);
        Assert.Equal(-1, one.CompareTo(two));
    }

    [Fact]
    public void EventSequenceZeroIsNotAnAssignedValue() =>
        Assert.Equal(0, EventSequence.Zero.Value);

    [Fact]
    public void AggregateVersionFromRejectsNegativeValues()
    {
        Assert.True(AggregateVersion.From(0).IsOk);
        Assert.False(AggregateVersion.From(-1).IsOk);
    }

    [Fact]
    public void AggregateVersionNewIsZero() =>
        Assert.Equal(0, AggregateVersion.New.Value);

    [Fact]
    public void EventIdsWithEqualValuesAreEqual()
    {
        var a = EventId.Create("same").Match(v => v, e => throw new InvalidOperationException(e.Type));
        var b = EventId.Create("same").Match(v => v, e => throw new InvalidOperationException(e.Type));
        Assert.Equal(a, b);
    }
}
