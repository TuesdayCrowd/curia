using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// VER1 (item 344, plan ruling 14 and ruling 20): the version of <c>curia-mcp</c> is held by four sources, each checked against another, and by one hand-written twin.
/// The sources are the errata's highest-numbered <c>R11.n</c> paragraph that says <c>`curia-mcp` is X.Y.Z</c> (read by <c>PublishedReadersLastWords.Version()</c>, the one parser),
/// <c>ToolCatalogue.Version</c>, and the README's two statements. Every other test calls the parser or the constant, and this fence fails on a version-shaped literal
/// (<c>0.n.n</c> inside a string literal, outside a comment) anywhere under <c>tests/</c> that is not a reasoned row, by file and count, as the parent-read fence holds a read by file and count (in Curia.Mcp.Tests because <c>ToolCatalogue</c> is internal to the adapter). A bump is four edits (errata, constant, README, twin) and each is checked by the other three.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class VersionFenceTests
{
    /// <summary>The version-shaped string literals allowed, by file (relative to <c>tests/</c>, forward slashes): how many, and why none of them follows the current version.</summary>
    private static readonly (string File, int Literals, string Reason)[] Rows =
    [
        ("Curia.Mcp.Tests/BudgetToolTests.cs", 1, "the hand-written twin (ruling 14): the one independent literal of the current version, so that a wrong errata and a wrong constant that agree are still caught; a bump edits it by hand"),
        ("Curia.Mcp.Tests/BoardNameToolTests.cs", 1, "NAM32's historical literal beside PublishedBoardWords.Version(): R11.78's version at its own build, a different artifact and a different fact from the current version; no bump moves it (ruling 20)"),
        ("Curia.Mcp.Tests/ReplyBoardToolTests.cs", 2, "R11.85's history, the version that requirement was built at (RequirementNamesMcpVersion), in the check and in its message; no bump moves it"),
        ("Curia.Mcp.Tests/UnheardActToolTests.cs", 2, "R11.81 words' history, the version they were built at, in the check and in the expected text; no bump moves it"),
    ];

    /// <summary>VER1 (scan): no version-shaped string literal under <c>tests/</c> outside the reasoned rows, and none of a row's count exceeded.</summary>
    [Fact]
    public void VER1_NoVersionLiteralUnderTestsButTheReasonedRows()
    {
        var root = Root();
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Path.Combine(root, "tests"), path).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal)) continue;
            var count = File.ReadLines(path).Sum(line => Literal().Count(WithoutComment(line)));
            if (count > 0) found[relative] = count;
        }

        var problems = new List<string>();
        foreach (var (file, count) in found)
        {
            var allowed = Rows.Where(r => r.File == file).Select(r => r.Literals).DefaultIfEmpty(0).Single();
            if (count > allowed) problems.Add($"{file} holds {count} version-shaped literal(s), its row allows {allowed}");
        }

        foreach (var row in Rows.Where(r => !found.ContainsKey(r.File))) problems.Add($"{row.File} holds none, its row allows {row.Literals}: remove the row");
        foreach (var row in Rows.Where(r => found.TryGetValue(r.File, out var n) && n < r.Literals)) problems.Add($"{row.File} holds {found[row.File]}, its row says {row.Literals}: correct the row");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>VER1 (agreement): the parser's value is the constant's and is both statements in the README; the twin's literal is the same.</summary>
    [Fact]
    public void VER1_TheParserTheConstantTheReadmeAndTheTwinAgree()
    {
        var root = Root();
        var parsed = PublishedReadersLastWords.Version();
        var problems = new List<string>();
        if (parsed != ToolCatalogue.Version) problems.Add($"the parser reads {parsed}, ToolCatalogue.Version is {ToolCatalogue.Version}");

        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var isForm = Regex.Matches(readme, @"`curia-mcp` is (\d+\.\d+\.\d+)", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        var dotForm = Regex.Matches(readme, @"`curia-mcp` (\d+\.\d+\.\d+)\.", RegexOptions.CultureInvariant).Select(m => m.Groups[1].Value).ToArray();
        if (!isForm.Contains(parsed)) problems.Add($"README.md does not say `curia-mcp` is {parsed}");
        foreach (var other in isForm.Where(v => v != parsed).Distinct(StringComparer.Ordinal)) problems.Add($"README.md says `curia-mcp` is {other}");
        if (!dotForm.Contains(parsed)) problems.Add($"README.md does not end a sentence with `curia-mcp` {parsed}.");
        var newest = isForm.Concat(dotForm).Select(v => Version.Parse(v)).Max()!.ToString();
        if (newest != parsed) problems.Add($"README.md's newest `curia-mcp` version is {newest}");

        var twin = File.ReadAllText(Path.Combine(root, "tests", "Curia.Mcp.Tests", "BudgetToolTests.cs"));
        var twinNewest = Literal().Matches(string.Join('\n', twin.Split('\n').Select(WithoutComment))).Select(m => Version.Parse(m.Value.Trim('"'))).Max()!.ToString();
        if (twinNewest != parsed) problems.Add($"the hand-written twin says {twinNewest}");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>The line up to a <c>//</c> comment that is not inside a string literal.</summary>
    private static string WithoutComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\')) inString = !inString;
            else if (!inString && line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/') return line[..i];
        }

        return line;
    }

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>A <c>0.n.n</c> number inside a string literal that is not part of a longer dotted number (an address is not a version).</summary>
    [GeneratedRegex("\"[^\"\\n]*?(?<![\\d.])0\\.\\d+\\.\\d+(?![\\d.])[^\"\\n]*\"", RegexOptions.CultureInvariant)]
    private static partial Regex Literal();
}
