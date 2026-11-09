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

    /// <summary>LOB38, the port-string fence: <c>NamedFilters.ServedBoards</c>'s <c>servable</c> has a row in <c>PortStringAllowlist</c> with a reason. Predicted red on the seam: no row (the seam's route does not call them yet).</summary>
    [Fact]
    public void R11_34_LOB38_TheListingsStringMembersHaveReasonedRows()
    {
        var rows = PortStringAllowlist.Rows.Select(r => $"{r.Member}({r.Parameter})").ToHashSet(StringComparer.Ordinal);
        // BoardRow's name is no row: the fence reads a row for a member in its scope (a port or a host-called use case), and a projection record is neither, so a row for it is stale (measured at Task 2).
        string[] needed = ["Curia.Application.Projections.NamedFilters.ServedBoards(servable)"];
        var missing = needed.Where(n => !rows.Contains(n)).ToArray();

        Assert.True(missing.Length == 0, $"the allowlist has no reasoned row for: {string.Join(", ", missing)}");
    }

    private static IEnumerable<string> AllSources(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    private static string Folded(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    /// <summary>
    /// LOB51 (Amendment 2, ruling 38), the sentences of "R10.98 words (the rest)" are written once in <c>src</c>: each, cut to a run with no placeholder (whitespace folded in the source and in the sentence), occurs
    /// exactly once across every source file, and the list of them is parsed from the errata and has at least six sentences of at least twenty characters. Predicted red where a sentence is written twice
    /// (FLOB81's second form), where it is not written as published (the help of <c>ask</c> as it stood) and where the parse found fewer.
    /// </summary>
    [Fact]
    public void R10_98_LOB51_EachSentenceOfTheRestIsWrittenOnceInSource()
    {
        var root = Root();
        var sentences = PublishedBoardListWords.RestSentences().Select(Folded).ToArray();
        Assert.True(sentences.Length >= 6 && sentences.All(f => f.Trim().Length >= 20), $"NOT REACHED: the parse of the rest holds {sentences.Length} sentences, one shorter than 20 characters or fewer than six");

        var texts = AllSources(root).Select(path => (Path: path, Text: Folded(File.ReadAllText(path)))).ToArray();
        Assert.True(texts.Length > 100, $"NOT REACHED: only {texts.Length} source files were read");

        var found = sentences.Select(f => (Sentence: f, Count: texts.Sum(t => CountOf(t.Text, f)))).Where(x => x.Count != 1)
            .Select(x => $"\"{x.Sentence[..Math.Min(40, x.Sentence.Length)]}...\" occurs {x.Count} times").ToArray();
        Assert.True(found.Length == 0, string.Join(" | ", found));

        static int CountOf(string text, string needle)
        {
            var count = 0;
            for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal)) count++;
            return count;
        }
    }

    /// <summary>
    /// LOB52 (ruling 39), no source says a command is safe to run again, or may be run again, without the condition: the phrase <c>running the same command again is safe</c> occurs nowhere in <c>src</c>, and each <c>may be run again</c>
    /// is followed within 120 characters by <c>unless an earlier run</c>, and each <c>may be made again</c> (the adapter's descriptions) is followed within 160 characters by <c>unless an earlier</c> (Amendment 3, ruling 55).
    /// Predicted red on a source that restores either sentence (FLOB82, FLOB83 and FLOB90).
    /// </summary>
    [Fact]
    public void R10_98_LOB52_NoSourceSaysACommandIsSafeOrMayBeRunAgainWithoutTheCondition()
    {
        var root = Root();
        var bad = new List<string>();
        var files = AllSources(root).ToArray();
        Assert.True(files.Length > 100, $"NOT REACHED: only {files.Length} source files were read");
        foreach (var path in files)
        {
            var text = Folded(File.ReadAllText(path));
            if (text.Contains("running the same command again is safe", StringComparison.Ordinal)) bad.Add($"{Path.GetRelativePath(root, path)}: says \"running the same command again is safe\"");
            for (var at = text.IndexOf("may be run again", StringComparison.Ordinal); at >= 0; at = text.IndexOf("may be run again", at + 1, StringComparison.Ordinal))
            {
                if (!text[at..Math.Min(text.Length, at + 120)].Contains("unless an earlier run", StringComparison.Ordinal))
                    bad.Add($"{Path.GetRelativePath(root, path)}: says \"may be run again\" without the condition");
            }

            for (var at = text.IndexOf("may be made again", StringComparison.Ordinal); at >= 0; at = text.IndexOf("may be made again", at + 1, StringComparison.Ordinal))
            {
                if (!text[at..Math.Min(text.Length, at + 160)].Contains("unless an earlier", StringComparison.Ordinal))
                    bad.Add($"{Path.GetRelativePath(root, path)}: says \"may be made again\" without the condition");
            }
        }

        Assert.True(bad.Count == 0, string.Join(" | ", bad));
    }
}
