using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// <see cref="FlagRaiserGatePortContractTests"/> over the per-run database (R7.22, R10.70; errata G18,
/// review of 4b3e91a). Each gate takes a schema name of its own, which is only the lock key's prefix
/// (the gate reads and writes no table), so no two tests' holds can meet.
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
public sealed class PostgresFlagRaiserGateContractTests : FlagRaiserGatePortContractTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresFlagRaiserGateContractTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static string NewSchema() => "gate_" + Guid.NewGuid().ToString("N");

    protected override IFlagRaiserGate CreateGate() => new PostgresFlagRaiserGate(_fixture.AppRoleDataSource, NewSchema());

    /// <summary>
    /// The hold is the database's, not the process's: two gates on two separate data sources over the
    /// same database stand in for two Forum processes, and a raiser held through one is refused
    /// through the other.
    /// </summary>
    [Fact]
    public async Task R7_22_TheHoldIsSeenAcrossTwoDataSources()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = NewSchema();
        var first = new PostgresFlagRaiserGate(_fixture.AppRoleDataSource, schema);
        var second = new PostgresFlagRaiserGate(_fixture.AdminDataSource, schema);
        var a = NewRaiser("a");

        await using (Require(await first.TryEnterAsync(a, ct)))
        {
            var other = await second.TryEnterAsync(a, ct);
            if (other.TryGetValue(out var leaked, out _)) await leaked!.DisposeAsync();
            AssertInFlight(other);
        }

        await using var after = Require(await second.TryEnterAsync(a, ct));
    }
}
