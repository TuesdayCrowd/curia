using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Authorization;
using Xunit;

namespace Curia.Domain.Tests.Authorization;

/// <summary>
/// The white paper's Table 10 and <see cref="ResourceActionModel"/>, held against each other.
/// Neither is derived from the other, which is the only arrangement in which their agreement is
/// evidence rather than a tautology.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs (R7.6, R7.12) they enforce verbatim, " +
        "mirroring Curia.Architecture.Tests' precedent.")]
public sealed class Table10ConformanceTests
{
    [Fact]
    public void Published_table_and_model_name_the_same_resource_action_pairs()
    {
        var published = PublishedTable10.Rows.Keys.OrderBy(k => k.Resource).ThenBy(k => k.Action).ToArray();
        var modelled = ResourceActionModel.ModelledPairs.OrderBy(k => k.Resource).ThenBy(k => k.Action).ToArray();

        Assert.Equal(published, modelled);
    }

    /// <summary>
    /// Every cell, both directions. This is the test that makes the 21 denial assertions mean
    /// something: it is what fails when the transcription drifts from the publication.
    /// </summary>
    [Fact]
    public void Every_published_cell_matches_the_model()
    {
        var mismatches = new List<string>();

        foreach (var (pair, publishedRow) in PublishedTable10.Rows)
        {
            var modelled = ResourceActionModel.RowFor(pair.Resource, pair.Action);
            if (!modelled.TryGetValue(out var modelledRow, out var error))
            {
                mismatches.Add($"{PublishedTable10.Describe(pair, PrincipalTier.Anonymous)}: model has no row ({error!.Type})");
                continue;
            }

            foreach (var tier in Enum.GetValues<PrincipalTier>())
            {
                if (publishedRow[tier] != modelledRow[tier])
                    mismatches.Add(
                        $"{PublishedTable10.Describe(pair, tier)}: white paper says {publishedRow[tier]}, model says {modelledRow[tier]}");
            }

            if (publishedRow.Qualifier != modelledRow.Qualifier)
                mismatches.Add(
                    $"{PublishedTable10.Describe(pair, PrincipalTier.Anonymous)}: white paper qualifier " +
                    $"{publishedRow.Qualifier}, model {modelledRow.Qualifier}");
        }

        Assert.Empty(mismatches);
    }

    /// <summary>
    /// R7.12: "every 'denied' case in Table 10 SHALL have a test asserting the denial." The cases
    /// are enumerated from the published table, so this covers exactly the denials the white paper
    /// writes -- 21 of them today, and automatically any that a future edit adds.
    /// </summary>
    [Theory]
    [MemberData(nameof(PublishedDenials))]
    public void R7_12_EveryPublishedDenialIsDenied(ResourceKind resource, ActionKind action, PrincipalTier tier)
    {
        var decision = AccessPolicy.Decide(new AuthorizationRequest(
            tier, Curia.Domain.Credentials.CredentialState.Active, resource, action));

        Assert.True(decision.TryGetValue(out var value, out _));
        Assert.Equal(DecisionEffect.Deny, value!.Effect);
    }

    /// <summary>
    /// The count is asserted separately from the cases. A parser bug that silently produced zero
    /// denials would make <see cref="R7_12_EveryPublishedDenialIsDenied"/> pass vacuously by never
    /// running -- the exact failure mode this whole arrangement exists to prevent, one level up.
    /// </summary>
    [Fact]
    public void The_published_table_still_contains_21_denials()
    {
        Assert.Equal(21, PublishedTable10.DeniedCells().Count());
    }

    public static IEnumerable<object[]> PublishedDenials() => PublishedTable10.DeniedCells();
}
