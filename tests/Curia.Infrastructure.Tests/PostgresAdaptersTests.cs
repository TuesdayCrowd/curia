using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// <see cref="PostgresAdapters"/>' start-up refusal (R7.22; errata G18, review of 980fb0e). The raiser
/// gate draws on a connection pool of its own, a quarter of the Forum's, so a hold never waits on the
/// pool it is keeping a connection from. A Forum pool too small to split leaves no room for that, and
/// is refused at start-up rather than run with a gate that can deadlock it. No database is reached:
/// building a data source opens no connection.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PostgresAdaptersTests
{
    private const string Unreached = "Host=localhost;Port=5432;Database=curia_never_opened;Username=curia";

    private static ManualTimeProvider Clock() => new(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public void R7_22_APoolTooSmallToSplitIsRefusedAtStartup()
    {
        var refused = Assert.Throws<InvalidOperationException>(
            () => new PostgresAdapters(Unreached + ";Maximum Pool Size=1", Clock()));

        Assert.Contains("R7.22", refused.Message, StringComparison.Ordinal);
    }

    /// <summary>The positive control: the smallest pool that splits constructs, so the refusal above is the pool's size and not the connection string.</summary>
    [Fact]
    public async Task R7_22_APoolOfTwoIsAccepted()
    {
        var adapters = new PostgresAdapters(Unreached + ";Maximum Pool Size=2", Clock());
        await adapters.DisposeAsync();
    }
}
