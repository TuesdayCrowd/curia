using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Content;
using Curia.Domain.Retrieval;
using Curia.Domain.Verification;
using Xunit;

namespace Curia.Domain.Tests.Retrieval;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RetrievalFloorTests
{
    /// <summary>
    /// Entry G12 reverses R10.2's V1 for the MCP tool: the published default is V0 on every modelled
    /// surface, and the floor is a criterion of the search request rather than a level the
    /// specification supplies. Asserted over every surface the enum models rather than the two named
    /// here, so a third surface added later cannot arrive with a default nobody chose.
    /// </summary>
    [Fact]
    public void R10_2_ThePublishedDefaultIsV0OnEveryModelledSurface()
    {
        foreach (var surface in Enum.GetValues<RetrievalSurface>())
        {
            Assert.Equal(VerificationLevel.V0, RetrievalFloorPolicy.PublishedFloor(surface));
        }
    }

    [Fact]
    public void R10_45_TheFloorAppliesOnlyToKindsTable13CanGrade()
    {
        Assert.Equal([PostKind.Answer, PostKind.Finding], RetrievalFloorPolicy.GradableKinds);
        Assert.Equal([PostKind.Question, PostKind.Comment, PostKind.Revision], RetrievalFloorPolicy.UngradableKinds);

        // A question is V0 forever (R8.58); a V2 floor never hides it.
        Assert.True(RetrievalFloorPolicy.Serves(VerificationLevel.V2, PostKind.Question, VerificationLevel.V0));
        Assert.False(RetrievalFloorPolicy.Serves(VerificationLevel.V2, PostKind.Answer, VerificationLevel.V0));
        Assert.True(RetrievalFloorPolicy.Serves(VerificationLevel.V2, PostKind.Finding, VerificationLevel.V2));
    }

    [Theory]
    [InlineData(VerificationLevel.V0, VerificationLevel.V0, true)]
    [InlineData(VerificationLevel.V0, VerificationLevel.Contradicted, true)]
    [InlineData(VerificationLevel.V1, VerificationLevel.V0, false)]
    [InlineData(VerificationLevel.V1, VerificationLevel.V1, true)]
    [InlineData(VerificationLevel.V1, VerificationLevel.V2, true)]
    [InlineData(VerificationLevel.V1, VerificationLevel.Contradicted, false)]
    [InlineData(VerificationLevel.V2, VerificationLevel.V1, false)]
    [InlineData(VerificationLevel.V2, VerificationLevel.V2, true)]
    public void R10_2_V0IsNoFloorAndHigherFloorsAdmitTheirLevelAndAbove(VerificationLevel floor, VerificationLevel level, bool admitted) =>
        Assert.Equal(admitted, RetrievalFloorPolicy.Admits(floor, level));

    [Fact]
    public void Table13_WeightsAreTheWhitePapersAndContradictedIsDemotedNotHidden()
    {
        Assert.Equal(1.0, RetrievalFloorPolicy.Weight(VerificationLevel.V0));
        Assert.Equal(1.2, RetrievalFloorPolicy.Weight(VerificationLevel.V1));
        Assert.Equal(2.0, RetrievalFloorPolicy.Weight(VerificationLevel.V2));
        Assert.Equal(0.3, RetrievalFloorPolicy.Weight(VerificationLevel.Contradicted));
    }

    [Fact]
    public void R9_6_AFloorOnTheWireIsV0V1OrV2AndNothingElse()
    {
        Assert.Equal(VerificationLevel.V2, RetrievalFloorPolicy.ParseFloor("V2").Match(v => v, _ => throw new InvalidOperationException()));
        Assert.False(RetrievalFloorPolicy.ParseFloor("V-").TryGetValue(out _, out var contradicted));
        Assert.Equal("curia/search/not-a-floor", contradicted!.Type);
        Assert.False(RetrievalFloorPolicy.ParseFloor("v1").TryGetValue(out _, out _));
        Assert.False(RetrievalFloorPolicy.IsFloor(VerificationLevel.Contradicted));
        Assert.Throws<ArgumentOutOfRangeException>(() => RetrievalFloorPolicy.Admits(VerificationLevel.Contradicted, VerificationLevel.V0));
    }

    [Fact]
    public void R10_46_AnUnmodelledSurfaceIsAFailureNotADefault()
    {
        Assert.Equal(RetrievalSurface.RestSearch, RetrievalSurfaces.Parse("rest-search").Match(v => v, _ => throw new InvalidOperationException()));
        Assert.False(RetrievalSurfaces.Parse("graphql").TryGetValue(out _, out var error));
        Assert.Equal("curia/retrieval/unknown-surface", error!.Type);
        Assert.Equal("mcp-search", RetrievalSurfaces.Wire(RetrievalSurface.McpSearch));
    }
}
