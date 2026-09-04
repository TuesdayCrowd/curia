using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Credentials;
using Xunit;

namespace Curia.Domain.Tests.Credentials;

/// <summary>R4.24's proof vocabulary and R4.1's owner identifier, as the log spells them (errata G5).</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OwnerVerificationTests
{
    /// <summary>Every member has a spelling, and every spelling parses back to its member.</summary>
    [Fact]
    public void R4_24_EveryProofRoundTripsThroughItsSpelling()
    {
        foreach (var method in Enum.GetValues<OwnerVerificationMethod>())
        {
            var wire = OwnerVerificationMethods.Wire(method);
            Assert.True(OwnerVerificationMethods.Parse(wire).TryGetValue(out var parsed, out var error), error?.Type);
            Assert.Equal(method, parsed);
        }
    }

    /// <summary>
    /// Four spellings, one per R4.24 arm. Appendix F.1's <c>org</c> is the spelling errata G5
    /// declined to adopt, because it collapses two proofs of different strength under one name --
    /// so it is refused rather than mapped to either.
    /// </summary>
    [Theory]
    [InlineData("org")]
    [InlineData("MANUAL")]
    [InlineData("")]
    [InlineData("dns")]
    public void G5_ASpellingOutsideTheFourProofsIsRefused(string wire)
    {
        Assert.False(OwnerVerificationMethods.Parse(wire).TryGetValue(out _, out var error));
        Assert.Equal("curia/attest/unknown-method", error!.Type);
    }

    [Fact]
    public void G5_TheVocabularyIsExactlyTheFourR4_24Arms()
    {
        var spellings = Enum.GetValues<OwnerVerificationMethod>().Select(OwnerVerificationMethods.Wire).Order(StringComparer.Ordinal);
        Assert.Equal(["attestation", "domain", "email", "manual"], spellings);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void R4_1_AnOwnerMustBeNamed(string value)
    {
        Assert.False(OwnerId.Create(value).TryGetValue(out _, out var error));
        Assert.Equal("curia/domain/empty-identifier", error!.Type);
    }

    [Fact]
    public void R4_1_AnOwnerIdentifierIsOpaque()
    {
        Assert.True(OwnerId.Create("owner:tuesdaycrowd").TryGetValue(out var owner, out _));
        Assert.Equal("owner:tuesdaycrowd", owner.Value);
    }
}
