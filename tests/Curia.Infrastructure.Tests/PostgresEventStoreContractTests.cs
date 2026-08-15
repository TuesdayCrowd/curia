using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.Application.Tests;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// Runs the shared <see cref="EventStorePortContractTests"/> suite against
/// <see cref="PostgresEventStore"/> -- the same contract <c>InMemoryEventStoreTests</c> holds
/// the Stage 1 adapter to, so a real difference between the two adapters shows up as a failing
/// inherited test here rather than as an untested divergence.
///
/// <see cref="CreateStore"/> builds the store on <see cref="PostgresDatabaseFixture.AppRoleDataSource"/>,
/// not the admin connection: this contract suite therefore exercises the adapter under the
/// exact R11.6-constrained privileges (INSERT, SELECT only) it will actually run with in
/// production, not a superuser connection that would let a latent bug (e.g. an accidental
/// UPDATE) pass unnoticed.
/// </summary>
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "Must be public for xUnit to discover it as a concrete test class: it carries no " +
        "[Fact] of its own (every test comes from the inherited EventStorePortContractTests base), so " +
        "the analyzer's own test-class heuristic does not recognize it the way it recognizes a class " +
        "with direct [Fact] methods -- but xUnit's own discovery still requires the class public " +
        "regardless of where its [Fact]s are declared.")]
[Collection(PostgresCollectionDefinition.Name)]
public sealed class PostgresEventStoreContractTests : EventStorePortContractTests
{
    private readonly PostgresDatabaseFixture _fixture;

    public PostgresEventStoreContractTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    protected override IEventStore CreateStore()
    {
        // CreateStore() is synchronous (the abstract suite's contract), so the async schema
        // provisioning is blocked on here -- the same accepted pattern
        // EventStorePortContractTests' own property test already uses for AppendAsync via
        // GetAwaiter().GetResult(). A fresh schema per call, not a shared-table TRUNCATE: see
        // PostgresDatabaseFixture.CreateIsolatedSchemaAsync's remarks for why this suite
        // specifically needs true per-call isolation, including under CsCheck's own
        // concurrent case execution.
        var schema = _fixture.CreateIsolatedSchemaAsync().GetAwaiter().GetResult();
        return new PostgresEventStore(_fixture.AppRoleDataSource, TimeProvider.System, schema);
    }
}
