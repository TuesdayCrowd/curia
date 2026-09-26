using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Curia.Canon.Jws;
using Curia.Domain.Primitives;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>The Postgres adapter, held to the same enrollment contract as the in-memory one (R11.4).</summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "See PostgresEventStoreContractTests: xUnit discovery needs the concrete class public.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresAuthorKeyRegistryContractTests : AuthorKeyRegistryPortContractTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresAuthorKeyRegistryContractTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    protected override IAuthorKeyRegistry CreateStore()
    {
        var schema = _fixture.CreateIsolatedOperationalSchemaAsync().GetAwaiter().GetResult();
        return new PostgresAgentKeyStore(_fixture.AppRoleDataSource, schema);
    }
}

/// <summary>
/// R4.31's atomicity, observed rather than hoped for: an enrollment waits while its identifier's
/// lock is held, and two enrollments racing for one fresh identifier -- released together -- leave
/// exactly one key. The race is made deterministic by holding the lock while both start, so the
/// outcome does not depend on scheduling; a store that took no lock fails the first assertion,
/// because it finishes while the lock is held.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PostgresEnrollmentSerializationTests
{
    private const string Alice = "https://agents.example/alice";

    private readonly PostgresDatabaseFixture _fixture;

    public PostgresEnrollmentSerializationTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static readonly DateTimeOffset Today = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static PublicKeyMaterial NewKey(string kid)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new PublicKeyMaterial("ES256", kid, ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Takes <paramref name="agentId"/>'s enrollment lock in a transaction of the caller's, as a concurrent enrollment would mid-decision.</summary>
    private static async Task HoldAsync(NpgsqlConnection holder, NpgsqlTransaction holding, string schema, string agentId, CancellationToken ct)
    {
        await using var take = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@k, 0));", holder, holding);
        take.Parameters.AddWithValue("k", PostgresAgentKeyStore.EnrollmentLockKey(schema, agentId));
        await take.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task R4_31_AnEnrollmentWaitsWhileItsIdentifiersLockIsHeld()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);
        var store = new PostgresAgentKeyStore(_fixture.AppRoleDataSource, schema);

        await using var holder = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var holding = await holder.BeginTransactionAsync(ct);
        await HoldAsync(holder, holding, schema, Alice, ct);

        var enroll = store.EnrollAsync(Alice, NewKey("alice-1"), Today, ct);
        var finishedWhileHeld = await Task.WhenAny(enroll, Task.Delay(TimeSpan.FromSeconds(2), ct)) == enroll;
        Assert.False(finishedWhileHeld, "an enrollment decided while its identifier's lock was held by another transaction");

        await holding.CommitAsync(ct);
        Assert.True((await enroll).IsOk);
    }

    [Fact]
    public async Task R4_31_TwoEnrollmentsRacingForOneFreshIdentifierLeaveOneKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedOperationalSchemaAsync(ct);
        var store = new PostgresAgentKeyStore(_fixture.AppRoleDataSource, schema);

        await using var holder = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var holding = await holder.BeginTransactionAsync(ct);
        await HoldAsync(holder, holding, schema, Alice, ct);

        // Both start while the lock is held, so neither can have read the table before the other.
        var first = store.EnrollAsync(Alice, NewKey("alice-1"), Today, ct);
        var second = store.EnrollAsync(Alice, NewKey("alice-2"), Today, ct);
        await Task.WhenAny(Task.WhenAny(first, second), Task.Delay(TimeSpan.FromSeconds(2), ct));
        Assert.False(first.IsCompleted || second.IsCompleted, "an enrollment decided while its identifier's lock was held by another transaction");

        await holding.CommitAsync(ct);
        var outcomes = await Task.WhenAll(first, second);

        Assert.Single(outcomes, o => o.IsOk);
        Assert.Single(outcomes, o => !o.IsOk && o.Match(_ => string.Empty, e => e.Type) == "curia/enroll/already-enrolled");
        Assert.Single(await store.KeysForAsync(Alice, ct));
    }
}
