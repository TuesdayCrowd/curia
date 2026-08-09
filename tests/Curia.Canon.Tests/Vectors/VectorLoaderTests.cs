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

    // Regression guard: a vector citing R6.9 (NFC normalization) whose note describes a
    // transformation must actually exercise one. A vector that claims "A -> B" but has
    // input.json byte-identical to expected.canonical is an identity mapping in disguise:
    // it passes every existing assertion (requirement present, digest correct, canonical
    // present) while proving nothing about normalization. This is precisely how
    // unicode/nfd-to-nfc-composed and unicode/singleton-ohm went undetected: their
    // literal-character input.json had already been NFC-normalized before it reached
    // disk, silently reducing both to identity vectors.
    private static readonly string[] AllFamilies = ["c4", "ordering", "unicode", "numbers", "admit-reject"];

    [Fact]
    public void EveryR6NineTransformationVectorActuallyTransforms()
    {
        var transformVectors = AllFamilies
            .SelectMany(family => VectorLoader.Load(family).Select(v => (family, v)))
            .Where(x => x.v.Requirement == "R6.9" && x.v.Note.Contains("->", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(transformVectors);
        Assert.All(transformVectors, x =>
        {
            Assert.NotNull(x.v.ExpectedCanonical);
            Assert.False(x.v.Input.SequenceEqual(x.v.ExpectedCanonical),
                $"{x.family}/{x.v.Name} cites R6.9 and claims a transformation (\"{x.v.Note}\") "
                    + "but input.json is byte-identical to expected.canonical -- the vector exercises no normalization at all");
        });
    }
}
