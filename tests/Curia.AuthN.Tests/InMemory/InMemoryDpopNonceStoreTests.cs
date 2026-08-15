using Xunit;

namespace Curia.AuthN.Tests.InMemory;

/// <summary>R11.4: <see cref="InMemoryDpopNonceStore"/> exercised directly, not only through
/// <c>AccessTokenValidator</c>'s Phase 4 nonce sub-check.</summary>
public sealed class InMemoryDpopNonceStoreTests
{
    [Fact]
    public async Task ANewlyIssuedNonceIsCurrent()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryDpopNonceStore(clock);
        var ct = TestContext.Current.CancellationToken;

        var issued = await store.IssueAsync(ct);
        Assert.True(issued.TryGetValue(out var nonce, out var issueError), issueError?.Detail);

        var isCurrent = await store.IsCurrentAsync(nonce.Value, ct);
        Assert.True(isCurrent.TryGetValue(out var current, out _) && current);
    }

    [Fact]
    public async Task AnUnissuedValueIsNeverCurrent()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryDpopNonceStore(clock);

        var isCurrent = await store.IsCurrentAsync("never-issued", TestContext.Current.CancellationToken);

        Assert.True(isCurrent.TryGetValue(out var current, out _) && !current);
    }

    [Fact]
    public async Task IssuingASecondNonceRotatesOutTheFirst()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryDpopNonceStore(clock);
        var ct = TestContext.Current.CancellationToken;

        var first = await store.IssueAsync(ct);
        Assert.True(first.TryGetValue(out var firstNonce, out _));
        await store.IssueAsync(ct);

        var stillCurrent = await store.IsCurrentAsync(firstNonce.Value, ct);
        Assert.True(stillCurrent.TryGetValue(out var current, out _) && !current);
    }

    [Fact]
    public async Task ANonceExpiresAtItsOwnRotationIntervalEvenWithoutARotationCall()
    {
        // R5.19: "rotation intervals <= 5 minutes." A nonce issued once and never explicitly
        // rotated still stops being current once its own interval elapses.
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryDpopNonceStore(clock, TimeSpan.FromMinutes(5));
        var ct = TestContext.Current.CancellationToken;

        var issued = await store.IssueAsync(ct);
        Assert.True(issued.TryGetValue(out var nonce, out _));

        clock.Advance(TimeSpan.FromMinutes(5));
        var stillCurrentAtBoundary = await store.IsCurrentAsync(nonce.Value, ct);

        clock.Advance(TimeSpan.FromSeconds(1));
        var afterBoundary = await store.IsCurrentAsync(nonce.Value, ct);

        Assert.True(stillCurrentAtBoundary.TryGetValue(out var atBoundary, out _) && !atBoundary);
        Assert.True(afterBoundary.TryGetValue(out var expired, out _) && !expired);
    }
}
