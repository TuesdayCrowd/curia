using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R11.85 (errata G61), RPB32: the reply tools take the board of the post they read and ask no board rule of their own, so the fence row that required them to call
/// <c>BoardRules.Refusal(</c> goes with the pre-check (trap 171: found by grep, not asserted). Three facts about the tree after the build: the fence
/// <see cref="SharedBoardAndBlankRulesFenceTests"/> holds two rows (<c>curia search</c> and <c>curia inbox</c>, <c>curia_search</c>); <c>WriteTools.cs</c> calls <c>BoardRules.</c>
/// nowhere; and no <c>ReplySentence</c> symbol remains in <c>src/</c> (amendment 16). The first is read from the fence's own row table by reflection, so deleting the row is what
/// turns it green. Predicted red on the seam: three rows, one call site each in <c>WriteTools.cs</c> and two <c>ReplySentence</c> occurrences.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ReplyBoardFenceTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not find a 'src' directory above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void R11_85_RPB32_TheReplyToolsAskNoBoardRuleAndTheFenceHoldsTwoRows()
    {
        var root = RepositoryRoot();
        var problems = new List<string>();

        var rows = typeof(SharedBoardAndBlankRulesFenceTests).GetField("CallRows", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) as Array;
        Assert.NotNull(rows);
        if (rows.Length != 2) problems.Add($"SharedBoardAndBlankRulesFenceTests holds {rows.Length} call rows, expected 2");

        var writeTools = File.ReadAllText(Path.Combine(root, "src", "Curia.Mcp", "WriteTools.cs"));
        var calls = System.Text.RegularExpressions.Regex.Count(writeTools, @"BoardRules\.", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (calls != 0) problems.Add($"WriteTools.cs refers to BoardRules. {calls} time(s), expected 0");

        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                continue;
            scanned++;
            var count = System.Text.RegularExpressions.Regex.Count(File.ReadAllText(file), "ReplySentence", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            if (count > 0) problems.Add($"{Path.GetRelativePath(root, file).Replace('\\', '/')} holds {count} occurrence(s) of ReplySentence");
        }

        Assert.True(scanned > 100, $"the scan read {scanned} source files");
        Assert.True(problems.Count == 0, "Expected: two fence rows, no BoardRules. in WriteTools.cs and no ReplySentence in src/" + Environment.NewLine + "Actual:   " + string.Join("; ", problems));
    }
}
