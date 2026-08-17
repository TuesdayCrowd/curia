using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// <see cref="PostgresReplayCache"/> against a real Postgres.
///
/// <para>The test that matters most here is
/// <see cref="ReplayIsRefusedAcrossTwoAdapterInstancesOverOneDatabase"/>, because it is the one
/// the in-memory predecessor could not have passed at all. R5.15: "The cache SHALL be shared
/// across all instances of a resource server -- a per-process cache means an attacker replays
/// against a different pod and succeeds." Every other test here checks behavior the in-memory
/// version also had; that one checks the reason for the change.</para>
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresReplayCacheTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresReplayCacheTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private async Task<(ManualTimeProvider Clock, string Schema)> NewSchemaAsync(CancellationToken ct)
    {
        // A schema per test, not a shared table: a jti is a global key, and a test that had to
        // avoid another test's identifiers would be a test whose isolation depended on naming
        // discipline.
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);
        return (new ManualTimeProvider(Now), schema);
    }

    // Typed to the adapter rather than to IReplayCache: CA1859 is right that a private helper
    // gains nothing from the interface, and that the port is satisfied is already a compile-time
    // fact of the class declaration rather than something a helper's return type could add.
    private PostgresReplayCache CacheOn(string schema, TimeProvider clock) =>
        new(_fixture.AppRoleDataSource, clock, schema);

    private static bool Accepted(Result<bool> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    [Fact]
    public async Task FirstUseIsAcceptedAndTheSecondIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, schema) = await NewSchemaAsync(ct);
        var cache = CacheOn(schema, clock);

        Assert.True(Accepted(await cache.TryInsertAsync("jti-first-use", Now.AddMinutes(5), ct)));
        Assert.False(Accepted(await cache.TryInsertAsync("jti-first-use", Now.AddMinutes(5), ct)));
    }

    /// <summary>
    /// The property the in-memory cache could not have, and therefore the one worth asserting:
    /// two adapter instances -- standing in for two resource-server processes behind a load
    /// balancer, or for one process either side of a restart -- share the refusal. Under
    /// <c>InMemoryReplayCache</c> the second instance held an empty dictionary and would have
    /// accepted the replay.
    /// </summary>
    [Fact]
    public async Task ReplayIsRefusedAcrossTwoAdapterInstancesOverOneDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, schema) = await NewSchemaAsync(ct);

        var podA = CacheOn(schema, clock);
        var podB = CacheOn(schema, new ManualTimeProvider(Now));

        Assert.True(Accepted(await podA.TryInsertAsync("jti-two-pods", Now.AddMinutes(5), ct)));
        Assert.False(Accepted(await podB.TryInsertAsync("jti-two-pods", Now.AddMinutes(5), ct)));
    }

    /// <summary>
    /// R5.14 bounds retention at the artifact's own maximum lifetime plus the maximum permitted
    /// skew. Past that instant the artifact can no longer be presented, so the entry recording it
    /// can no longer be defending anything -- and continuing to refuse on its account would refuse
    /// a <c>jti</c> a fresh artifact is entitled to.
    /// </summary>
    [Fact]
    public async Task AnExpiredEntryStopsBlocking()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, schema) = await NewSchemaAsync(ct);
        var cache = CacheOn(schema, clock);

        Assert.True(Accepted(await cache.TryInsertAsync("jti-expiring", Now.AddMinutes(1), ct)));
        Assert.False(Accepted(await cache.TryInsertAsync("jti-expiring", Now.AddMinutes(1), ct)));

        // One second past the recorded expiry, not one minute: the boundary is where an
        // off-by-one in the SQL predicate would hide.
        clock.Set(Now.AddMinutes(1).AddSeconds(1));
        Assert.True(Accepted(await cache.TryInsertAsync("jti-expiring", clock.GetUtcNow().AddMinutes(1), ct)));

        // And the fresh window is enforced from that point, so the re-admission was a new entry
        // rather than the cache having simply forgotten how to refuse.
        Assert.False(Accepted(await cache.TryInsertAsync("jti-expiring", clock.GetUtcNow().AddMinutes(1), ct)));
    }

    /// <summary>
    /// An entry exactly at its expiry instant is collectable. Asserted separately because
    /// <c>expires_at &lt;= now</c> and <c>expires_at &lt; now</c> differ only here, and R5.14's
    /// "retained for at least" makes the inclusive reading the safe one to pin.
    /// </summary>
    [Fact]
    public async Task AnEntryAtExactlyItsExpiryInstantIsNoLongerBlocking()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, schema) = await NewSchemaAsync(ct);
        var cache = CacheOn(schema, clock);

        var expiry = Now.AddMinutes(1);
        Assert.True(Accepted(await cache.TryInsertAsync("jti-boundary", expiry, ct)));

        clock.Set(expiry);
        Assert.True(Accepted(await cache.TryInsertAsync("jti-boundary", expiry.AddMinutes(1), ct)));
    }

    /// <summary>
    /// R5.17: "Cache insertion SHALL be atomic (compare-and-set / <c>SET NX</c>). A
    /// check-then-insert sequence is a race that a concurrent replay wins." Sixteen adapters over
    /// one database present the same <c>jti</c> at once; exactly one may be told it was the first.
    /// A check-then-insert implementation fails this intermittently, which is the worst way for a
    /// replay defense to fail.
    /// </summary>
    [Fact]
    public async Task ConcurrentPresentationsOfOneJtiAdmitExactlyOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, schema) = await NewSchemaAsync(ct);

        var attempts = Enumerable.Range(0, 16)
            .Select(_ => CacheOn(schema, clock).TryInsertAsync("jti-contended", Now.AddMinutes(5), ct))
            .ToArray();

        var outcomes = await Task.WhenAll(attempts);
        Assert.Equal(1, outcomes.Count(Accepted));
    }

    /// <summary>
    /// Distinct identifiers do not interfere. The positive control for every refusal above: a
    /// cache that refused everything would pass most of this file for entirely the wrong reason.
    /// </summary>
    [Fact]
    public async Task DistinctIdentifiersAreIndependent()
    {
        var ct = TestContext.Current.CancellationToken;
        var (clock, schema) = await NewSchemaAsync(ct);
        var cache = CacheOn(schema, clock);

        Assert.True(Accepted(await cache.TryInsertAsync("jti-alpha", Now.AddMinutes(5), ct)));
        Assert.True(Accepted(await cache.TryInsertAsync("jti-beta", Now.AddMinutes(5), ct)));
        Assert.False(Accepted(await cache.TryInsertAsync("jti-alpha", Now.AddMinutes(5), ct)));
    }
}
