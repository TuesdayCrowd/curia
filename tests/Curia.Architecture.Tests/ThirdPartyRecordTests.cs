using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Errata G65 (D10a), SEM18: <c>docs/third-party.md</c> is the record of dependencies Appendix I's R I.1 has never had (reading finding 10, Ruling 17). Three different artifacts are compared:
/// the ids of <c>Directory.Packages.props</c>, the closure of <c>Microsoft.ML.OnnxRuntime</c> in the lock file of <c>Curia.Infrastructure</c>, and the table in the document.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ThirdPartyRecordTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not find repo root above " + AppContext.BaseDirectory);
    }

    /// <summary>The ids in the first cell of each table row of the document (a row is a line that begins <c>|</c>), with the whole row.</summary>
    private static Dictionary<string, string> Rows(string document)
    {
        var rows = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in document.Split('\n').Where(l => l.TrimStart().StartsWith('|')))
        {
            var cells = line.Trim().Trim('|').Split('|').Select(c => c.Trim().Trim('`')).ToArray();
            if (cells.Length < 3 || cells[0].StartsWith("---", StringComparison.Ordinal) || cells[0].Equals("Package", StringComparison.OrdinalIgnoreCase)) continue;
            rows[cells[0]] = line;
        }

        return rows;
    }

    private static HashSet<string> Closure(Dictionary<string, string[]> graph, string start)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>([start]);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!seen.Add(id)) continue;
            if (graph.TryGetValue(id, out var dependencies))
                foreach (var d in dependencies) queue.Enqueue(d);
        }

        return seen;
    }

    /// <summary>
    /// SEM18. The record holds one row per <c>PackageVersion</c> id of the props file and one per package in the closure of <c>Microsoft.ML.OnnxRuntime</c> in the Infrastructure lock file, each with a licence
    /// cell, and no row the two do not name (except the model and the tokenizer, which are not packages); it words the native library's notices (Eigen under MPL-2.0, Mbed TLS taken under Apache-2.0 by the notice's
    /// own words, Intel MKL, 70 components); and it names the model with MIT and "training-data provenance was not reviewed". Predicted red on the seam: <c>docs/third-party.md does not exist</c>.
    /// </summary>
    [Fact]
    public void R_I_1_SEM18_TheRecordOfDependenciesMatchesThePropsAndTheLockedClosure()
    {
        var root = Root();
        var path = Path.Combine(root, "docs", "third-party.md");
        Assert.True(File.Exists(path), "docs/third-party.md does not exist");
        var document = File.ReadAllText(path);
        var rows = Rows(document);

        var props = XDocument.Load(Path.Combine(root, "Directory.Packages.props")).Descendants("PackageVersion").Select(p => (string)p.Attribute("Include")!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("Microsoft.ML.OnnxRuntime", props);
        var closure = Closure(EmbedderFenceTests.LockDependencies(Path.Combine(root, "src", "Curia.Infrastructure", "packages.lock.json")), "Microsoft.ML.OnnxRuntime");
        Assert.True(closure.Count >= 3, $"NOT REACHED: the closure of Microsoft.ML.OnnxRuntime holds {closure.Count} package(s), expected the package and its two transitives");

        var expected = props.Union(closure, StringComparer.OrdinalIgnoreCase).ToList();
        var missing = expected.Where(id => !rows.ContainsKey(id)).ToList();
        Assert.True(missing.Count == 0, "the record has no row for: " + string.Join(", ", missing));
        var unknown = rows.Keys.Except(expected, StringComparer.OrdinalIgnoreCase).Where(k => !k.Contains("bge-small", StringComparison.OrdinalIgnoreCase) && !k.Contains("tokenizer", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.True(unknown.Count == 0, "the record has a row for a package the props and the lock do not name: " + string.Join(", ", unknown));

        foreach (var id in expected)
            Assert.Matches(@"\b(MIT|Apache-2\.0|BSD-3-Clause|BSD-2-Clause)\b", rows[id]);

        foreach (var word in new[] { "Eigen", "MPL-2.0", "Mbed TLS", "Apache-2.0", "Intel", "70 components", "ThirdPartyNotices" })
            Assert.Contains(word, document, StringComparison.Ordinal);

        var model = rows.Single(r => r.Key.Contains("bge-small", StringComparison.OrdinalIgnoreCase)).Value;
        Assert.Contains("MIT", model, StringComparison.Ordinal);
        Assert.Contains("training-data provenance was not reviewed", document, StringComparison.Ordinal);
        Assert.Contains("written in this repository", document, StringComparison.OrdinalIgnoreCase);
    }
}
