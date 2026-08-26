using Curia.Canon.Json;
using Xunit;

namespace Curia.Canon.Tests.Json;

/// <summary>
/// R6.39's four magnitudes, compared against <see cref="AdmitLimits.Default"/> cell for cell.
///
/// <para>This is the only test in the assembly that knows what the caps <i>are</i>. Every other
/// boundary test knows only what they are called, and that is deliberate -- see
/// <see cref="PublishedAdmitLimits"/> for why deriving each boundary test's input from the
/// constant it checks makes the whole family blind to the constant's value.</para>
/// </summary>
public sealed class PublishedAdmitLimitsTests
{
    /// <summary>
    /// The vacuity guard, and it comes first for the reason Stage 1 learned on Table 10: a
    /// parser that silently returns nothing makes every comparison below pass by having nothing
    /// to compare. R6.39 publishes the count itself -- "ADMIT's <b>four</b> size-shaped limits"
    /// -- so the count is checked against the sentence rather than against this test's own idea
    /// of how many there should be.
    /// </summary>
    [Fact]
    public void PublishedSentenceEnumeratesExactlyTheFourLimitsItClaimsTo()
    {
        Assert.Contains("four size-shaped limits", PublishedAdmitLimits.Paragraph, StringComparison.Ordinal);
        Assert.Equal(4, PublishedAdmitLimits.Limits.Count);
        Assert.Equal(
            ["nesting depth", "per object", "string length", "submission size"],
            PublishedAdmitLimits.Limits.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void DepthCapMatchesThePublishedNumber()
    {
        var published = PublishedAdmitLimits.Depth;
        Assert.Equal(AdmitLimits.Default.MaxDepth, published.Magnitude);
        Assert.Equal("containers", published.Unit);
    }

    [Fact]
    public void MemberCapMatchesThePublishedNumber()
    {
        var published = PublishedAdmitLimits.MembersPerObject;
        Assert.Equal(AdmitLimits.Default.MaxMembersPerObject, published.Magnitude);
        Assert.Equal("members", published.Unit);
    }

    [Fact]
    public void SubmissionSizeCapMatchesThePublishedNumber()
    {
        var published = PublishedAdmitLimits.SubmissionSize;
        Assert.Equal(AdmitLimits.Default.MaxBytes, published.Bytes);
    }

    [Fact]
    public void StringLengthCapMatchesThePublishedNumber()
    {
        var published = PublishedAdmitLimits.StringLength;
        Assert.Equal(AdmitLimits.Default.MaxStringBytes, published.Bytes);
    }

    /// <summary>
    /// Both byte-valued caps are published twice -- once as a binary prefix and once as an exact
    /// count -- and the two must agree. A cap written "1 MiB (1,000,000 bytes)" would be
    /// self-inconsistent by 4.9 %, and whichever half an implementer read would be defensible
    /// from the document; R14.6 calls a divergence between two implementations a release
    /// blocker, and a specification that supplies two answers is how one is manufactured.
    /// </summary>
    [Theory]
    [InlineData("submission size")]
    [InlineData("string length")]
    public void BinaryPrefixAndParentheticalByteCountAgree(string clause)
    {
        var published = PublishedAdmitLimits.Limits[clause];
        Assert.NotNull(published.ByteGloss);
        Assert.Equal(published.ByteGloss, published.Bytes);
    }

    /// <summary>
    /// The unit of measurement is as load-bearing as the magnitude: 256 KiB of UTF-8 bytes and
    /// 256 KiB of UTF-16 code units are different caps for every non-ASCII document, and the
    /// difference is invisible to a suite whose fixtures are all ASCII. Pinned as a phrase
    /// because that is how R6.39 states it.
    /// </summary>
    [Fact]
    public void PublishedCapsAreMeasuredInUtf8Bytes()
    {
        Assert.Contains("measured in UTF-8 bytes", PublishedAdmitLimits.Paragraph, StringComparison.Ordinal);
    }

    /// <summary>
    /// R15.1's freeze is what makes this whole file worth having: an unfrozen constant may
    /// legitimately drift and needs no conformance check, so if R6.39 ever stops claiming the
    /// values are frozen, that is a decision someone must make deliberately rather than a
    /// sentence that quietly disappears in an edit.
    /// </summary>
    [Fact]
    public void PublishedCapsAreStillClaimedFrozenUnderR151()
    {
        Assert.Contains("frozen values under R15.1", PublishedAdmitLimits.Paragraph, StringComparison.Ordinal);
    }
}
