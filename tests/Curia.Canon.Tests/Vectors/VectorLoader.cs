using System.Text.Json;

namespace Curia.Canon.Tests.Vectors;

internal sealed record Vector(
    string Name,
    byte[] Input,
    byte[]? ExpectedCanonical,
    string? ExpectedDigestHex,
    string? ExpectRejectSlug,
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

    public static IReadOnlyList<Vector> Load(string family)
    {
        var root = Path.Combine(ConformanceRoot, family);
        var vectors = new List<Vector>();
        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
        {
            var meta = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(dir, "meta.json"))).RootElement;
            var canonical = Path.Combine(dir, "expected.canonical");
            var digest = Path.Combine(dir, "expected.digest");
            var reject = Path.Combine(dir, "expect-reject");
            vectors.Add(new Vector(
                Name: Path.GetFileName(dir),
                Input: File.ReadAllBytes(Path.Combine(dir, "input.json")),
                ExpectedCanonical: File.Exists(canonical) ? File.ReadAllBytes(canonical) : null,
                ExpectedDigestHex: File.Exists(digest) ? File.ReadAllText(digest).Trim() : null,
                ExpectRejectSlug: File.Exists(reject) ? File.ReadAllText(reject).Trim() : null,
                Requirement: meta.GetProperty("requirement").GetString()!,
                Note: meta.TryGetProperty("note", out var n) ? n.GetString() ?? "" : ""));
        }
        return vectors;
    }
}
