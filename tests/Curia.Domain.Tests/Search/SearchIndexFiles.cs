using System.Globalization;
using System.Text;

namespace Curia.Domain.Tests.Search;

/// <summary>Where the committed index files are, and the one generator of the tag fold table (errata G88, R9.75). Test-side only: the build reads the files, it never generates them.</summary>
internal static class SearchIndexFiles
{
    public static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "conformance")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("No repository root (a directory holding conformance/) above " + AppContext.BaseDirectory);
    }

    public static string Retrieval(string name) => Path.Combine(RepoRoot(), "conformance", "retrieval", name);

    /// <summary>
    /// The fold table of this runtime: every scalar value as a one-scalar string, bucketed by the comparer's <i>in-process</i> hash (no hash is written: they are per process), each bucket's members grouped by
    /// <c>string.Equals(.., OrdinalIgnoreCase)</c>; a class's representative is its least scalar; one line per scalar that is not its own representative, sorted by scalar.
    /// </summary>
    public static string GenerateTagFold()
    {
        var buckets = new Dictionary<int, List<int>>();
        for (var scalar = 0; scalar <= 0x10FFFF; scalar++)
        {
            if (scalar is >= 0xD800 and <= 0xDFFF) continue;
            var hash = StringComparer.OrdinalIgnoreCase.GetHashCode(char.ConvertFromUtf32(scalar));
            if (!buckets.TryGetValue(hash, out var members)) buckets[hash] = members = [];
            members.Add(scalar);
        }

        var representative = new SortedDictionary<int, int>();
        foreach (var members in buckets.Values)
        {
            // Members arrive in ascending scalar order, so the first member of a class is its least.
            var classes = new List<(string Text, int Least)>();
            foreach (var scalar in members)
            {
                var text = char.ConvertFromUtf32(scalar);
                var match = classes.FindIndex(c => string.Equals(c.Text, text, StringComparison.OrdinalIgnoreCase));
                if (match < 0) classes.Add((text, scalar));
                else representative[scalar] = classes[match].Least;
            }
        }

        var table = new StringBuilder();
        foreach (var (scalar, least) in representative)
            table.Append(scalar.ToString("X4", CultureInfo.InvariantCulture)).Append('\t').Append(least.ToString("X4", CultureInfo.InvariantCulture)).Append('\n');
        return table.ToString();
    }
}
