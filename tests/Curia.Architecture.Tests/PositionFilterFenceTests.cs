using System.Diagnostics.CodeAnalysis;
using Curia.Infrastructure;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// LRV40 (errata G68, PR N's ruling 72): every statement in <see cref="PostgresEventStore"/> that selects or counts by aggregate says <c>seq &gt; 0</c>, so a version, a read by
/// aggregate and a forward read are of one set of rows. Read from the IL's string literals, not the source the filter was written in, so a statement added later is held too.
/// An interpolated string is a run of literals around its holes and a <c>const</c> hole is a literal of its own, so a statement's predicate is the literal that names the aggregate
/// joined to the literal that follows it. The fence finds the three statements it expects (a vacuous pass is a failure).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PositionFilterFenceTests
{
    private const string ByAggregate = "WHERE aggregate_id = @agg";

    [Fact]
    public void LRV40_EveryStatementByAggregateReadsOnlyTheLog()
    {
        var literals = IlCallSites.StringLiterals(typeof(PostgresEventStore).Assembly.Location, typeof(PostgresEventStore).FullName!);
        var statements = literals
            .Select((l, i) => (l.Method, Predicate: i + 1 < literals.Length && literals[i + 1].Method == l.Method ? l.Literal + literals[i + 1].Literal : l.Literal, l.Literal))
            .Where(l => l.Literal.Contains(ByAggregate, StringComparison.Ordinal))
            .Select(l => $"{l.Method}: {(l.Predicate.Contains(ByAggregate + " AND seq > 0", StringComparison.Ordinal) ? "in the log" : "NOT FILTERED")}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["AppendCoreAsync: in the log", "AppendCoreAsync: in the log", "ReadByAggregateAsync: in the log"],
            statements);
    }
}
