using System.Text.Json;

namespace Curia.Canon.Tests.Vectors;

/// <summary>
/// The function a vector constrains, from <c>meta.json</c>'s <c>profile</c> key —
/// conformance/README.md, "Which function a vector constrains". R6.44 makes the profile,
/// and not the directory a vector happens to sit in, what selects the runner: "a runner
/// encountering a <c>profile</c> value it does not recognize SHALL fail rather than skip,
/// and SHALL route every vector by its declared profile rather than by the directory the
/// vector occupies."
/// </summary>
internal enum VectorProfile
{
    /// <summary><c>rfc8785</c> — pure RFC 8785, no Unicode normalization.</summary>
    Rfc8785,

    /// <summary><c>canonicalize-with-nfc</c> — NFC every key and string value, then canonicalize.</summary>
    CanonicalizeWithNfc,

    /// <summary><c>admit</c> — the input must be REJECTED with the slug in <c>expect-reject</c>.</summary>
    Admit,

    /// <summary>
    /// <c>admit-accept</c> — the input must be ADMITTED, and the same bytes must then
    /// canonicalize to <c>expected.canonical</c> (R6.44).
    /// </summary>
    AdmitAccept,

    /// <summary><c>envelope</c> — canonicalize a full Table 9 envelope, digest it, verify its JWS.</summary>
    Envelope,

    /// <summary>
    /// <c>merkle-tree</c> — RFC 9162 §2.1 over given leaves: hash them, build the tree, reproduce
    /// and verify every audit path and consistency proof (R6.23).
    /// </summary>
    MerkleTree,

    /// <summary>
    /// <c>acta-leaf</c> — a log entry document canonicalized under <b>pure</b> RFC 8785 and
    /// hashed as <c>SHA-256(0x00 ‖ canonical)</c>: R6.46's leaf input, frozen by R15.1.
    /// </summary>
    ActaLeaf,
}

internal sealed record Vector(
    string Name,
    VectorProfile Profile,
    byte[] Input,
    byte[]? ExpectedCanonical,
    string? ExpectedDigestHex,
    string? ExpectRejectSlug,
    string? ExpectedLeafHex,
    string? PairsWith,
    string Requirement,
    string Note);

internal static class VectorLoader
{
    public static string ConformanceRoot { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance")))
            dir = dir.Parent;
        return dir is null
            ? throw new InvalidOperationException("conformance/ not found above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "conformance");
    }

    /// <summary>
    /// The corpus spelling of a profile, mapped to the enum. An unrecognized value throws:
    /// R6.44 requires a runner to fail on one rather than skip the vector, because "a skipped
    /// vector is indistinguishable in a passing log from a satisfied one".
    /// </summary>
    public static VectorProfile ParseProfile(string value) => value switch
    {
        "rfc8785" => VectorProfile.Rfc8785,
        "canonicalize-with-nfc" => VectorProfile.CanonicalizeWithNfc,
        "admit" => VectorProfile.Admit,
        "admit-accept" => VectorProfile.AdmitAccept,
        "envelope" => VectorProfile.Envelope,
        "merkle-tree" => VectorProfile.MerkleTree,
        "acta-leaf" => VectorProfile.ActaLeaf,
        _ => throw new InvalidOperationException(
            $"unrecognized conformance profile \"{value}\" -- R6.44 requires a runner to fail rather than skip"),
    };

    /// <summary>The inverse of <see cref="ParseProfile"/>, so failures name what is on disk.</summary>
    public static string ProfileName(VectorProfile profile) => profile switch
    {
        VectorProfile.Rfc8785 => "rfc8785",
        VectorProfile.CanonicalizeWithNfc => "canonicalize-with-nfc",
        VectorProfile.Admit => "admit",
        VectorProfile.AdmitAccept => "admit-accept",
        VectorProfile.Envelope => "envelope",
        VectorProfile.MerkleTree => "merkle-tree",
        VectorProfile.ActaLeaf => "acta-leaf",
        _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "not a conformance profile"),
    };

    /// <summary>Reads the <c>profile</c> a <c>meta.json</c> declares, failing when it declares none.</summary>
    public static VectorProfile ReadProfile(string metaPath) =>
        ProfileOf(JsonDocument.Parse(File.ReadAllBytes(metaPath)).RootElement, metaPath);

    private static VectorProfile ProfileOf(JsonElement meta, string metaPath) =>
        meta.TryGetProperty("profile", out var profile) && profile.GetString() is { } declared
            ? ParseProfile(declared)
            : throw new InvalidOperationException(
                $"{metaPath} declares no profile -- R6.44 makes the profile, not the directory, what routes a vector");

    public static IReadOnlyList<Vector> Load(string family)
    {
        var root = Path.Combine(ConformanceRoot, family);
        var vectors = new List<Vector>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var metaPath = Path.Combine(dir, "meta.json");
            var meta = JsonDocument.Parse(File.ReadAllBytes(metaPath)).RootElement;
            var canonical = Path.Combine(dir, "expected.canonical");
            var digest = Path.Combine(dir, "expected.digest");
            var reject = Path.Combine(dir, "expect-reject");
            var leaf = Path.Combine(dir, "expected.leaf");
            vectors.Add(new Vector(
                Name: Path.GetFileName(dir),
                Profile: ProfileOf(meta, metaPath),
                Input: File.ReadAllBytes(Path.Combine(dir, "input.json")),
                ExpectedCanonical: File.Exists(canonical) ? File.ReadAllBytes(canonical) : null,
                ExpectedDigestHex: File.Exists(digest) ? File.ReadAllText(digest).Trim() : null,
                ExpectRejectSlug: File.Exists(reject) ? File.ReadAllText(reject).Trim() : null,
                ExpectedLeafHex: File.Exists(leaf) ? File.ReadAllText(leaf).Trim() : null,
                PairsWith: meta.TryGetProperty("pairs-with", out var pairs) ? pairs.GetString() : null,
                Requirement: meta.GetProperty("requirement").GetString()!,
                Note: meta.TryGetProperty("note", out var n) ? n.GetString() ?? "" : ""));
        }
        return vectors;
    }
}
