using Curia.Domain;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Domain.Tests;

public sealed class KeyPrimitivesTests
{
    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException(e.Type));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void KeyIdRejectsEmptyOrWhitespace(string? value) =>
        Assert.False(KeyId.Create(value!).IsOk);

    [Fact]
    public void KeyIdAcceptsAnyNonEmptyString() =>
        Assert.Equal("kid-1", Require(KeyId.Create("kid-1")).Value);

    [Fact]
    public void KeyIdsWithEqualValuesAreEqual()
    {
        var a = Require(KeyId.Create("same"));
        var b = Require(KeyId.Create("same"));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ServerTimestampRoundTripsTheWrappedInstant()
    {
        var instant = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(instant, ServerTimestamp.At(instant).Value);
    }

    [Fact]
    public void ServerTimestampOrdersByTheWrappedInstant()
    {
        var earlier = ServerTimestamp.At(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var earlierAgain = ServerTimestamp.At(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var later = ServerTimestamp.At(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero));

        Assert.True(earlier < later);
        Assert.True(later > earlier);
        Assert.True(earlier <= earlierAgain);
        Assert.True(earlier >= earlierAgain);
    }

    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ServerTimestamp At(int day) => ServerTimestamp.At(Epoch.AddDays(day));

    [Fact]
    public void ValidityWindowAcceptsAnEndStrictlyAfterTheStart() =>
        Assert.True(KeyValidityWindow.Create(At(1), At(2)).IsOk);

    [Fact]
    public void ValidityWindowAcceptsNoEndAtAll() =>
        Assert.True(KeyValidityWindow.Create(At(1), null).IsOk);

    [Fact]
    public void ValidityWindowRejectsAnEndEqualToTheStart() =>
        Assert.False(KeyValidityWindow.Create(At(1), At(1)).IsOk);

    [Fact]
    public void ValidityWindowRejectsAnEndBeforeTheStart() =>
        Assert.False(KeyValidityWindow.Create(At(3), At(1)).IsOk);

    [Fact]
    public void OpenEndedWindowContainsEveryInstantFromItsStartOnward()
    {
        var window = KeyValidityWindow.OpenEndedFrom(At(1));

        Assert.False(window.Contains(At(0)));
        Assert.True(window.Contains(At(1)));
        Assert.True(window.Contains(At(100)));
        Assert.True(window.IsOpenEnded);
    }

    [Fact]
    public void ClosedWindowExcludesItsOwnEndInstant()
    {
        // [At(1), At(3)) -- half-open: the end instant itself is already outside the window,
        // matching how a revocation's server_ts is the first instant the key is no longer valid.
        var window = Require(KeyValidityWindow.Create(At(1), At(3)));

        Assert.False(window.Contains(At(0)));
        Assert.True(window.Contains(At(1)));
        Assert.True(window.Contains(At(2)));
        Assert.False(window.Contains(At(3)));
        Assert.False(window.Contains(At(4)));
        Assert.False(window.IsOpenEnded);
    }
}
