using Xunit;

namespace Curia.Canon.Tests.Vectors;

public sealed class VectorLoaderTests
{
    [Theory]
    [InlineData("c4")]
    [InlineData("ordering")]
    [InlineData("unicode")]
    [InlineData("numbers")]
    [InlineData("admit-reject")]
    public void EveryFamilyLoadsAndEveryVectorCitesARequirement(string family)
    {
        var vectors = VectorLoader.Load(family);
        Assert.NotEmpty(vectors);
        Assert.All(vectors, v => Assert.False(string.IsNullOrWhiteSpace(v.Requirement)));
        Assert.All(vectors, v =>
            Assert.True(v.ExpectedCanonical is not null || v.ExpectRejectSlug is not null,
                $"{family}/{v.Name} declares neither an expected canonical form nor a rejection"));
    }

    [Fact]
    public void C4VectorNineIsTheEscapeSequenceNotARawNulByte()
    {
        var v = VectorLoader.Load("c4").Single(x => x.Name == "vector-09");
        Assert.DoesNotContain((byte)0, v.Input);
        Assert.Equal(14, v.Input.Length);          // {"a":"\u0000"}
        Assert.Equal(v.Input, v.ExpectedCanonical); // preserved unchanged
    }

    [Fact]
    public void AdmitRejectRawNulVectorDoesContainARawNulByte()
    {
        var v = VectorLoader.Load("admit-reject").Single(x => x.Name == "raw-nul-byte");
        Assert.Contains((byte)0, v.Input);
    }
}
