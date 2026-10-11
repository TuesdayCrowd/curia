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
/// R11.34 (errata G18) at the Postgres adapter: a row written before the types, which no adapter
/// append can write today, is read back as stored and does not take down every read of the store.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PostgresFlagDetailReadBackTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresFlagDetailReadBackTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Before b75b2bf, <c>RaiseFlag</c> answered 201 to a flag against an enrolled agent's id, so a
    /// stored <c>post_id</c> may be no ULID. The row is inserted raw, as the app role, bypassing the
    /// adapter's append. A store that parsed read-back would fail every read until an administrator
    /// deleted the row under R11.32's grant: RaiseFlag's R10.70 read, ApplyModeration, both GET flag
    /// routes and the operator's queue.
    /// </summary>
    [Fact]
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The only interpolated text is the schema name the fixture minted (flg + hex), quoted; every value is a constant.")]
    public async Task R11_34_ARowWrittenBeforeTheTypesDoesNotTakeDownTheStore()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = await _fixture.CreateIsolatedFlagDetailSchemaAsync(ct);

        await using (var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct))
        await using (var insert = new NpgsqlCommand(
            $"INSERT INTO \"{schema}\".flag_details (event_id, post_id, raised_by, rationale, salt) " +
            "VALUES ('e1', 'some-agent', 'raiser', 'why', 's');", connection))
        {
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
        }

        var store = new PostgresFlagDetailStore(_fixture.AppRoleDataSource, schema);
        var wellFormed = new FlagDetail("e2", "01JP0ST0000000000000000001", "https://agents.example/reporter", "looks like spam", "c2FsdA");
        Assert.True((await store.AppendAsync(wellFormed, ct)).IsOk);

        var read = await store.ReadAllAsync(ct);

        Assert.True(read.IsOk);
        var rows = read.Match(r => r, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));
        Assert.Equal(["e1", "e2"], rows.Select(r => r.EventId));
        Assert.Equal("some-agent", rows[0].PostId);
        Assert.Equal(wellFormed, rows[1]);
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
