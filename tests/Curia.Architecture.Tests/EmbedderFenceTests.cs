using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Errata G65 (D10a), SEM13, SEM14 and SEM17: three fences on the embedder, read from IL and from the lock files, in Debug and in Release (D16).
/// SEM15 (the startup check is called from <c>Program.Build</c> after the lifecycle gate) is <b>not built</b>: <see cref="IlCallSites"/> returns the set of calls in a method body and
/// not their order, so the order cannot be read from IL here, and SEM20 and SEM21 (which boot the host) carry the property; recorded in the report as the plan's own rule allows.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class EmbedderFenceTests
{
    private static readonly string[] MayName = ["Curia.Application", "Curia.Domain"];

    /// <summary>The type names (namespace-qualified) a scanned assembly references, with the name of the assembly each is scoped to.</summary>
    private static List<(string Assembly, string Type)> TypeReferences(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var found = new List<(string, string)>();
        foreach (var handle in md.TypeReferences)
        {
            var reference = md.GetTypeReference(handle);
            if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
            var assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
            found.Add((assembly, md.GetString(reference.Namespace) + "." + md.GetString(reference.Name)));
        }

        return found;
    }

    private static string AssemblyNameOf(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>
    /// SEM13. No assembly built from <c>src/</c> other than <c>Curia.Application</c> and <c>Curia.Domain</c> references <c>HashedNGramEmbedder</c> or <c>HashedNGramEmbedding</c> (R11.91: production has no
    /// other embedder). The scanned assemblies are asserted by name, and a control proves the scan resolves a type that is certainly referenced, so a fence that found nothing cannot pass. Predicted red on
    /// the seam: <c>Curia.Api references Curia.Application.Retrieval.HashedNGramEmbedder</c> (the composition root still registers it).
    /// </summary>
    [Fact]
    public void R11_91_SEM13_NoProductionAssemblyBeyondTheTwoThatDefineItNamesTheHashedEmbedder()
    {
        var scanned = Shipped.ShippedAssemblies().Where(p => !MayName.Contains(AssemblyNameOf(p), StringComparer.Ordinal)).ToList();
        var names = scanned.Select(AssemblyNameOf).ToList();
        foreach (var expected in new[] { "Curia.Api", "Curia.Infrastructure", "Curia.Client", "curia-operator", "curia-mcp" })
            Assert.True(names.Contains(expected, StringComparer.Ordinal), $"NOT REACHED: the scan did not include {expected}; it held {string.Join(", ", names)}");

        var api = TypeReferences(scanned.Single(p => AssemblyNameOf(p) == "Curia.Api"));
        Assert.Contains(("Curia.Application", "Curia.Application.Retrieval.EmbeddingIndexer"), api);

        var offenders = scanned
            .SelectMany(p => TypeReferences(p).Select(r => (Host: AssemblyNameOf(p), r.Type)))
            .Where(r => r.Type is "Curia.Application.Retrieval.HashedNGramEmbedder" or "Curia.Domain.Search.HashedNGramEmbedding")
            .Select(r => $"{r.Host} references {r.Type}")
            .Distinct(StringComparer.Ordinal)
            .ToList();
        Assert.True(offenders.Count == 0, string.Join("; ", offenders));
    }

    /// <summary>
    /// SEM14. No type of <c>Microsoft.ML.OnnxRuntime</c> is referenced by a <c>src/</c> assembly but <c>Curia.Infrastructure</c> (CS-7), and <c>Curia.Infrastructure</c> does reference one (so the scan is
    /// not empty); no assembly of the solution references <c>Microsoft.ML.Tokenizers</c>, and no <c>packages.lock.json</c> lists it or <c>Google.Protobuf</c> (the dropped dependency stays dropped,
    /// Amendment 1 ruling 22). Predicted red on the seam: <c>Curia.Infrastructure references no type of Microsoft.ML.OnnxRuntime, so the scan found nothing</c>.
    /// </summary>
    [Fact]
    public void R11_91_SEM14_OnlyInfrastructureReferencesOnnxRuntimeAndTheTokenizerPackageStaysDropped()
    {
        var shipped = Shipped.ShippedAssemblies();
        var holders = shipped
            .Where(p => TypeReferences(p).Any(r => r.Assembly == "Microsoft.ML.OnnxRuntime"))
            .Select(AssemblyNameOf)
            .ToList();
        Assert.True(holders.Contains("Curia.Infrastructure"), "Curia.Infrastructure references no type of Microsoft.ML.OnnxRuntime, so the scan found nothing");
        Assert.True(holders.SequenceEqual(["Curia.Infrastructure"]), "Microsoft.ML.OnnxRuntime is referenced by: " + string.Join(", ", holders));

        var tokenizer = shipped.Select(AssemblyNameOf)
            .Concat([typeof(EmbedderFenceTests).Assembly.GetName().Name!])
            .Where(n => ReferencesAssembly(shipped.FirstOrDefault(p => AssemblyNameOf(p) == n) ?? typeof(EmbedderFenceTests).Assembly.Location, "Microsoft.ML.Tokenizers"))
            .ToList();
        Assert.True(tokenizer.Count == 0, "Microsoft.ML.Tokenizers is referenced by: " + string.Join(", ", tokenizer));

        var root = RepoRoot();
        var locks = Directory.EnumerateFiles(root, "packages.lock.json", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.True(locks.Count >= 10, $"NOT REACHED: only {locks.Count} lock files were found");
        var listing = locks.Where(f =>
        {
            var text = File.ReadAllText(f);
            return text.Contains("\"Microsoft.ML.Tokenizers\"", StringComparison.Ordinal) || text.Contains("\"Google.Protobuf\"", StringComparison.Ordinal);
        }).Select(f => Path.GetRelativePath(root, f)).ToList();
        Assert.True(listing.Count == 0, "a lock file lists the dropped dependency: " + string.Join(", ", listing));
    }

    private static bool ReferencesAssembly(string path, string name)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        return md.AssemblyReferences.Any(h => string.Equals(md.GetString(md.GetAssemblyReference(h).Name), name, StringComparison.Ordinal));
    }

    /// <summary>
    /// SEM17. The port string fence has the <c>EmbedAsync</c> row with its reason and no row for <c>Embed</c> (R11.34's fence follows the port's rename). Expected on the seam: green; the seam renames the
    /// row with the port, and the fence fails the build otherwise. A control.
    /// </summary>
    [Fact]
    public void R11_34_SEM17_TheTextEmbedderRowFollowsThePort()
    {
        var rows = PortStringAllowlist.Rows.Where(r => r.Member.StartsWith("Curia.Application.Ports.ITextEmbedder.", StringComparison.Ordinal)).ToList();

        Assert.Single(rows);
        Assert.Equal("Curia.Application.Ports.ITextEmbedder.EmbedAsync", rows[0].Member);
        Assert.Equal("text", rows[0].Parameter);
        Assert.False(string.IsNullOrWhiteSpace(rows[0].Reason));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find repo root above " + AppContext.BaseDirectory);
    }

    /// <summary>A lock file's packages and what each depends on, for SEM18's closure.</summary>
    internal static Dictionary<string, string[]> LockDependencies(string lockPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var framework in document.RootElement.GetProperty("dependencies").EnumerateObject())
        {
            foreach (var package in framework.Value.EnumerateObject())
            {
                var dependencies = package.Value.TryGetProperty("dependencies", out var d)
                    ? d.EnumerateObject().Select(x => x.Name).ToArray()
                    : [];
                result[package.Name] = dependencies;
            }
        }

        return result;
    }
}
