using System.Diagnostics.CodeAnalysis;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// db/0002's grants, proved the way <see cref="AppRoleGrantRefusalTests"/> proves R11.6's: on a
/// connection opened AS the throwaway application role, asserting on Postgres's own
/// insufficient-privilege SQLSTATE rather than on "some exception was thrown."
///
/// <para>Three tables, three deliberately different privilege sets, and the point of testing them
/// is that the differences are load-bearing rather than incidental:</para>
/// <list type="bullet">
/// <item><c>events</c> is the system of record: INSERT and SELECT only (R11.6), covered by
/// <see cref="AppRoleGrantRefusalTests"/>.</item>
/// <item><c>authn_replay</c> and <c>authn_dpop_nonces</c> hold intrinsically expiring state, so
/// they need UPDATE (R5.17's atomic compare-and-set over an expired entry) and DELETE (collecting
/// what has expired). A reader who knows R11.6 should be able to see the difference asserted, not
/// merely asserted about in a comment.</item>
/// <item><c>agent_keys</c> is the third discipline: UPDATE yes -- revocation closes a validity
/// window in place -- DELETE no, because R4.19 requires revoked <c>kid</c>s retained indefinitely
/// with their interval, "because verifying a historical signature requires knowing what was valid
/// when it was made."</item>
/// </list>
/// </summary>
[Collection(PostgresCollectionDefinition.Name)]
public sealed class OperationalStateGrantTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public OperationalStateGrantTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The privilege the two caches must have and <c>events</c> must not. Table-level privileges
    /// are checked before a statement's WHERE is evaluated, so these succeed (affecting no rows)
    /// on privilege grounds alone -- which keeps the test independent of any adapter's behavior.
    /// </summary>
    [SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "The interpolated table and column names come from this method's own " +
            "[InlineData] literals -- values written three lines above -- not from any input a test " +
            "subject or a user supplies.")]
    [Theory]
    [InlineData("authn_replay", "jti")]
    [InlineData("authn_dpop_nonces", "nonce")]
    public async Task TheAppRoleCanUpdateAndDeleteTheExpiringCaches(string table, string keyColumn)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);

        await using (var update = new NpgsqlCommand(
            $"UPDATE {table} SET expires_at = now() WHERE {keyColumn} = 'no-such-row';", connection))
        {
            Assert.Equal(0, await update.ExecuteNonQueryAsync(ct));
        }

        await using var delete = new NpgsqlCommand(
            $"DELETE FROM {table} WHERE {keyColumn} = 'no-such-row';", connection);
        Assert.Equal(0, await delete.ExecuteNonQueryAsync(ct));
    }

    /// <summary>
    /// R4.19 made mechanical. Revocation is an UPDATE of <c>valid_until</c> on a row that stays;
    /// there is no operation the application can perform that removes a key from the history, and
    /// that is the grant's doing rather than the code's restraint.
    /// </summary>
    [Fact]
    public async Task TheAppRoleCanUpdateAgentKeysButCannotDeleteThem()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);

        await using (var revoke = new NpgsqlCommand(
            "UPDATE agent_keys SET valid_until = now() WHERE kid = 'no-such-kid';", connection))
        {
            Assert.Equal(0, await revoke.ExecuteNonQueryAsync(ct));
        }

        await using var delete = new NpgsqlCommand("DELETE FROM agent_keys WHERE kid = 'no-such-kid';", connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => delete.ExecuteNonQueryAsync(ct));

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Contains("permission denied for table agent_keys", ex.MessageText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The positive control the whole file needs: the same role really can read and write these
    /// tables, so the refusal above is a narrow REVOKE rather than a role with no access at all --
    /// which would make the DELETE refusal true for entirely the wrong reason.
    /// </summary>
    [Fact]
    public async Task TheAppRoleCanInsertAndSelectAgentKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from) " +
            "VALUES ('kid-grant-positive-control', 'agent://forum/control', 'ES256', '\\x00'::bytea, now());",
            connection))
        {
            Assert.Equal(1, await insert.ExecuteNonQueryAsync(ct));
        }

        await using var select = new NpgsqlCommand(
            "SELECT count(*) FROM agent_keys WHERE kid = 'kid-grant-positive-control';", connection);
        Assert.Equal(1L, (long)(await select.ExecuteScalarAsync(ct))!);
    }

    /// <summary>
    /// R4.15's algorithm restriction, enforced by the CHECK constraint db/0002 carries over from
    /// Appendix D verbatim: an <c>HS256</c> or <c>RS256</c> key cannot be stored at all, whatever
    /// an application layer above it believes.
    /// </summary>
    [Fact]
    public async Task AKeyWithAnAlgorithmOutsideThePinnedAllowListIsRefusedByTheDatabase()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await _fixture.AppRoleDataSource.OpenConnectionAsync(ct);

        await using var insert = new NpgsqlCommand(
            "INSERT INTO agent_keys (kid, agent_id, alg, public_key, valid_from) " +
            "VALUES ('kid-bad-alg', 'agent://forum/control', 'HS256', '\\x00'::bytea, now());",
            connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync(ct));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }
}
