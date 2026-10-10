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

    /// <summary>
    /// R10.68's order, which the rationale keeps on both paths: absent and blank, then the cap, then
    /// what no store holds. A rationale over the cap is too long whatever else it holds, as it was when
    /// A4 checked the cap first; the rule's own order (B1's <c>R11_34_TheRuleRefusesInItsOrder</c>)
    /// is unchanged. A lone surrogate is counted as U+FFFD, three bytes. The theory takes an index
    /// because a lone surrogate does not survive theory serialization.
    /// </summary>
    private static readonly (string Value, string Type, string Detail)[] OrderCases =
    [
        (new string('a', 4_096) + (char)0, StorableText.TooLongType, "field=rationale bytes=4097: at most 4096 UTF-8 bytes"),
        (new string('a', 4_096) + (char)0xD800, StorableText.TooLongType, "field=rationale bytes=4099: at most 4096 UTF-8 bytes"),
        (new string(' ', 4_097), StorableText.BlankType, "field=rationale"),
        ("a" + (char)0, StorableText.NulType, "field=rationale"),
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void R11_34_ARationaleOverTheCapIsTooLongBeforeWhatNoStoreHoldsIsNamed(int index)
    {
        var (value, type, detail) = OrderCases[index];

        Assert.False(Rationale.Parse(value).TryGetValue(out _, out var error));
        Assert.Equal((type, detail), (error!.Type, error.Detail));
    }

    [Fact]
    public void R11_34_ARationaleRefusalNamesItsField()
    {
        Assert.True(Rationale.Parse(null).TryGetValue(out _, out var error) is false);
        Assert.Equal("field=rationale", error!.Detail);
        Assert.Equal("reviewed", Rationale.Parse("reviewed").Match(v => v.Value, e => e.Type));
    }
}
