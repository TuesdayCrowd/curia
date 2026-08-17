using Curia.AuthN;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// <see cref="PostgresDpopNonceStore"/> against a real Postgres: rotation on the published
/// interval, the previous nonce still accepted, and -- the property the in-memory store could not
/// have -- two instances agreeing on which value is current.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresDpopNonceStoreTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresDpopNonceStoreTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Chosen to sit exactly on a rotation-epoch boundary (its Unix second is divisible by 300),
    /// so "advance by one interval" moves to the next epoch and nothing else, and a test that
    /// advances by less than an interval genuinely stays inside one.
    /// </summary>
    private static readonly DateTimeOffset EpochStart = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static TimeSpan Rotation => AuthNConstants.MaxDpopNonceRotationInterval;

    // Typed to the adapter rather than to IDpopNonceStore, for the reason CA1859 gives: a
    // private helper gains nothing from the interface, and that the port is satisfied is already
    // a compile-time fact of the class declaration.
    private PostgresDpopNonceStore StoreOn(string schema, TimeProvider clock) =>
        new(_fixture.AppRoleDataSource, clock, schema: schema);

    private static T Require<T>(Result<T> result) =>
        result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

    [Fact]
    public async Task AnIssuedNonceIsCurrent()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);
        var store = StoreOn(schema, new ManualTimeProvider(EpochStart));

        var nonce = Require(await store.IssueAsync(ct));

        Assert.NotEmpty(nonce.Value);
        Assert.True(Require(await store.IsCurrentAsync(nonce.Value, ct)));
        Assert.False(Require(await store.IsCurrentAsync("a-value-this-store-never-issued", ct)));
    }

    /// <summary>
    /// The multi-instance property, and the reason this is a table. Two adapters -- two pods, or
    /// one pod either side of a restart -- issue the <i>same</i> nonce, because the epoch is
    /// computed identically from each one's clock and the first to ask wins the insert. Under
    /// <c>InMemoryDpopNonceStore</c> each process minted its own value, so a client that obtained
    /// a nonce from one pod was refused by the next.
    /// </summary>
    [Fact]
    public async Task TwoInstancesOverOneDatabaseIssueTheSameNonce()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);

        var podA = StoreOn(schema, new ManualTimeProvider(EpochStart));
        var podB = StoreOn(schema, new ManualTimeProvider(EpochStart.AddSeconds(11)));

        var fromA = Require(await podA.IssueAsync(ct));
        var fromB = Require(await podB.IssueAsync(ct));

        Assert.Equal(fromA.Value, fromB.Value);
        Assert.Equal(fromA.ExpiresAt, fromB.ExpiresAt);
        Assert.True(Require(await podB.IsCurrentAsync(fromA.Value, ct)));
    }

    /// <summary>
    /// R5.19's rotation, and the deliberate acceptance of the previous value across the boundary.
    /// A store that honored only the newest nonce would refuse every request in flight at the
    /// instant of rotation -- a random-looking failure for a client and a rotation bug for an
    /// operator.
    /// </summary>
    [Fact]
    public async Task TheNonceRotatesOnThePublishedIntervalAndThePreviousOneIsStillAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);
        var clock = new ManualTimeProvider(EpochStart);
        var store = StoreOn(schema, clock);

        var first = Require(await store.IssueAsync(ct));

        // One second short of the interval: still the same nonce, so the rotation below is
        // demonstrably driven by the interval rather than by every call minting a fresh value.
        clock.Set(EpochStart + Rotation - TimeSpan.FromSeconds(1));
        Assert.Equal(first.Value, Require(await store.IssueAsync(ct)).Value);

        clock.Set(EpochStart + Rotation);
        var second = Require(await store.IssueAsync(ct));

        Assert.NotEqual(first.Value, second.Value);
        Assert.True(Require(await store.IsCurrentAsync(second.Value, ct)));
        Assert.True(Require(await store.IsCurrentAsync(first.Value, ct)));
    }

    /// <summary>
    /// The other half of "current and previous": two rotations on, the first nonce is no longer
    /// accepted. Without this the acceptance above would be indistinguishable from a store that
    /// never expired anything, which is a nonce mechanism with its point removed.
    /// </summary>
    [Fact]
    public async Task ANonceTwoRotationsOldIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);
        var clock = new ManualTimeProvider(EpochStart);
        var store = StoreOn(schema, clock);

        var first = Require(await store.IssueAsync(ct));

        clock.Set(EpochStart + Rotation);
        await store.IssueAsync(ct);

        clock.Set(EpochStart + Rotation + Rotation);
        var third = Require(await store.IssueAsync(ct));

        Assert.True(Require(await store.IsCurrentAsync(third.Value, ct)));
        Assert.False(Require(await store.IsCurrentAsync(first.Value, ct)));
    }

    /// <summary>
    /// A nonce is not single-use -- RFC 9449 §8 has the server accept any proof carrying the
    /// currently active value for as long as it remains current. Repetition of one <i>proof</i> is
    /// the <c>jti</c> replay cache's job, and conflating the two would make every legitimate
    /// second request fail.
    /// </summary>
    [Fact]
    public async Task CheckingANonceDoesNotConsumeIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);
        var store = StoreOn(schema, new ManualTimeProvider(EpochStart));

        var nonce = Require(await store.IssueAsync(ct));

        Assert.True(Require(await store.IsCurrentAsync(nonce.Value, ct)));
        Assert.True(Require(await store.IsCurrentAsync(nonce.Value, ct)));
        Assert.True(Require(await store.IsCurrentAsync(nonce.Value, ct)));
    }

    /// <summary>
    /// R5.19 caps the rotation interval; a store configured beyond it would be issuing a value
    /// whose freshness guarantee is weaker than the requirement states, silently. Refused at
    /// construction, where it is one operator's mistake, rather than at some later audit.
    /// </summary>
    [Fact]
    public void ARotationIntervalLongerThanTheRequirementPermitsIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostgresDpopNonceStore(
            _fixture.AppRoleDataSource,
            new ManualTimeProvider(EpochStart),
            Rotation + TimeSpan.FromSeconds(1)));
    }
}
