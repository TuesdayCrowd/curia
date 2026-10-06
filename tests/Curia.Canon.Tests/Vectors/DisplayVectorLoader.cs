using System.Text;
using System.Text.Json;

namespace Curia.Canon.Tests.Vectors;

/// <summary>
/// A <c>conformance/display/</c> vector -- conformance/README.md, "The <c>display/</c> family": a
/// string, spelled as code points, and the exact literal a reader prints for it.
/// </summary>
internal sealed record DisplayVector(string Name, string Input, string Expected, string Requirement, string Note);

/// <summary>
/// Loads the <c>display/</c> family. Its shape is its own (<c>shape: "display"</c> in
/// <c>index.json</c>): <c>input.json</c> is a list of scalar values rather than a document to
/// canonicalize, and <c>expected.display</c> is text to compare, so
/// <see cref="VectorLoader.Load"/>'s canonical-or-reject expectation does not apply.
/// </summary>
internal static class DisplayVectorLoader
{
    public const string Family = "display";

    public static IReadOnlyList<DisplayVector> Load()
    {
        var root = Path.Combine(VectorLoader.ConformanceRoot, Family);
        var vectors = new List<DisplayVector>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var metaPath = Path.Combine(dir, "meta.json");
            using var meta = JsonDocument.Parse(File.ReadAllBytes(metaPath));
            var profile = VectorLoader.ReadProfile(metaPath);
            if (profile != VectorProfile.DisplayLiteral)
                throw new InvalidOperationException(
                    $"{Family}/{Path.GetFileName(dir)} declares profile \"{VectorLoader.ProfileName(profile)}\"; this loader handles only display-literal (R6.44)");

            using var input = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "input.json")));
            var text = new StringBuilder();
            foreach (var point in input.RootElement.GetProperty("code_points").EnumerateArray())
                text.Append(char.ConvertFromUtf32(point.GetInt32()));

            vectors.Add(new DisplayVector(
                Name: Path.GetFileName(dir),
                Input: text.ToString(),
                Expected: File.ReadAllText(Path.Combine(dir, "expected.display"), Encoding.ASCII),
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
}
