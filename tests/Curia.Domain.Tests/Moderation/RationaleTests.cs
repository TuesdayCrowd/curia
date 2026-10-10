using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Xunit;

namespace Curia.Domain.Tests.Moderation;

/// <summary>R11.34 (errata G18): a rationale is a boundary type over R10.68's cap.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RationaleTests
{
    private static string? TypeOf(Result<Rationale> result) => result.TryGetValue(out _, out var error) ? null : error!.Type;

    [Fact]
    public void R11_34_ARationaleTakesR10_68sCap()
    {
        Assert.Equal(RationaleLimit.MaxUtf8Bytes, Rationale.MaxUtf8Bytes);
        Assert.Null(TypeOf(Rationale.Parse(new string('a', 4_096))));
        Assert.Equal(StorableText.TooLongType, TypeOf(Rationale.Parse(new string('a', 4_097))));

        var threeByte = ((char)0x4E2D).ToString();
        Assert.Null(TypeOf(Rationale.Parse(string.Concat(Enumerable.Repeat(threeByte, 1_365)) + "a")));
        Assert.Equal(StorableText.TooLongType, TypeOf(Rationale.Parse(string.Concat(Enumerable.Repeat(threeByte, 1_365)) + "ab")));
    }

    [Fact]
    public void R11_34_ARationaleNamesSoABlankOneIsRefused()
    {
        Assert.Equal(StorableText.BlankType, TypeOf(Rationale.Parse("   ")));
        Assert.Equal(StorableText.AbsentType, TypeOf(Rationale.Parse(null)));
        Assert.Equal(StorableText.NulType, TypeOf(Rationale.Parse("a\0")));
    }

    [Fact]
    public void R11_34_ARationaleRefusalNamesItsField()
    {
        Assert.True(Rationale.Parse(null).TryGetValue(out _, out var error) is false);
        Assert.Equal("field=rationale", error!.Detail);
        Assert.Equal("reviewed", Rationale.Parse("reviewed").Match(v => v.Value, e => e.Type));
    }
}
