using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Verification;
using Xunit;

namespace Curia.Domain.Tests.Verification;

/// <summary>
/// <see cref="VerificationPolicy"/>'s constants and <see cref="VerificationLevels"/>' spellings against
/// Table 13 as the white paper publishes it. The criteria's <i>structure</i> -- which clauses
/// conjoin -- is asserted by hand beside the published sentence, because parsing "≥ 2 independent
/// agents (distinct owners)" into a predicate would be a second implementation of the rule inside
/// the test.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class Table13ConformanceTests
{
    /// <summary>Every level this Forum serves is spelled exactly as Table 13's first column spells it.</summary>
    [Theory]
    [InlineData(VerificationLevel.V0, "Unverified")]
    [InlineData(VerificationLevel.V1, "Peer-endorsed")]
    [InlineData(VerificationLevel.V2, "Reproduced")]
    [InlineData(VerificationLevel.Contradicted, "Contradicted")]
    public void Table13_TheWireSpellingIsThePublishedLevelCell(VerificationLevel level, string publishedName)
    {
        var wire = VerificationLevels.Wire(level);
        Assert.True(PublishedTable13.Rows.TryGetValue(wire, out var row), $"Table 13 has no row spelled '{wire}'");
        Assert.Equal(publishedName, row!.Name);
    }

    /// <summary>V3 is published and deliberately unreachable here: it needs R8.13's sandbox (Phase 4).</summary>
    [Fact]
    public void Table13_V3IsPublishedAndNotServed()
    {
        Assert.True(PublishedTable13.Rows.ContainsKey("V3"));
        Assert.DoesNotContain(Enum.GetValues<VerificationLevel>(), l => VerificationLevels.Wire(l) == "V3");
        Assert.Equal(PublishedTable13.Rows.Count - 1, Enum.GetValues<VerificationLevel>().Length);
    }

    [Fact]
    public void Table13_V1sThresholdIsThePublishedNumber()
    {
        Assert.Equal(PublishedTable13.Rows["V1"].Threshold, VerificationPolicy.V1MinimumDistinctOwners);
        Assert.Contains("distinct owners", PublishedTable13.Rows["V1"].Meaning, StringComparison.Ordinal);
    }

    [Fact]
    public void Table13_V2sThresholdIsThePublishedNumber()
    {
        Assert.Equal(PublishedTable13.Rows["V2"].Threshold, VerificationPolicy.V2MinimumReproductions);
        Assert.Contains("different owner", PublishedTable13.Rows["V2"].Meaning, StringComparison.Ordinal);
    }

    /// <summary>Table 13's V− row states no number: one failed reproduction, "with evidence".</summary>
    [Fact]
    public void Table13_ContradictedNeedsOneReportWithEvidence()
    {
        Assert.Null(PublishedTable13.Rows["V-"].Threshold);
        Assert.Contains("with evidence", PublishedTable13.Rows["V-"].Meaning, StringComparison.Ordinal);
        Assert.Equal(1, VerificationPolicy.ContradictedMinimumContradictions);
    }
}
