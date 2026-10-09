using System.Collections.Concurrent;
using Curia.AuthN.Ports;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.AuthN.Tests.InMemory;

/// <summary>
/// R11.4: the in-memory adapter is a first-class implementation with its own tests, not merely a
/// fixture other tests happen to use. R5.17 is specifically about atomicity, so this class proves
/// two things: the real adapter survives a concurrent race on one <c>jti</c> with exactly one
/// winner, and (via <see cref="NaiveCheckThenInsertReplayCache"/>) that the same test genuinely
/// would have failed a non-atomic implementation -- the task's own bar for this port.
/// </summary>
public sealed class InMemoryReplayCacheTests
{
    [Fact]
    public async Task FirstInsertionOfAJtiSucceedsAndSecondFails()
    {
        var cache = new InMemoryReplayCache();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var first = await cache.TryInsertAsync("jti-1", expiresAt, TestContext.Current.CancellationToken);
        var second = await cache.TryInsertAsync("jti-1", expiresAt, TestContext.Current.CancellationToken);

        Assert.True(first.TryGetValue(out var firstInserted, out _) && firstInserted);
        Assert.True(second.TryGetValue(out var secondInserted, out _) && !secondInserted);
    }

    [Fact]
    public async Task DifferentJtisDoNotInterfereWithEachOther()
    {
        var cache = new InMemoryReplayCache();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);

        var a = await cache.TryInsertAsync("jti-a", expiresAt, TestContext.Current.CancellationToken);
        var b = await cache.TryInsertAsync("jti-b", expiresAt, TestContext.Current.CancellationToken);

        Assert.True(a.TryGetValue(out var aInserted, out _) && aInserted);
        Assert.True(b.TryGetValue(out var bInserted, out _) && bInserted);
    }

    /// <summary>
    /// R5.17: "Cache insertion SHALL be atomic (compare-and-set / SET NX). A check-then-insert
    /// sequence is a race that a concurrent replay wins." Two hundred concurrent callers race to
    /// insert the *same* <c>jti</c>; exactly one may ever see <see langword="true"/>. This would
    /// fail immediately for a check-then-insert implementation -- see
    /// <see cref="ANaiveCheckThenInsertImplementationLosesThisRace"/> just below, which runs the
    /// identical race against exactly such an implementation and asserts it loses, proving this
    /// test is not vacuous against the real one.
    /// </summary>
    [Fact]
    public async Task ConcurrentInsertionOfTheSameJtiHasExactlyOneWinner()
    {
        var cache = new InMemoryReplayCache();
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        const int concurrency = 200;
        var ct = TestContext.Current.CancellationToken;

        var results = await Task.WhenAll(Enumerable.Range(0, concurrency)
            .Select(_ => cache.TryInsertAsync("shared-jti", expiresAt, ct)));

        var winners = results.Count(r => r.TryGetValue(out var inserted, out _) && inserted);
        Assert.Equal(1, winners);
    }

    /// <summary>
    /// The falsification the task asks for: an implementation that checks presence, then (after
    /// an intentional yield that widens the race window) inserts -- exactly the sequence R5.17's own
    /// text names as the bug. Racing it the same way <see cref="ConcurrentInsertionOfTheSameJtiHasExactlyOneWinner"/>
    /// races the real adapter produces more than one winner in about 99.5% of single races, not in
    /// every one: measured at the review of A6, 92 of 20,000 single races outside xUnit had exactly
    /// one winner, because a pool thread can run the first caller's insert before the test thread
    /// reaches the second caller's check, and <see cref="Task.Yield"/> does not close that window. So
    /// this fact runs up to twenty independent races, each against a fresh cache, which puts a false
    /// red near 0.0046^20 -- assuming the trials are independent, which the measurement (a warm-pool
    /// loop outside xUnit) did not establish. The same rate is how often
    /// <see cref="ConcurrentInsertionOfTheSameJtiHasExactlyOneWinner"/>, racing once, would miss a
    /// naive adapter of this shape (a yield between check and insert); one with no await between them
    /// runs to completion on the test thread and is not caught by this fan-out at all.
    /// </summary>
    [Fact]
    public async Task ANaiveCheckThenInsertImplementationLosesThisRace()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        const int concurrency = 200;
        const int trials = 20;
        var ct = TestContext.Current.CancellationToken;

        var maxWinners = 0;
        for (var trial = 0; trial < trials && maxWinners <= 1; trial++)
        {
            var cache = new NaiveCheckThenInsertReplayCache();
            var results = await Task.WhenAll(Enumerable.Range(0, concurrency)
                .Select(_ => cache.TryInsertAsync("shared-jti", expiresAt, ct)));
            maxWinners = Math.Max(maxWinners, results.Count(inserted => inserted));
        }

        Assert.True(maxWinners > 1, "expected the non-atomic check-then-insert implementation to race, but it did not in 20 trials");
    }

    /// <summary>Deliberately not atomic: a plain dictionary read followed, after a forced yield,
    /// by a plain dictionary write -- the exact shape R5.17 forbids. Exists only to prove
    /// <see cref="ConcurrentInsertionOfTheSameJtiHasExactlyOneWinner"/> is a meaningful test, per
    /// <see cref="ANaiveCheckThenInsertImplementationLosesThisRace"/>; never referenced by
    /// production code or by <see cref="IReplayCache"/> callers.</summary>
    private sealed class NaiveCheckThenInsertReplayCache
    {
        private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

        public async Task<bool> TryInsertAsync(string jti, DateTimeOffset expiresAt, CancellationToken cancellationToken)
        {
            var alreadyPresent = _seen.ContainsKey(jti);
            await Task.Yield(); // widens the check-then-insert window; it does not guarantee the race (see the fact's summary)
            if (alreadyPresent)
                return false;

            _seen[jti] = expiresAt;
            return true;
        }
    }
}
