using System.Collections.Immutable;
using System.Text.Json;

namespace Curia.Canon.Tests.Vectors;

/// <summary>One audit path of a <c>merkle/</c> vector: the leaf it proves and the nodes, as hex.</summary>
internal sealed record MerkleInclusion(int Index, IReadOnlyList<string> Path);

/// <summary>One consistency proof of a <c>merkle/</c> vector: the earlier size and the nodes, as hex.</summary>
internal sealed record MerkleConsistency(int From, IReadOnlyList<string> Path);

/// <summary>
/// A <c>conformance/merkle/</c> vector -- conformance/README.md, "The <c>merkle/</c> family". The
/// leaf inputs are raw bytes; every expectation is lowercase hex.
/// </summary>
internal sealed record MerkleVector(
    string Name,
    ImmutableArray<ImmutableArray<byte>> Leaves,
    string Root,
    IReadOnlyList<string> LeafHashes,
    IReadOnlyList<MerkleInclusion> Inclusion,
    IReadOnlyList<MerkleConsistency> Consistency,
    string Requirement,
    string Note)
{
    /// <summary>The tree size, which the directory name also carries.</summary>
    public int Size => Leaves.Length;
}

/// <summary>
/// Loads the <c>merkle/</c> family. Its shape is its own (<c>shape: "merkle"</c> in
/// <c>index.json</c>): <c>input.json</c> is a list of hex leaf inputs rather than a document to
/// canonicalize, and <c>expected.json</c> carries a root and every path, so
/// <see cref="VectorLoader.Load"/>'s canonical-or-reject expectation does not apply.
/// </summary>
internal static class MerkleVectorLoader
{
    public const string Family = "merkle";

    public static IReadOnlyList<MerkleVector> Load()
    {
        var root = Path.Combine(VectorLoader.ConformanceRoot, Family);
        var vectors = new List<MerkleVector>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var metaPath = Path.Combine(dir, "meta.json");
            using var meta = JsonDocument.Parse(File.ReadAllBytes(metaPath));
            var profile = VectorLoader.ReadProfile(metaPath);
            if (profile != VectorProfile.MerkleTree)
                throw new InvalidOperationException(
                    $"{Family}/{Path.GetFileName(dir)} declares profile \"{VectorLoader.ProfileName(profile)}\"; this loader handles only merkle-tree (R6.44)");

            using var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "input.json")));
            using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "expected.json")));
            var e = expected.RootElement;

            vectors.Add(new MerkleVector(
                Name: Path.GetFileName(dir),
                Leaves: [.. input.RootElement.GetProperty("leaves").EnumerateArray()
                    .Select(l => ImmutableArray.Create(Convert.FromHexString(l.GetString()!)))],
                Root: e.GetProperty("root").GetString()!,
                LeafHashes: Strings(e.GetProperty("leaf_hashes")),
                Inclusion: [.. e.GetProperty("inclusion").EnumerateArray()
                    .Select(c => new MerkleInclusion(c.GetProperty("index").GetInt32(), Strings(c.GetProperty("path"))))],
                Consistency: [.. e.GetProperty("consistency").EnumerateArray()
                    .Select(c => new MerkleConsistency(c.GetProperty("from").GetInt32(), Strings(c.GetProperty("path"))))],
                Requirement: meta.RootElement.GetProperty("requirement").GetString()!,
                Note: meta.RootElement.TryGetProperty("note", out var n) ? n.GetString() ?? "" : ""));
        }

        return vectors;
    }

    /// <summary>The count <c>index.json</c> declares for the family, so a test can hold this loader to it.</summary>
    public static int DeclaredCount()
    {
        using var index = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(VectorLoader.ConformanceRoot, "index.json")));
        var entry = index.RootElement.GetProperty("directories").EnumerateArray()
            .Single(d => d.GetProperty("name").GetString() == Family);
        return entry.GetProperty("count").GetInt32();
    }

    private static string[] Strings(JsonElement e) => [.. e.EnumerateArray().Select(x => x.GetString()!)];
}
