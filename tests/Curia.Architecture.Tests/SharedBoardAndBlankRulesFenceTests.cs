using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R11.78 (errata G56), NAM34: a fence and not a convention. <c>Curia.Client.Cli</c> and <c>Curia.Mcp</c> ask whether a search criterion is blank
/// from <c>SearchRequestRules</c> and whether a board is a name from <c>BoardRules</c> (through <c>SubmissionBuilder.Build</c> for what they sign, and
/// directly for the board a reply tool copies from a post it read). Neither holds a blank test or a board predicate of its own: a second copy is how
/// the two doors that "refuse every other blank" came to refuse one criterion each (the register's D102, which was true of one door).
///
/// <para>A source scan, the shape of <see cref="ParentReadFenceTests"/>: the call sites that must exist (a positive row per entry point) and the
/// definitions that must not (no second <c>BlankCriterion</c>, <c>HasCriterion</c> or board-class function in either assembly). The positive rows are
/// red until the build; the negative rows are controls.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class SharedBoardAndBlankRulesFenceTests
{
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not find a 'src' directory above " + AppContext.BaseDirectory);
    }

    private static string Source(string relative) => File.ReadAllText(Path.Combine(RepositoryRoot(), relative.Replace('/', Path.DirectorySeparatorChar)));

    [GeneratedRegex(@"SearchRequestRules\.BlankCriterion\(", RegexOptions.CultureInvariant)]
    private static partial Regex BlankCriterionCall();

    [GeneratedRegex(@"BoardRules\.Classes\(", RegexOptions.CultureInvariant)]
    private static partial Regex BoardClassesCall();

    [GeneratedRegex(@"\b(?:static|internal|private|public)\b[^;{=]*\b(?:BlankCriterion|HasCriterion|BoardClasses|BoardIssues)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex SecondDefinition();

    /// <summary>The entry points that must ask the shared function, and how many times at least: <c>curia search</c>, <c>curia inbox</c>, <c>curia_search</c>.</summary>
    private static readonly (string File, string Function, Func<Regex> Call, int AtLeast)[] CallRows =
    [
        ("src/Curia.Client.Cli/Program.cs", "curia search and curia inbox (SearchAsync, InboxAsync)", BlankCriterionCall, 2),
        ("src/Curia.Mcp/ForumTools.cs", "curia_search (SearchAsync)", BlankCriterionCall, 1),
        ("src/Curia.Mcp/WriteTools.cs", "the reply tools (ReplyAsync and ReviseAsync: the board copied from a post)", BoardClassesCall, 2),
    ];

    /// <summary>NAM34: each entry point calls the shared function; red until the clients are built on it.</summary>
    [Fact]
    public void R11_78_NAM34_EveryEntryPointAsksTheSharedFunction()
    {
        var problems = new List<string>();
        foreach (var (file, function, call, atLeast) in CallRows)
        {
            var count = call().Count(Source(file));
            if (count < atLeast) problems.Add($"{file}: {function} calls the shared function {count} time(s); {atLeast} required (it tests a criterion or a board itself, or not at all)");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>NAM34: neither client assembly defines a second blank test or board predicate (a control, green on the seam).</summary>
    [Fact]
    public void R11_78_NAM34_NeitherClientDefinesASecondOne()
    {
        var root = RepositoryRoot();
        var scanned = 0;
        var problems = new List<string>();
        foreach (var project in new[] { "src/Curia.Client.Cli", "src/Curia.Mcp" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, project.Replace('/', Path.DirectorySeparatorChar)), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) continue;
                scanned++;
                if (SecondDefinition().IsMatch(File.ReadAllText(file))) problems.Add($"{relative} defines its own blank-criterion or board-class function");
            }
        }

        Assert.True(scanned > 20, $"the scan read {scanned} source files");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
