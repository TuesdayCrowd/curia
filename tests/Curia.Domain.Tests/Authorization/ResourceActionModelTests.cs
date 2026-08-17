using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Authorization;
using Xunit;

namespace Curia.Domain.Tests.Authorization;

/// <summary>Properties of Table 10 as a whole, rather than of any one cell.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ResourceActionModelTests
{
    /// <summary>
    /// Table 10 is monotone in tier: no row grants a capability to a lower column and withholds it
    /// from a higher one.
    ///
    /// <para>Asserted rather than assumed, because <see cref="PrincipalTierExtensions.Rank"/>
    /// exists on the strength of it -- errata A19 has Cedar comparing <c>principal.tier_rank &gt;=
    /// 2</c>, which is only a faithful rendering of the table while this holds. A future row that
    /// broke it would make every rank comparison in policy silently wrong, and that is a property
    /// of the published table, so it is checked against the published table.</para>
    /// </summary>
    [Fact]
    public void Table_10_is_monotone_in_tier()
    {
        var ordered = Enum.GetValues<PrincipalTier>().OrderBy(t => t.Rank()).ToArray();

        foreach (var (pair, row) in PublishedTable10.Rows)
        {
            for (var i = 1; i < ordered.Length; i++)
            {
                var lower = Capability(row[ordered[i - 1]]);
                var higher = Capability(row[ordered[i]]);

                Assert.True(
                    higher >= lower,
                    $"{PublishedTable10.Describe(pair, ordered[i])} is less capable than {ordered[i - 1]}");
            }
        }

        // Denied < RateLimited < Allowed. OwnerAuthOnly is off this scale entirely -- it is not a
        // tier decision (see Table10Cell) -- and the `agent`/`enroll` row is uniform across all
        // five columns, so mapping it to a single value keeps the row trivially monotone without
        // pretending it sits somewhere on the capability order.
        static int Capability(Table10Cell cell) => cell switch
        {
            Table10Cell.Denied => 0,
            Table10Cell.RateLimited => 1,
            Table10Cell.Allowed => 2,
            Table10Cell.OwnerAuthOnly => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(cell), cell, null),
        };
    }

    /// <summary>
    /// The table models exactly the pairs it names -- nothing extra, and no pair reachable by two
    /// different spellings.
    /// </summary>
    [Fact]
    public void Every_modelled_pair_resolves_and_no_other_does()
    {
        foreach (var pair in ResourceActionModel.ModelledPairs)
            Assert.True(ResourceActionModel.RowFor(pair.Resource, pair.Action).TryGetValue(out _, out _));

        var modelled = ResourceActionModel.ModelledPairs.ToHashSet();
        var unmodelled = 0;

        foreach (var resource in Enum.GetValues<ResourceKind>())
        foreach (var action in Enum.GetValues<ActionKind>())
        {
            if (modelled.Contains((resource, action))) continue;

            unmodelled++;
            var result = ResourceActionModel.RowFor(resource, action);
            Assert.False(result.TryGetValue(out _, out var error));
            Assert.Equal("curia/authz/unmodelled-resource-action", error!.Type);
        }

        // 13 resources x 10 actions = 130 combinations; Table 10 names 16 of them.
        Assert.Equal(114, unmodelled);
        Assert.Equal(16, modelled.Count);
    }

    /// <summary>
    /// Every wire name round-trips, and no two members share one. The AuthZEN request body is built
    /// from these (R7.2), so a collision would make two distinct actions indistinguishable to the
    /// policy engine.
    /// </summary>
    [Fact]
    public void R7_2_WireNamesAreDistinct()
    {
        var resources = Enum.GetValues<ResourceKind>().Select(ResourceActionNames.Wire).ToArray();
        var actions = Enum.GetValues<ActionKind>().Select(ResourceActionNames.Wire).ToArray();

        Assert.Equal(resources.Length, resources.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(actions.Length, actions.Distinct(StringComparer.Ordinal).Count());
        // Table 10 writes every name in lower case; asserted as "contains no upper-case letter"
        // rather than by round-tripping through a case conversion, which CA1308 rightly flags as a
        // normalization that can change meaning under some cultures.
        Assert.All(resources, n => Assert.DoesNotContain(n, char.IsUpper));
        Assert.All(actions, n => Assert.DoesNotContain(n, char.IsUpper));
    }

    /// <summary>
    /// <see cref="PrincipalTier.Anonymous"/> ranks below every tier. Nothing in the white paper
    /// compares against it, but the ordering being total is what stops a rank comparison from
    /// admitting an anonymous principal to a tier-gated action.
    /// </summary>
    [Fact]
    public void Anonymous_ranks_below_every_tier()
    {
        foreach (var tier in Enum.GetValues<PrincipalTier>().Where(t => t is not PrincipalTier.Anonymous))
            Assert.True(PrincipalTier.Anonymous.Rank() < tier.Rank());

        // Errata A19: Cedar compares tier_rank as a Long, so T2's rank is the literal 2 the
        // published policy example writes.
        Assert.Equal(2, PrincipalTier.T2.Rank());
    }
}
