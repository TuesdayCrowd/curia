using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Errata G88, R9.75, R11.114, R11.115: the keyword channel's two tables have one writer. Only <c>SearchIndexWriter</c> inserts into <c>search_docs</c> or <c>search_terms</c> (the event store's append and the
/// insert-only reconcile both call it, so a row is decided by one function), and no source file of <c>src/</c> updates, deletes from or truncates either table (a row is never changed by the Forum; the database's owner
/// removes a wrong one). Read from the source text, where a SQL statement is written; the positive control is that the one writer is found.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class SearchIndexWriteFenceTests
{
    [GeneratedRegex(@"INSERT\s+INTO\s+[^;]{0,80}search_(docs|terms)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex InsertIntoTheTables();

    [GeneratedRegex(@"(UPDATE|DELETE\s+FROM|TRUNCATE(\s+TABLE)?)\s+[^;]{0,80}search_(docs|terms|index_versions)\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 2000)]
    private static partial Regex ChangesTheTables();

    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no repository root above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<(string Relative, string Code)> SourceFiles()
    {
        var repo = Repo();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(repo, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var sep = Path.DirectorySeparatorChar;
            if (file.Contains($"{sep}obj{sep}", StringComparison.Ordinal) || file.Contains($"{sep}bin{sep}", StringComparison.Ordinal)) continue;

            // Comments are prose: a sentence that says "no UPDATE of search_terms" is not a statement.
            var code = string.Join('\n', File.ReadAllLines(file).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
            yield return (Path.GetRelativePath(repo, file), code);
        }
    }

    [Fact]
    public void R11_115_SI7_OnlyTheOneWriterInsertsIntoTheSearchTables()
    {
        var writers = SourceFiles().Where(f => InsertIntoTheTables().IsMatch(f.Code)).Select(f => f.Relative).ToArray();

        Assert.Equal(["src/Curia.Infrastructure/Search/SearchIndexWriter.cs"], writers);
    }

    [Fact]
    public void R11_114_SI7_NoSourceUpdatesDeletesFromOrTruncatesTheSearchTables()
    {
        var found = SourceFiles().Where(f => ChangesTheTables().IsMatch(f.Code)).Select(f => f.Relative).ToArray();

        Assert.True(found.Length == 0, "these files change a search table, which the Forum never does: " + string.Join(", ", found));
    }

    [Fact]
    public void R11_114_SI7_TheFenceSeesAChangingStatementWhenThereIsOne()
    {
        // A control for the fence itself: the patterns match the statements they exist for.
        Assert.Matches(InsertIntoTheTables(), "INSERT INTO \"x\".search_terms (a) VALUES (1)");
        Assert.Matches(ChangesTheTables(), "UPDATE \"x\".search_terms SET body_tf = 1");
        Assert.Matches(ChangesTheTables(), "DELETE FROM search_docs");
        Assert.Matches(ChangesTheTables(), "TRUNCATE search_index_versions");
    }
}
