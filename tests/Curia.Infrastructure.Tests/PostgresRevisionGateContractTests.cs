using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ingest;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Npgsql;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// <see cref="RevisionGatePortContractTests"/> over the per-run database (R8.77; errata G45), and the facts only a database can state: the hold is
/// the database's (RVO22), the gate tries and never waits (RVO23), an unreachable store is a refusal that writes nothing (RVO24), and the key carries
/// the schema and a prefix of its own (RVO25). Each gate takes a schema name of its own, which is only the lock key's prefix (the gate reads and
/// writes no table), so no two tests' holds can meet.
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
public sealed class PostgresRevisionGateContractTests : RevisionGatePortContractTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresRevisionGateContractTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    private static string NewSchema() => "gate_" + Guid.NewGuid().ToString("N");

    protected override IRevisionGate CreateGate() => new PostgresRevisionGate(_fixture.AppRoleDataSource, NewSchema());

    /// <summary>RVO22. Two gates on two data sources over one database stand in for two Forum processes.</summary>
    [Fact]
    public async Task R8_77_TheHoldIsSeenAcrossTwoDataSources()
    {
        var ct = TestContext.Current.CancellationToken;
        var schema = NewSchema();
        var first = new PostgresRevisionGate(_fixture.AppRoleDataSource, schema);
        var second = new PostgresRevisionGate(_fixture.AdminDataSource, schema);
        var digest = NewDigest();

        string whileHeld;
        await using (Require(await first.TryEnterAsync(digest, ct)))
            whileHeld = await OutcomeAsync(await second.TryEnterAsync(digest, ct));

        var afterRelease = await OutcomeAsync(await second.TryEnterAsync(digest, ct));

        Assert.Equal(
            $"{IngestErrors.RevisionInFlightType} while held; entered after release",
            $"{whileHeld} while held; {afterRelease} after release");
    }

    /// <summary>RVO23. A blocking lock would hold a pooled connection for every waiter: with the first hold open, the second entry answers a refusal within 2 s (the test cancels at 3 s, so a wait is a red and not a hang).</summary>
    [Fact]
    public async Task R8_77_ASecondEntryIsRefusedAtOnceAndNeverWaits()
    {
        var ct = TestContext.Current.CancellationToken;
        var gate = CreateGate();
        var digest = NewDigest();

        string outcome;
        TimeSpan elapsed;
        await using (Require(await gate.TryEnterAsync(digest, ct)))
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cancel.CancelAfter(TimeSpan.FromSeconds(3));
            var timer = Stopwatch.StartNew();
            try
            {
                outcome = await OutcomeAsync(await gate.TryEnterAsync(digest, cancel.Token));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                outcome = "still waiting at 3 s";
            }

            elapsed = timer.Elapsed;
        }

        Assert.Equal(
            $"{IngestErrors.RevisionInFlightType} within 2 s",
            $"{outcome} {(elapsed < TimeSpan.FromSeconds(2) ? "within 2 s" : "after " + elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " s")}");
    }

    /// <summary>RVO24. A data source whose pool of one is held and whose connection timeout is one second cannot be reached: the entry is the gate-unavailable refusal, and it writes nothing.</summary>
    [Fact]
    public async Task R8_77_AnUnreachableStoreIsGateUnavailableAndWritesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var builder = new NpgsqlConnectionStringBuilder(_fixture.AppRoleDataSource.ConnectionString)
        {
            MaxPoolSize = 1,
            MinPoolSize = 0,
            Timeout = 1,
            Pooling = true,
        };
        await using var starved = NpgsqlDataSource.Create(builder.ConnectionString);
        await using var occupant = await starved.OpenConnectionAsync(ct);
        var gate = new PostgresRevisionGate(starved, NewSchema());

        var outcome = await OutcomeAsync(await gate.TryEnterAsync(NewDigest(), ct));

        Assert.Equal(IngestErrors.RevisionGateUnavailableType, outcome);
    }

    /// <summary>
    /// RVO25. The key is per schema and carries a prefix of its own. The first assertion is the vacuity guard: the same schema and digest do hold each
    /// other, so the independence the others state is the key's and not an absence of any lock.
    /// </summary>
    [Fact]
    public async Task R8_77_TheKeyCarriesTheSchemaAndAPrefixOfItsOwn()
    {
        var ct = TestContext.Current.CancellationToken;
        var digest = NewDigest();
        var schema = NewSchema();
        var gate = new PostgresRevisionGate(_fixture.AppRoleDataSource, schema);

        string sameSchema;
        string otherSchema;
        string flagGate;
        await using (Require(await gate.TryEnterAsync(digest, ct)))
        {
            sameSchema = await OutcomeAsync(await new PostgresRevisionGate(_fixture.AdminDataSource, schema).TryEnterAsync(digest, ct));
            otherSchema = await OutcomeAsync(await new PostgresRevisionGate(_fixture.AdminDataSource, NewSchema()).TryEnterAsync(digest, ct));
        }

        // A flag hold on a raiser named like the digest, in the same schema, does not refuse a revision entry on that string.
        await using (Require(await new PostgresFlagRaiserGate(_fixture.AppRoleDataSource, schema).TryEnterAsync(digest, ct)))
            flagGate = await OutcomeAsync(await gate.TryEnterAsync(digest, ct));

        Assert.Equal(
            $"same schema {IngestErrors.RevisionInFlightType}; other schema entered; beside a flag hold entered",
            $"same schema {sameSchema}; other schema {otherSchema}; beside a flag hold {flagGate}");
    }
}
