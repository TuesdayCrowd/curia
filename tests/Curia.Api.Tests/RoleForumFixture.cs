using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>The collection of the one fixture whose host connects as the application role and whose facts revoke its grants: serial, in a database of its own.</summary>
[CollectionDefinition("role-forum", DisableParallelization = true)]
public sealed class RoleForumGroup;

/// <summary>
/// The D95 PR's Task 0b, ruling 13: the Forum hosted in process the way <see cref="ForumFixture"/> hosts it, except that the host <b>connects as the application
/// role</b> (so a <c>REVOKE</c> from it reaches the host: R11.6's grants are exercised, which no other Api fixture does) and runs in the <b>Production</b>
/// environment (so an unhandled exception is the body-less <c>500</c> the deployed Forum answers, and not Development's developer page; ruling 11).
///
/// <para>The role string is built on an <see cref="NpgsqlConnectionStringBuilder"/> with <see cref="NpgsqlConnectionStringBuilder.Username"/> and
/// <see cref="NpgsqlConnectionStringBuilder.Password"/> set explicitly: a string read back from a data source drops the password. Every fault is made on this
/// fixture's own database, by the administrator's connection (<see cref="ForumFixture.ConnectionString"/>), and is restored in the fact's <c>finally</c>;
/// <see cref="AssertRestoredAsync"/> checks the restoration from the catalogue (<c>has_table_privilege</c>, <c>pg_policies</c>, <c>relrowsecurity</c>), and
/// the poison row an RLS fault needs is deleted again by the owner.</para>
/// </summary>
public sealed class RoleForumFixture : ForumFixture
{
    /// <summary>The aggregate of the row only a whole-log scan reaches (Task 0's M2).</summary>
    internal const string PoisonAggregate = "n12-poison";

    private const string PolicyName = "n12_poison_policy";
    private const string FunctionName = "n12_poison_check";

    protected override string HostConnectionString =>
        new NpgsqlConnectionStringBuilder(ConnectionString) { Username = RoleName, Password = RolePassword }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }

    internal Task RevokeSelectOnEventsAsync(CancellationToken ct) => AdminAsync($"REVOKE SELECT ON events FROM \"{RoleName}\";", ct);

    internal Task GrantSelectOnEventsAsync(CancellationToken ct) => AdminAsync($"GRANT SELECT ON events TO \"{RoleName}\";", ct);

    /// <summary>
    /// A row-level-security fault on the role's reads: a poison row exists that only a whole-log scan reaches, and a select policy for the role calls a function
    /// that raises for it. A read of one aggregate (an index on <c>aggregate_id</c>) never evaluates the function on the row.
    /// </summary>
    internal Task CreatePoisonPolicyAsync(CancellationToken ct) => AdminAsync(
        $"""
        INSERT INTO events (event_id, event_type, aggregate_id, actor_id, payload, server_ts)
        VALUES ('{PoisonAggregate}-1', 'poison.row', '{PoisonAggregate}', 'poison', jsonb_build_object(), now());
        CREATE FUNCTION {FunctionName}(agg text) RETURNS boolean LANGUAGE plpgsql SECURITY DEFINER AS $f$
        BEGIN
            IF agg = '{PoisonAggregate}' THEN RAISE EXCEPTION 'poison row reached by a scan'; END IF;
            RETURN true;
        END $f$;
        ALTER TABLE events ENABLE ROW LEVEL SECURITY;
        CREATE POLICY {PolicyName} ON events FOR SELECT TO "{RoleName}" USING ({FunctionName}(aggregate_id));
        CREATE POLICY n12_insert_policy ON events FOR INSERT TO "{RoleName}" WITH CHECK (true);
        """,
        ct);

    internal Task DropPoisonPolicyAsync(CancellationToken ct) => AdminAsync(
        $"""
        DROP POLICY IF EXISTS {PolicyName} ON events;
        DROP POLICY IF EXISTS n12_insert_policy ON events;
        ALTER TABLE events DISABLE ROW LEVEL SECURITY;
        DROP FUNCTION IF EXISTS {FunctionName}(text);
        DELETE FROM events WHERE aggregate_id = '{PoisonAggregate}';
        """,
        ct);

    /// <summary>
    /// The state of every fault this fixture makes, from the catalogue: whether the role may select from <c>events</c> and from the other tables, whether any
    /// policy or row security remains, whether the poison row is gone. Empty where everything is restored; otherwise the words of what is not.
    /// </summary>
    internal async Task<string> RestorationProblemsAsync(CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(ConnectionString);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            """
            SELECT has_table_privilege(@role, 'events', 'SELECT'),
                   has_table_privilege(@role, 'events', 'INSERT'),
                   (SELECT count(*) FROM pg_policies WHERE tablename = 'events'),
                   (SELECT relrowsecurity FROM pg_class WHERE relname = 'events' AND relnamespace = 'public'::regnamespace),
                   (SELECT count(*) FROM events WHERE aggregate_id = @poison);
            """,
            admin);
        command.Parameters.AddWithValue("role", RoleName);
        command.Parameters.AddWithValue("poison", PoisonAggregate);
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var problems = new List<string>();
        if (!reader.GetBoolean(0)) problems.Add("SELECT on events is not granted to the role");
        if (!reader.GetBoolean(1)) problems.Add("INSERT on events is not granted to the role");
        if (reader.GetInt64(2) != 0) problems.Add("a policy remains on events");
        if (reader.GetBoolean(3)) problems.Add("row security remains enabled on events");
        if (reader.GetInt64(4) != 0) problems.Add("the poison row remains");
        return string.Join("; ", problems);
    }

    /// <summary>Fails with the words of what is not restored.</summary>
    internal async Task AssertRestoredAsync(CancellationToken ct)
    {
        var problems = await RestorationProblemsAsync(ct);
        Assert.True(problems.Length == 0, "the role fixture's faults are not restored: " + problems);
    }

    /// <summary>The users the database reports connected, by name, so a fact can say the host's connections are the role's and not the administrator's.</summary>
    internal async Task<string[]> ConnectedUsersAsync(CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(ConnectionString);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand(
            "SELECT DISTINCT usename FROM pg_stat_activity WHERE datname = current_database() AND pid <> pg_backend_pid() ORDER BY usename;", admin);
        var users = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) users.Add(reader.GetString(0));
        return [.. users];
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Every statement is built in this class from the fixture's own generated role name and constants; no caller text reaches it.")]
    private async Task AdminAsync(string sql, CancellationToken ct)
    {
        await using var admin = new NpgsqlConnection(ConnectionString);
        await admin.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, admin);
        await command.ExecuteNonQueryAsync(ct);
    }
}
