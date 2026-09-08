using System.Diagnostics.CodeAnalysis;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Client;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R6.46 and R15.1: <see cref="ActaCheck.RecomputeLeaf"/> held to <c>conformance/acta/</c>.
///
/// <para><b>Why here as well as in <c>Curia.Canon.Tests</c>.</b> That suite pins
/// <c>MerkleTree.LeafHash(CanonicalJson.Canonicalize(…))</c> — the computation. This one pins the
/// client's own function, which is a <i>second</i> place the same encoding is spelled out, and a
/// second spelling of a frozen encoding is exactly the class of defect §6 exists to prevent: it
/// agrees with the first on every document anybody tests and disagrees on the one an attacker
/// constructs.</para>
///
/// <para><b>The vector that carries the information is <c>nfd-payload-stays-nfd</c>.</b> A leaf is
/// canonicalized under <b>pure</b> RFC 8785 while a post envelope is canonicalized under the NFC
/// profile, and every other vector in the family is ASCII, so swapping the two functions is
/// invisible without it. A falsification run established that: with the NFD vector absent from this
/// path, replacing <c>Canonicalize</c> with <c>CanonicalizeWithNfc</c> left the whole end-to-end
/// suite green.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ActaLeafRecomputationTests
{
    private const string Family = "acta";

    /// <summary>
    /// The conformance root, found by walking up from the test binary. Not a configured path: a
    /// setting that could point at nothing would let this suite pass over an empty directory.
    /// </summary>
    private static string ConformanceRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                var candidate = Path.Combine(directory.FullName, "conformance", Family);
                if (Directory.Exists(candidate)) return candidate;
                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                $"conformance/{Family}/ is not above {AppContext.BaseDirectory}. A vector family no "
                + "runner can find is indistinguishable from a lost one (trap 5).");
        }
    }

    public static TheoryData<string> Vectors()
    {
        var data = new TheoryData<string>();
        foreach (var dir in Directory.EnumerateDirectories(ConformanceRoot).OrderBy(d => d, StringComparer.Ordinal))
            data.Add(Path.GetFileName(dir));
        return data;
    }

    /// <summary>
    /// Every vector's leaf, recomputed by the function <c>curia_verify</c> actually calls.
    /// </summary>
    [Theory]
    [MemberData(nameof(Vectors))]
    public void R6_46_TheClientRecomputesEveryPublishedLeaf(string name)
    {
        var directory = Path.Combine(ConformanceRoot, name);
        var entry = Parse(File.ReadAllBytes(Path.Combine(directory, "input.json")));
        var expected = File.ReadAllText(Path.Combine(directory, "expected.leaf")).Trim();

        Assert.True(expected.Length == 64, $"{Family}/{name} has no expected.leaf to check against");

        Assert.True(ActaCheck.RecomputeLeaf(entry).TryGetValue(out var leaf, out var error), error?.Type);
        Assert.Equal(expected, Convert.ToHexStringLower([.. leaf]));
    }

    /// <summary>
    /// The family is not vacuous with respect to the one distinction this path can get wrong. If
    /// every vector were ASCII, the theory above would pass for a client that canonicalized under
    /// the NFC profile, and the frozen leaf encoding would be unguarded here.
    /// </summary>
    [Fact]
    public void R6_46_TheFamilyContainsALeafTheNfcProfileWouldGetWrong()
    {
        var caught = Directory.EnumerateDirectories(ConformanceRoot).Where(directory =>
        {
            var entry = Parse(File.ReadAllBytes(Path.Combine(directory, "input.json")));
            var pure = CanonicalJson.Canonicalize(entry).Match(c => c.ToArray(), _ => []);
            var nfc = CanonicalJson.CanonicalizeWithNfc(entry).Match(c => c.ToArray(), _ => []);
            return !pure.AsSpan().SequenceEqual(nfc);
        }).Select(Path.GetFileName).ToArray();

        Assert.Contains("nfd-payload-stays-nfd", caught);
    }

    /// <summary>
    /// Unrestricted, not ADMIT-gated. An entry wraps a whole canonical envelope, so a real one can
    /// exceed the 1 MiB cap a <i>submission</i> is held to; applying the submission caps here would
    /// reject legitimate log entries and look like a Forum defect.
    /// </summary>
    private static JsonValue.Object Parse(byte[] bytes) =>
        JsonReader.ParseUnrestricted(bytes).TryGetValue(out var value, out var error) && value is JsonValue.Object o
            ? o
            : throw new InvalidOperationException($"the vector does not parse: {error?.Type}");
}
