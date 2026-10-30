using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// BRB58 (errata G48, R7.27): the three connection pools one Forum process holds sum to its configured Maximum Pool Size. Read by reflection on the
/// adapters' own <see cref="NpgsqlDataSource"/> fields and compared with the database's limit read from the test server, not with the other pools. No
/// connection is opened by building a data source, so the arithmetic needs no Forum.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "See PostgresEventStoreContractTests: xUnit discovery needs the concrete class public.")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class AgentWriteGatePoolArithmeticTests(PostgresDatabaseFixture fixture)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ManualTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero));

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private string WithPoolSize(int size) =>
        new NpgsqlConnectionStringBuilder(fixture.AppRoleConnectionString) { MaxPoolSize = size, MinPoolSize = 0 }.ConnectionString;

    /// <summary>The sizes of the data sources an adapters object holds, by field name, and whether any two fields are the same object.</summary>
    private static (SortedDictionary<string, int> Sizes, bool Distinct) PoolsOf(PostgresAdapters adapters)
    {
        var fields = typeof(PostgresAdapters).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(f => typeof(NpgsqlDataSource).IsAssignableFrom(f.FieldType))
            .ToArray();
        var sources = fields.ToDictionary(f => f.Name, f => (NpgsqlDataSource)f.GetValue(adapters)!);
        var sizes = new SortedDictionary<string, int>(
            sources.ToDictionary(p => p.Key, p => new NpgsqlConnectionStringBuilder(p.Value.ConnectionString).MaxPoolSize),
            StringComparer.Ordinal);
        return (sizes, sources.Values.Distinct().Count() == sources.Count);
    }

    /// <summary>BRB58, the carve: for each configured size the agent, gate and Forum pools are floor(S/4), floor(S/4) and S - 2 x floor(S/4), distinct, and sum to S.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(40)]
    [InlineData(80)]
    [InlineData(100)]
    public async Task R7_27_BRB58_ThePoolsSumToTheConfiguredSize(int configured)
    {
        await using var adapters = new PostgresAdapters(WithPoolSize(configured), Clock(), new PermissiveLifecycleAppendGate());
        var (sizes, distinct) = PoolsOf(adapters);

        var quarter = Math.Max(1, configured / 4);
        var expected = new[] { quarter, quarter, configured - (2 * quarter) }.Order().ToArray();
        var actual = sizes.Values.Order().ToArray();
        var sum = actual.Sum();
        var forum = actual.Length == 0 ? 0 : actual.Max();

        Said(
            $"three distinct pools of {string.Join(", ", expected)} summing to {configured}; the Forum's at least 2",
            $"{sizes.Count} {(distinct ? "distinct" : "shared")} pools of {string.Join(", ", actual)} summing to {sum}; the Forum's {(forum >= 2 ? "at least 2" : "under 2")}");
    }

    /// <summary>BRB58, the refusal: a size that cannot carve three pools is refused at construction with the constructor's own words naming Maximum Pool Size.</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public async Task R7_27_BRB58_ASizeThatCannotBeCarvedIsRefusedAtStartup(int configured)
    {
        string outcome;
        try
        {
            var adapters = new PostgresAdapters(WithPoolSize(configured), Clock(), new PermissiveLifecycleAppendGate());
            await adapters.DisposeAsync();
            outcome = "constructed";
        }
        catch (InvalidOperationException refused)
        {
            outcome = refused.Message.Contains("Maximum Pool Size", StringComparison.Ordinal) ? "refused naming Maximum Pool Size" : "refused: " + refused.Message;
        }

        Said($"size {configured} refused naming Maximum Pool Size", $"size {configured} {outcome}");
    }

    /// <summary>
    /// BRB58, the database: the README's example size (80, and any size at most 97 on this server) fits the database's usable connections, read from the test
    /// server (<c>max_connections</c> less <c>superuser_reserved_connections</c>), and the carve never adds: the pools of a size sum to the size and not to
    /// the 125 that a quarter added to each would give.
    /// </summary>
    [Fact]
    public async Task R7_27_BRB58_TheCarvedSumFitsTheDatabasesUsableConnections()
    {
        await using var command = fixture.AdminDataSource.CreateCommand(
            "SELECT current_setting('max_connections')::int - current_setting('superuser_reserved_connections')::int;");
        var usable = Convert.ToInt32(await command.ExecuteScalarAsync(Ct), CultureInfo.InvariantCulture);

        const int Configured = 80;
        await using var adapters = new PostgresAdapters(WithPoolSize(Configured), Clock(), new PermissiveLifecycleAppendGate());
        var (sizes, _) = PoolsOf(adapters);
        var sum = sizes.Values.Sum();

        Said(
            $"the pools of size {Configured} sum to at most {Configured} and at most the {usable} usable connections",
            $"the pools of size {Configured} sum to {sum}{(sum <= Configured ? string.Empty : ", more than " + Configured)}{(sum <= usable ? string.Empty : ", more than the " + usable + " usable connections")}; {(sizes.Count == 3 ? "three" : sizes.Count.ToString(CultureInfo.InvariantCulture))} pools");
    }
}
