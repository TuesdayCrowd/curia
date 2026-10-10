using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Domain.Moderation;
using Xunit;

namespace Curia.Application.Tests.Moderation;

/// <summary>R10.68 (errata G18): a rationale's cap, counted in UTF-8 bytes.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RationaleLimitTests
{
    private static readonly string Wide = new((char)0x4E2D, 1);

    [Fact]
    public void R10_68_TheCapIs4096Utf8Bytes() =>
        Assert.Equal(4_096, RationaleLimit.MaxUtf8Bytes);

    [Fact]
    public void R10_68_AnAsciiRationaleAtTheCapIsNotOver() =>
        Assert.Null(RationaleLimit.Over(new string('a', 4_096)));

    [Fact]
    public void R10_68_AnAsciiRationaleOneByteOverReportsItsLength() =>
        Assert.Equal(4_097, RationaleLimit.Over(new string('a', 4_097)));

    [Fact]
    public void R10_68_ThreeByteCharactersAtTheCapAreNotOver()
    {
        var value = string.Concat(Enumerable.Repeat(Wide, 1_365)) + "a";
        Assert.Equal(4_096, Encoding.UTF8.GetByteCount(value));
        Assert.Null(RationaleLimit.Over(value));
    }

    [Fact]
    public void R10_68_ThreeByteCharactersOneByteOverReportTheirUtf8Length()
    {
        var value = string.Concat(Enumerable.Repeat(Wide, 1_365)) + "ab";
        Assert.Equal(1_367, value.Length);
        Assert.Equal(4_097, RationaleLimit.Over(value));
    }
}
