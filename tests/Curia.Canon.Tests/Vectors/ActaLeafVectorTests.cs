using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Acta;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Xunit;

namespace Curia.Canon.Tests.Vectors;

/// <summary>
/// The <c>acta/</c> family: R6.46's leaf input, the one computation R15.1 froze. Conformance
/// README, "The <c>acta/</c> family".
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ActaLeafVectorTests
{
    private const string Family = "acta";

    [Fact]
    public void R6_46_EveryVectorCanonicalizesUnderThePureProfileAndHashesToItsLeaf()
    {
        var vectors = VectorLoader.Load(Family);
        Assert.NotEmpty(vectors);

        foreach (var v in vectors)
        {
            Assert.True(v.Profile == VectorProfile.ActaLeaf, $"{Family}/{v.Name} declares {VectorLoader.ProfileName(v.Profile)}");
            Assert.NotNull(v.ExpectedCanonical);
            Assert.NotNull(v.ExpectedDigestHex);
            Assert.True(v.ExpectedLeafHex is { Length: 64 }, $"{Family}/{v.Name} has no expected.leaf");

            // Unrestricted, not ADMIT-gated: an entry wraps a whole canonical envelope, so a real
            // one can be larger than a submission is allowed to be.
            var parsed = JsonReader.ParseUnrestricted(v.Input)
                .Match(x => x, e => throw new Xunit.Sdk.XunitException($"{Family}/{v.Name}: parse: {e.Type}"));

            // The pure profile. The NFD vector is the one that catches CanonicalizeWithNfc here.
            var canonical = CanonicalJson.Canonicalize(parsed)
                .Match(c => c.ToArray(), e => throw new Xunit.Sdk.XunitException($"{Family}/{v.Name}: canonicalize: {e.Type}"));

            Assert.True(canonical.AsSpan().SequenceEqual(v.ExpectedCanonical), $"{Family}/{v.Name}: canonical bytes differ");
            Assert.Equal(v.ExpectedDigestHex, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(canonical)));
            Assert.Equal(v.ExpectedLeafHex, Convert.ToHexStringLower([.. MerkleTree.LeafHash(canonical)]));
        }
    }

    /// <summary>
    /// The family is not vacuous with respect to the pure/NFC split: at least one vector's
    /// canonical form is not NFC, so the wrong function produces a different leaf.
    /// </summary>
    [Fact]
    public void R6_46_TheFamilyContainsALeafTheNfcProfileWouldGetWrong()
    {
        var vectors = VectorLoader.Load(Family);
        var caught = vectors.Where(v =>
        {
            var parsed = JsonReader.ParseUnrestricted(v.Input).Match(x => x, _ => throw new InvalidOperationException());
            var nfc = CanonicalJson.CanonicalizeWithNfc(parsed).Match(c => c.ToArray(), _ => throw new InvalidOperationException());
            return !nfc.AsSpan().SequenceEqual(v.ExpectedCanonical);
        }).ToList();

        Assert.Contains(caught, v => v.Name == "nfd-payload-stays-nfd");
    }
}
