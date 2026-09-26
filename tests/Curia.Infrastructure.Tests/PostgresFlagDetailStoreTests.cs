using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "See PostgresEventStoreContractTests: xUnit discovery needs the concrete class public.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresFlagDetailStoreContractTests : FlagDetailStorePortContractTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresFlagDetailStoreContractTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    protected override IFlagDetailStore CreateStore()
    {
        var schema = _fixture.CreateIsolatedFlagDetailSchemaAsync().GetAwaiter().GetResult();
        return new PostgresFlagDetailStore(_fixture.AppRoleDataSource, schema);
    }
}

/// <summary>
/// db/0004's grant, proved the way <see cref="AppRoleGrantRefusalTests"/> proves R11.6's: on a
/// connection opened as the application role, asserting Postgres's own insufficient-privilege state.
/// One test per privilege, each over constant SQL, so a failure names the privilege that was granted.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FlagDetailGrantTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public FlagDetailGrantTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>The positive control: the role can write and read, so the refusals below are narrow revokes and not a role with no access.</summary>
    [Fact]
    public async Task TheAppRoleCanInsertAndSelectFlagDetails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO flag_details (event_id, post_id, raised_by, rationale, salt) " +
            "VALUES ('grant-positive-control', 'p', 'r', 'why', 's');", connection))
        {
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
        }

        await using var select = new NpgsqlCommand(
            "SELECT count(*) FROM flag_details WHERE event_id = 'grant-positive-control';", connection);
        Assert.Equal(1L, (long)(await select.ExecuteScalarAsync(ct))!);
    }

    /// <summary>R11.6 applied to the private store (R11.32): no UPDATE, by grant rather than by restraint.</summary>
    [Fact]
    public async Task R11_32_TheAppRoleCannotUpdateAFlagDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "UPDATE flag_details SET rationale = 'rewritten' WHERE event_id = 'no-such-row';", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("permission denied for table flag_details", ex.MessageText, StringComparison.Ordinal);
    }

    /// <summary>R11.6 applied to the private store (R11.32): no DELETE, by grant rather than by restraint.</summary>
    [Fact]
    public async Task R11_32_TheAppRoleCannotDeleteAFlagDetail()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand(
            "DELETE FROM flag_details WHERE event_id = 'no-such-row';", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("permission denied for table flag_details", ex.MessageText, StringComparison.Ordinal);
    }
}
