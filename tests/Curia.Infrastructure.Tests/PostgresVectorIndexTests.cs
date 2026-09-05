using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Curia.Domain.Search;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "See PostgresEventStoreContractTests: xUnit discovery needs the concrete class public.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresVectorIndexContractTests : VectorIndexPortContractTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresVectorIndexContractTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    protected override IVectorIndex CreateIndex()
    {
        var schema = _fixture.CreateIsolatedRetrievalSchemaAsync().GetAwaiter().GetResult();
        return new PostgresVectorIndex(_fixture.AppRoleDataSource, TimeProvider.System, schema);
    }
}

/// <summary>
/// The parts of db/0003 that are not the port's contract: pgvector is really there, the app
/// role's grants are exactly the migration's, and a query is answered by the extension's own
/// distance operator rather than by anything this solution computes.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RetrievalIndexSchemaTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public RetrievalIndexSchemaTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task R9_4_TheVectorExtensionIsInstalledByTheMigrationNotAssumed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AdminDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT extversion FROM pg_extension WHERE extname = 'vector';", connection);
        var version = await command.ExecuteScalarAsync(ct) as string;
        Assert.False(string.IsNullOrEmpty(version), "db/0003 did not create the vector extension");
    }

    [Fact]
    public async Task R11_10_TheAppRoleMayUpsertButNeverDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);

        await using var delete = new NpgsqlCommand("DELETE FROM post_embeddings WHERE digest = 'no-such-row';", connection);
        var refused = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(ct));
        Assert.Equal("42501", refused.SqlState);
    }

    [Fact]
    public async Task R9_4_DistanceComesFromPgvectorItself()
    {
        var ct = TestContext.Current.CancellationToken;
        var index = new PostgresVectorIndex(_fixture.AppRoleDataSource, TimeProvider.System);
        var model = new EmbeddingModel("schema-test", "1", 2);
        var digest = "sha256:" + Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");

        var stored = await index.UpsertAsync(digest, "p", 1, new Embedding(model, [0.6f, 0.8f]), ct);
        Assert.True(stored.TryGetValue(out _, out var storeError), storeError?.Type);

        // Read the stored row back through pgvector's own operator, bypassing the adapter's query.
        await using var connection = await _fixture.AdminDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT (embedding <=> '[1,0]'::vector) FROM post_embeddings WHERE digest = @digest;", connection);
        command.Parameters.AddWithValue("digest", digest);
        var distance = (double)(await command.ExecuteScalarAsync(ct))!;
        Assert.Equal(0.4, distance, 1e-6);
    }
}
