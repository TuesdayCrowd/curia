using Curia.Api.Issuer;
using Curia.Infrastructure.Migrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// A settable clock, so a test can advance eight days without waiting eight days. Table 11's tier
/// criteria are elapsed-time conditions, and a test that could not move time could only ever
/// exercise T0.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>
/// The real Forum, hosted in process: the same <see cref="Program.Build"/> the deployed host runs,
/// with three things supplied -- the clock (so tier promotion is testable), the connection string
/// (so each run gets its own database), and the issuer's signing key (which the host requires and
/// deliberately does not generate for itself; see <see cref="IssuerSigningKey"/>).
///
/// <para><b>Nothing else is substituted.</b> The pipeline, the PDP, the screener and the Postgres
/// event store are the production ones. A fixture that swapped the store for an in-memory fake
/// would be testing a different system: R11.6's append-only guarantee is a database grant, and the
/// whole claim under test is that a real Forum behaves this way.</para>
///
/// <para>Fails loudly when no Postgres is reachable rather than skipping, for the reason
/// <c>Curia.Infrastructure.Tests</c> already records: a green suite that quietly ran nothing is
/// the exact failure R11.9 exists to prevent.</para>
/// </summary>
public sealed class ForumFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string EnvVarName = "CURIA_TEST_POSTGRES";

    private readonly string _database = "curia_e2e_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _role = "curia_e2e_role_" + Guid.NewGuid().ToString("N")[..12];
    private readonly string _password = Guid.NewGuid().ToString("N");

    private string _connectionString = string.Empty;

    /// <summary>
    /// The issuer's signing key for this fixture's whole life, as a PEM. Public so a test that
    /// restarts the host can stand the second one up on the same key and check that what the
    /// first minted still verifies.
    /// </summary>
    internal string IssuerSigningKeyPem { get; } = IssuerSigningKey.GeneratePem();

    internal ManualTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));

    internal DateTimeOffset Now => Clock.GetUtcNow();

    internal HttpClient Client => CreateClient();

    private static string AdminConnectionString =>
        Environment.GetEnvironmentVariable(EnvVarName)
        ?? new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Port = 5432,
            Username = Environment.UserName,
            Database = "postgres",
            Timeout = 5,
        }.ConnectionString;

    /// <summary>
    /// Provisions a throwaway database and applies <b>every</b> migration in <c>db/</c> through the
    /// production renderer -- so the schema under test is the one a deployment gets, including
    /// R11.6's <c>REVOKE UPDATE, DELETE</c> on <c>events</c> and 0002's deliberately different
    /// grants on the operational tables. A fixture that hand-wrote equivalent DDL would be testing
    /// its own transcription; one that named a single migration file would go on passing while a
    /// later migration was missing from the database it provisions.
    /// </summary>
    public async ValueTask InitializeAsync()
    {
        await using var admin = new NpgsqlDataSourceBuilder(AdminConnectionString).Build();

        try
        {
            await using var probe = await admin.OpenConnectionAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Curia.Api.Tests could not reach a Postgres server (env var '{EnvVarName}' " +
                $"{(Environment.GetEnvironmentVariable(EnvVarName) is null ? "unset; used the local default" : "set")}). " +
                "The Forum has no in-memory production event store on purpose -- R11.6's append-only " +
                "guarantee is a database grant -- so these tests fail rather than skip. " +
                $"Original error: {ex.Message}",
                ex);
        }

        await using (var create = admin.CreateCommand($"CREATE DATABASE \"{_database}\""))
            await create.ExecuteNonQueryAsync();

        var builder = new NpgsqlConnectionStringBuilder(AdminConnectionString) { Database = _database };
        _connectionString = builder.ConnectionString;

        var sql = SchemaMigrations.RenderAll(_role, _password);

        await using var target = new NpgsqlDataSourceBuilder(_connectionString).Build();
        await using var apply = target.CreateCommand(sql);
        await apply.ExecuteNonQueryAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        await using var admin = new NpgsqlDataSourceBuilder(AdminConnectionString).Build();
        await using (var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)"))
            await drop.ExecuteNonQueryAsync();
        await using (var dropRole = admin.CreateCommand($"DROP ROLE IF EXISTS \"{_role}\""))
            await dropRole.ExecuteNonQueryAsync();

        GC.SuppressFinalize(this);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseSetting("ConnectionStrings:Events", _connectionString);

        // The issuer signing key is configuration, not something the host generates (see
        // Curia.Api.Issuer.IssuerSigningKey). One PEM per fixture, held for the fixture's whole
        // life, is what lets a test restart the host and find the tokens the previous one minted
        // still verifying -- the property a per-process key could not have. Generated rather than
        // checked in, because a signing key committed to a repository is the thing R4.20 names
        // first among the places a key must never be.
        builder.UseSetting("Curia:IssuerSigningKeyPem", IssuerSigningKeyPem);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }
}
