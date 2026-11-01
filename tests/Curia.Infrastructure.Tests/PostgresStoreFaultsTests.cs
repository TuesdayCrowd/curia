using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// ULG12 (errata G52, R11.71): the one predicate for a fault of a Postgres store, as a truth table. It is pure, so it is real on the seam tree and these facts
/// are a control there; the harm that turns them red is a predicate that drops <see cref="TimeoutException"/>, that answers true for any exception, or that
/// loses a level of depth (FULG2, FULG3, FULG22). The depth is read from the errata, not from the code (R11.71's words).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresStoreFaultsTests(PostgresDatabaseFixture fixture)
{
    private static NpgsqlException Driver() => new("pool exhausted", new TimeoutException("The operation has timed out."));

    private async Task<PostgresException> RealPostgresExceptionAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var connection = await fixture.AdminDataSource.OpenConnectionAsync(ct);
        await using var command = new NpgsqlCommand("SELECT 1/0;", connection);
        return await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteScalarAsync(ct));
    }

    /// <summary>Wraps <paramref name="inner"/> in <paramref name="levels"/> exceptions alternating the two wrappers a driver fault is seen inside.</summary>
    private static Exception Wrapped(Exception inner, int levels)
    {
        var current = inner;
        for (var i = 0; i < levels; i++)
            current = i % 2 == 0 ? new InvalidOperationException("wrapper " + i, current) : new AggregateException("aggregate " + i, current);
        return current;
    }

    [Fact]
    public async Task R11_71_ULG12_AFaultOfTheDriverIsAStoreFaultBareAndWrappedToTheDepthTheErrataPublishes()
    {
        var depth = PublishedStoreFaults.Depth;
        var real = await RealPostgresExceptionAsync();
        var inside = new Exception[] { Driver(), real, new TimeoutException("bare timeout") };

        var problems = new List<string>();
        foreach (var fault in inside)
        {
            foreach (var levels in new[] { 0, 1, 3, depth })
                if (!PostgresStoreFaults.Is(Wrapped(fault, levels))) problems.Add($"{fault.GetType().Name} {levels} deep is not a store fault");
            if (PostgresStoreFaults.Is(Wrapped(fault, depth + 1))) problems.Add($"{fault.GetType().Name} {depth + 1} deep is a store fault, past the published depth");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// ULG54, the predicate. A fault two, four and the published depth levels down is a store fault (so a walk that stops after the first wrapper is red), and one
    /// level past the published depth is not (so a walk that never stops is). The depth is read from the errata.
    /// </summary>
    [Fact]
    public async Task R11_71_ULG54_AFaultTwoFourAndEightLevelsDownIsFoundAndOneBeyondThePublishedDepthIsNot()
    {
        var depth = PublishedStoreFaults.Depth;
        Assert.True(depth >= 4, $"the published depth is {depth}");
        var real = await RealPostgresExceptionAsync();

        var problems = new List<string>();
        foreach (var fault in new Exception[] { Driver(), real, new TimeoutException("bare timeout") })
        {
            foreach (var levels in new[] { 2, 4, depth })
                if (!PostgresStoreFaults.Is(Wrapped(fault, levels))) problems.Add($"{fault.GetType().Name} {levels} deep is not found");
            if (PostgresStoreFaults.Is(Wrapped(fault, depth + 1))) problems.Add($"{fault.GetType().Name} {depth + 1} deep is found, past the published depth");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void R11_71_ULG12_ABugAndTheCallersCancellationAreNotStoreFaults()
    {
        var notFaults = new Exception[]
        {
            new InvalidOperationException("a bug"),
            new OperationCanceledException("the caller's"),
            new ArgumentException("also a bug"),
            new InvalidOperationException("a wrapper", new ArgumentException("of a bug")),
            new AggregateException(new OperationCanceledException()),
        };

        var named = notFaults.Where(PostgresStoreFaults.Is).Select(e => e.GetType().Name).ToArray();

        Assert.True(named.Length == 0, "the predicate named a bug or a cancellation as a store fault: " + string.Join(", ", named));
    }

    [Fact]
    public void R11_71_ULG12_ThePortsImplementationAnswersAsThePredicateDoes()
    {
        var classifier = Assert.IsAssignableFrom<IStoreFaultClassifier>(new PostgresStoreFaults());

        Assert.True(
            classifier.IsUnavailable(Driver()) && !classifier.IsUnavailable(new InvalidOperationException("a bug")),
            "the port's implementation disagrees with the predicate");
    }
}
