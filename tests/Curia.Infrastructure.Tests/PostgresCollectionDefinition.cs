using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Infrastructure.Tests;

/// <summary>
/// Ties every test class in this project to one shared <see cref="PostgresDatabaseFixture"/>
/// instance -- one throwaway database and role per test run (see that type's remarks), not
/// one per test class.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1711:Identifiers should not have incorrect suffix",
    Justification = "Named for xUnit's own [CollectionDefinition]/[Collection] pairing convention " +
        "('...CollectionDefinition' is the customary name for this marker class across the xUnit " +
        "ecosystem), not for System.Collections purposes; it does not implement ICollection<T> and " +
        "is not meant to.")]
[SuppressMessage(
    "Design",
    "CA1515:Consider making public types internal",
    Justification = "Must be discoverable by xUnit's reflection-based collection-fixture wiring, " +
        "which mirrors the same requirement test classes themselves have (see the xunit.analyzers " +
        "precedent this repo already follows for every [Fact]-bearing class).")]
[CollectionDefinition(Name)]
public sealed class PostgresCollectionDefinition : ICollectionFixture<PostgresDatabaseFixture>
{
    public const string Name = "Postgres";
}
