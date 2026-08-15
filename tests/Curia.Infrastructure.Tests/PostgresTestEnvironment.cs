using Npgsql;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// Resolves the admin-capable Postgres connection string these tests run against, and fails
/// loudly -- naming the environment variable -- when nothing is reachable, rather than quietly
/// skipping. A green Infrastructure test suite that silently ran nothing is exactly the failure
/// mode R11.9 exists to prevent (rebuild-by-replay must be *exercised*, not assumed), and the
/// same principle applies one level up: these tests existing in the tree proves nothing if they
/// can quietly no-op.
/// </summary>
internal static class PostgresTestEnvironment
{
    /// <summary>
    /// The one environment variable this whole test project reads. An admin-capable (able to
    /// CREATE DATABASE / CREATE ROLE) Npgsql connection string. CI should point this at
    /// whatever Postgres instance it provisions -- a Testcontainers-managed
    /// <c>postgres:18</c> (or <c>pgvector/pgvector:pg18</c>, matching the scoping doc's
    /// eventual pgvector need) container is the natural choice, exposing its mapped port as
    /// e.g. <c>Host=localhost;Port=&lt;mapped&gt;;Username=postgres;Password=postgres;Database=postgres</c>
    /// -- nothing in this project depends on Testcontainers itself, only on being handed a
    /// connection string that works, so CI's provisioning mechanism is free to change without
    /// touching a test.
    /// </summary>
    public const string EnvVarName = "CURIA_TEST_POSTGRES";

    /// <summary>
    /// The fallback used when <see cref="EnvVarName"/> is unset: the local Postgres server
    /// this repository's Stage 2 brief describes as already running and reachable over TCP on
    /// the default port, connecting as whatever OS user is running the tests (mirrors how
    /// <c>psql</c> itself defaults) with no password -- the exact shape verified by hand
    /// against this environment's Homebrew Postgres 18 before Stage 2 was dispatched. A
    /// deployment whose local Postgres needs a password, a non-default port, or a genuine
    /// Unix-socket connection sets <see cref="EnvVarName"/> explicitly instead; this default
    /// exists only to make an unconfigured `dotnet test` on a machine like this one work
    /// without ceremony.
    /// </summary>
    private static string DefaultAdminConnectionString => new NpgsqlConnectionStringBuilder
    {
        Host = "localhost",
        Port = 5432,
        Username = Environment.UserName,
        Database = "postgres",
        Timeout = 5,
    }.ConnectionString;

    public static string ResolveAdminConnectionString()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvVarName);
        return string.IsNullOrWhiteSpace(fromEnv) ? DefaultAdminConnectionString : fromEnv;
    }

    /// <summary>
    /// Opens and immediately closes a connection using <paramref name="adminConnectionString"/>,
    /// so a fixture can fail its very first step with a clear, environment-variable-naming
    /// message instead of a bare Npgsql exception several stack frames into database/role
    /// setup.
    /// </summary>
    public static async Task EnsureReachableAsync(string adminConnectionString, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(adminConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Curia.Infrastructure.Tests could not reach a Postgres server (env var '{EnvVarName}' " +
                $"{(Environment.GetEnvironmentVariable(EnvVarName) is null ? "is unset; used the local default" : "is set")}). " +
                $"Stage 2's tests require a real, reachable, admin-capable Postgres -- set {EnvVarName} to " +
                "an Npgsql connection string for one (CI: point it at a Testcontainers-provisioned " +
                "instance), or start a local server. These tests fail rather than skip when none is " +
                $"reachable, by design. Original error: {ex.Message}",
                ex);
        }
    }
}
