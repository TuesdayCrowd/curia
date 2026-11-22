using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Application.Tests.Retrieval;

/// <summary>
/// D10b's table of numbers keyed by model id (plan ruling 1: an id with no row refuses and removes nothing). The seams do not exist on the tree, so each probe reaches them by reflection and fails on its own
/// assertion, naming what is missing; the tail of NDT8 (the check under such a model, the floor, the search floor block) is written when the seam's shape exists.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NearDuplicateModelRowTests
{
    /// <summary>
    /// NDT8. <c>DuplicateThresholds.For(modelId)</c> for an id with no row answers "none" (read here as null). Predicted red:
    /// <c>Expected: DuplicateThresholds.For(string) exists  Actual: no such method</c>.
    /// </summary>
    [Fact]
    public void R11_102_NDT8_AModelWithNoRowGetsNoThresholds()
    {
        var method = typeof(DuplicateThresholds).GetMethod("For", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        Assert.True(method is not null, "Expected: DuplicateThresholds.For(string) exists  Actual: no such method (D10b's table does not exist yet)");

        var none = method!.Invoke(null, ["no-such-model@1"]);
        Assert.True(none is null, "Expected: DuplicateThresholds.For of an id with no row answers none  Actual: a row");
    }

    /// <summary>NDT8b, the control. The hashed-era numbers, exactly: 0.94 refusing, 0.5 overlap, 0.85 annotating, and the floor 0.2. Green on the tree.</summary>
    [Fact]
    public void R8_18_NDT8b_TheHashedRowIsTheHashedEraNumbersExactly()
    {
        Assert.Equal(0.94, DuplicateThresholds.Published.RefuseCosine);
        Assert.Equal(0.5, DuplicateThresholds.Published.RefuseOverlap);
        Assert.Equal(0.85, DuplicateThresholds.Published.AnnotateCosine);
        Assert.Equal(0.2, HybridRanking.MinimumCosine);
    }
}
