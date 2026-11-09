using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R10.98 (errata G63), LOB37 and LOB38, fences read from source text and not from names (D61 item 80's lesson): the sentences of the refusals and of the empty board are written by one file of
/// <c>Curia.Client</c>, for <c>curia</c> and <c>curia-mcp</c> alike, and no source file of <c>Curia.Client.Cli</c> or <c>Curia.Mcp</c> holds them (so the two clients cannot drift apart); and the board listing's
/// two string-typed members, <c>NamedFilters.ServedBoards</c>'s predicate and <c>BoardRow</c>'s name, have reasoned rows in the port-string allowlist (R11.34).
///
/// <para><b>Oracles.</b> The fragments are parsed from the errata (<see cref="PublishedBoardListWords"/>), each asserted at least twenty characters before it is searched for, so a fence over words too short
/// to mean anything says so. The fence has a positive half (one file of <c>Curia.Client</c> holds every sentence) so that a build that never writes them is red and not green by absence. Predicted red on the seam:
/// no file holds the sentences, and the allowlist has no row for the two members (the fence of R11.34 itself becomes live only when a host calls them, Task 2).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BoardWordsFenceTests
{
    private static readonly string[] ClientProjects = ["Curia.Client.Cli", "Curia.Mcp"];

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("no 'src' above " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> Sources(string root, string project) =>
        Directory.EnumerateFiles(Path.Combine(root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string[] Fragments()
    {
        var fragments = PublishedBoardListWords.FencedSentences();
        Assert.True(fragments.Length >= 5 && fragments.All(f => f.Trim().Length >= 20), $"NOT REACHED: a fragment of the published words is shorter than 20 characters: {string.Join(" | ", fragments)}");
        return [.. fragments.Select(f => f.Trim())];
    }

    /// <summary>LOB37, the positive half: some one file of <c>Curia.Client</c> holds every fragment. Predicted red on the seam: no file holds even one.</summary>
    [Fact]
    public void R10_98_LOB37_OneFileOfTheClientLibraryHoldsTheSentences()
    {
        var root = Root();
        var fragments = Fragments();
        var holders = Sources(root, "Curia.Client").Where(p => fragments.All(f => File.ReadAllText(p).Contains(f, StringComparison.Ordinal))).Select(p => Path.GetRelativePath(root, p)).ToArray();

        Assert.True(holders.Length == 1, $"expected exactly one file of Curia.Client to hold all {fragments.Length} sentences, found {holders.Length}: {string.Join(", ", holders)}");
    }

    /// <summary>LOB37, the negative half: no file of <c>Curia.Client.Cli</c> or <c>Curia.Mcp</c> contains a fragment. A control while nothing writes the sentences (green on the seam); FLOB52 turns it red by copying them into a second client file.</summary>
    [Fact]
    public void R10_98_LOB37_NoCliOrAdapterFileWritesTheSentencesItself()
    {
        var root = Root();
        var fragments = Fragments();
        var sources = ClientProjects.SelectMany(p => Sources(root, p)).ToArray();
        Assert.True(sources.Length > 30, $"NOT REACHED: only {sources.Length} source files were read");

        var found = sources.SelectMany(path => fragments.Where(f => File.ReadAllText(path).Contains(f, StringComparison.Ordinal)).Select(f => $"{Path.GetRelativePath(root, path)}: \"{f}\"")).ToArray();
        Assert.True(found.Length == 0, $"a client file writes the sentences itself: {string.Join(" | ", found)}");
    }

    /// <summary>LOB38, the port-string fence: <c>NamedFilters.ServedBoards</c>'s <c>servable</c> and <c>BoardRow</c>'s <c>Board</c> have rows in <c>PortStringAllowlist</c> with a reason. Predicted red on the seam: no row (the seam's route does not call them yet).</summary>
    [Fact]
    public void R11_34_LOB38_TheListingsStringMembersHaveReasonedRows()
    {
        var rows = PortStringAllowlist.Rows.Select(r => $"{r.Member}({r.Parameter})").ToHashSet(StringComparer.Ordinal);
        string[] needed = ["Curia.Application.Projections.NamedFilters.ServedBoards(servable)", "Curia.Application.Projections.BoardRow..ctor(Board)"];
        var missing = needed.Where(n => !rows.Contains(n)).ToArray();

        Assert.True(missing.Length == 0, $"the allowlist has no reasoned row for: {string.Join(", ", missing)}");
    }
}
