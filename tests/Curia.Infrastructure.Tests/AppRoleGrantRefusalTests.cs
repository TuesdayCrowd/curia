using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// R11.6, proved the way the Stage 2 brief insists on: connected AS the throwaway application
/// role (never the admin/superuser connection every other fixture operation uses), issuing
/// UPDATE and DELETE directly against <c>events</c> and asserting the *server* refuses them --
/// specifically with Postgres's insufficient-privilege SQLSTATE (42501), not merely "some
/// exception was thrown." A test that only checked "threw" would also pass if the table did
/// not exist, or the connection failed, or a dozen other conditions that prove nothing about
/// the grant; this is the test the whole stage exists for, so it asserts on the refusal itself.
///
/// Deliberately does not insert a row first: Postgres checks table-level privileges before
/// evaluating a statement's WHERE clause, so <c>UPDATE events SET ... WHERE seq = -1</c> is
/// refused on privilege grounds alone, with or without matching rows -- this keeps the test
/// independent of whether AppendAsync (a different code path, covered elsewhere) happens to
/// work.
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AppRoleGrantRefusalTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public AppRoleGrantRefusalTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AppRoleCannotUpdateEvents()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("UPDATE events SET event_type = 'tampered' WHERE seq = -1;", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("permission denied for table events", ex.MessageText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppRoleCannotDeleteEvents()
    {
        var ct = TestContext.Current.CancellationToken;

        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("DELETE FROM events WHERE seq = -1;", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(ct));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("permission denied for table events", ex.MessageText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The positive control: the same role, on the same table, CAN insert and select -- so the
    /// two tests above are demonstrating a real, narrow REVOKE (INSERT/SELECT still work),
    /// not a role that simply lacks all access to the table (which would make the UPDATE/
    /// DELETE refusals trivially true for the wrong reason).
    /// </summary>
    [Fact]
    public async Task AppRoleCanInsertAndSelectEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        await _fixture.ResetEventsTableAsync(ct);

        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO events (event_id, event_type, aggregate_id, payload, server_ts) " +
            "VALUES ('positive-control', 'test.event', 'agg-positive-control', '{}', now());",
            connection))
        {
            var inserted = await insert.ExecuteNonQueryAsync(ct);
            Assert.Equal(1, inserted);
        }

        await using var select = new NpgsqlCommand("SELECT count(*) FROM events;", connection);
        var count = (long)(await select.ExecuteScalarAsync(ct))!;
        Assert.Equal(1L, count);
    }
}
