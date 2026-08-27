using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Xunit;

namespace Curia.Canon.Tests.Vectors;

/// <summary>
/// R6.44 / errata G2 — the accepting side of R6.39's four boundaries. R6.39's second
/// sentence ("Published vectors SHALL exercise both sides of each of the four boundaries —
/// the value at the limit (accepted) and one past it (rejected)") was unsatisfiable from the
/// day it was written: the corpus could say "reject with this slug" and "canonicalize to
/// these bytes", and had no way at all to say "admit this". Zero of the four boundaries had
/// an accepting-side vector.
/// </summary>
/// <remarks>
/// <para>Each vector asserts three things, in R6.44's order: the published bytes go to the
/// real ADMIT entry point <b>unmodified</b> (R6.11, errata E6); a rejection fails the vector
/// <b>naming the slug ADMIT produced</b>, since a cap set one byte too tight is otherwise
/// indistinguishable in a log from a broken harness; and the same bytes canonicalize to
/// exactly <c>expected.canonical</c>, whose SHA-256 is <c>expected.digest</c>.</para>
/// <para>The third assertion is what stops the profile being vacuous. Acceptance alone
/// asserts almost nothing — an ADMIT phase that admits everything passes a bare accept
/// vector, and so does one that admits the document and then canonicalizes it wrongly.</para>
/// <para>Canonicalization here is handed the value ADMIT itself returned, rather than a
/// second parse of the same bytes. G2 records this seam explicitly: <c>curia-testis</c>'s
/// canonicalizer takes bytes and re-parses through R6.41's ADMIT-free path, while this one
/// takes a parsed tree, so the corpus asks only for the weaker "these bytes admit, and these
/// bytes canonicalize to X". Feeding ADMIT's own output is the stronger form of that claim
/// and constructs no different document, which is the property E6 exists to protect.</para>
/// </remarks>
public sealed class AdmitAcceptVectorTests
{
    private const string Family = "admit-accept";

    [Theory]
    [MemberData(nameof(AcceptVectors))]
    public void ConformanceAcceptVectorsAreAdmittedAndCanonicalizeToTheDeclaredForm(
        string name,
        byte[] input,
        byte[] expectedCanonical,
        string expectedDigestHex)
    {
        var admitted = JsonReader.Parse(input, AdmitLimits.Default)
            .Match(v => v, e => throw new Xunit.Sdk.XunitException(
                $"{Family}/{name}: ADMIT rejected a vector that must be admitted, with slug \"{e.Type}\""));

        var canonical = CanonicalJson.CanonicalizeWithNfc(admitted)
            .Match(b => b, e => throw new Xunit.Sdk.XunitException(
                $"{Family}/{name}: canonicalization failed with slug \"{e.Type}\""));

        Assert.Equal(expectedCanonical, canonical.ToArray());
        Assert.Equal(expectedDigestHex, Digests.Sha256(canonical).ToHex());
    }

    public static TheoryData<string, byte[], byte[], string> AcceptVectors()
    {
        var data = new TheoryData<string, byte[], byte[], string>();
        foreach (var v in VectorLoader.Load(Family))
        {
            // R6.44 routes by profile, not by directory: a vector filed here that declares
            // some other profile is a corpus defect, not something to run under this one.
            if (v.Profile is not VectorProfile.AdmitAccept)
                throw new InvalidOperationException(
                    $"{Family}/{v.Name} declares profile \"{VectorLoader.ProfileName(v.Profile)}\", not \"admit-accept\"");

            // The profile, never the absence of a file, declares acceptance: a vector whose
            // expectation failed to be committed must fail loudly rather than degrade into a
            // bare "it was admitted" that nothing can falsify.
            data.Add(
                v.Name,
                v.Input,
                v.ExpectedCanonical ?? throw new InvalidOperationException($"{Family}/{v.Name} has no expected.canonical"),
                v.ExpectedDigestHex ?? throw new InvalidOperationException($"{Family}/{v.Name} has no expected.digest"));
        }
        return data;
    }

    /// <summary>
    /// R6.44 (addendum). An accepting-side vector cannot detect a cap that is too generous
    /// and a rejecting-side vector cannot detect one that is too strict; only the pair locates
    /// a boundary. Without the link, a corpus that shipped only the easy half reports exactly
    /// what a complete one reports.
    /// </summary>
    [Fact]
    public void EveryAcceptVectorNamesARejectingTwinThatIsInTheCorpus()
    {
        var vectors = VectorLoader.Load(Family);
        Assert.NotEmpty(vectors);
        Assert.All(vectors, v =>
        {
            Assert.False(
                string.IsNullOrWhiteSpace(v.PairsWith),
                $"{Family}/{v.Name} names no rejecting-side twin -- R6.44 (addendum) requires \"pairs-with\": \"<family>/<case>\"");

            var twin = Path.Combine(VectorLoader.ConformanceRoot, v.PairsWith!.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(
                File.Exists(Path.Combine(twin, "meta.json")),
                $"{Family}/{v.Name} pairs with \"{v.PairsWith}\", which is not a vector in this corpus");
            Assert.True(
                File.Exists(Path.Combine(twin, "expect-reject")),
                $"{Family}/{v.Name} pairs with \"{v.PairsWith}\", which declares no expect-reject and so is not the rejecting side of the boundary");
        });
    }
}
