using System.Globalization;
using System.Text.RegularExpressions;

namespace Curia.Tests.Shared;

/// <summary>
/// The edge's published figures (errata G83, R7.33's table; R6.39's submission size), read at test time from the documents that publish them and never from the recipe a probe is about
/// (the Table 10 pattern: the check derives from the white paper and the errata, the configuration from the recipe). Each parse asserts its count before it returns, so an edit that removes or
/// duplicates a row turns a probe red by name and does not shrink what it compares.
/// </summary>
internal static partial class PublishedEdge
{
    internal sealed record Zone(string Name, string Route, double RatePerSecond, int Burst);

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root (a 'src' directory) above " + AppContext.BaseDirectory);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(RepoRoot(), name));

    /// <summary>G83's table: the two zones, their routes, rates and bursts.</summary>
    internal static IReadOnlyList<Zone> Zones()
    {
        var errata = Read("curia-whitepaper-ERRATA-AND-ADDENDUM.md");
        var start = errata.IndexOf("**R7.33**", StringComparison.Ordinal);
        if (start < 0) throw new InvalidOperationException("the errata has no R7.33 (G83)");
        var section = errata[start..Math.Min(errata.Length, start + 4000)];
        var rows = ZoneRow().Matches(section)
            .Select(m => new Zone(
                m.Groups[1].Value,
                m.Groups[2].Value,
                double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture)))
            .ToList();
        if (rows.Count != 2) throw new InvalidOperationException($"G83's table should hold 2 zones and holds {rows.Count}");
        return rows;
    }

    /// <summary>R6.39's submission size in bytes, as the white paper states it (1 MiB, 1,048,576 bytes).</summary>
    internal static long SubmissionCapBytes()
    {
        var paper = Regex.Replace(Read("curia-agent-forum-WHITEPAPER.md"), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(5));
        var m = CapPhrase().Matches(paper);
        if (m.Count != 1) throw new InvalidOperationException($"R6.39's submission size should be stated once and is stated {m.Count} times");
        return long.Parse(m[0].Groups[1].Value.Replace(",", "", StringComparison.Ordinal), CultureInfo.InvariantCulture);
    }

    [GeneratedRegex(@"\| `(curia_[a-z]+)` \| `[A-Z]+ ([^`]+)` \| [^|]+ \| (\d+(?:\.\d+)?) r/s \| (\d+) \|")]
    private static partial Regex ZoneRow();

    [GeneratedRegex(@"maximum submission size \*\*1 MiB\*\* \(([\d,]+) bytes\)")]
    private static partial Regex CapPhrase();
}
