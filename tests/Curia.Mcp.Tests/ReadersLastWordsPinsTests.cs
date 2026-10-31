using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// PR 3 (errata G50, R11.68, R11.66), Task 0's probes RLW39, RLW40 and RLW41 at the adapter: <c>curia_verify</c>'s description carries R11.68's advice for a
/// batch of verifications, the version <c>ToolCatalogue.Version</c> is the one R11.66 names through the one reader of an errata version, and G50's words are in
/// the errata with the reasons, members, spans, bound and version the other probes parse. Every word is parsed from the errata
/// (<see cref="PublishedReadersLastWords"/>); a production constant is never its own expectation.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ReadersLastWordsPinsTests
{
    private static string DescriptionOf(string tool)
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        return ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, new HeadStore(Path.Combine(Path.GetTempPath(), "curia-tool-descriptions-never-written")), null))
            .Single(t => t.ProtocolTool.Name == tool).ProtocolTool.Description ?? string.Empty;
    }

    /// <summary>RLW39 (the description's half; the help's is in the client's <c>NotServedWordsTests</c>). <c>curia_verify</c>'s description carries R11.68's three clauses: verifications sent together may differ in the retained head by which call stored it first, a verification in the same batch as its read is fetched afresh, and one verdict is had by reading first and pinning <c>expectedDigest</c>.</summary>
    [Fact]
    public void R11_68_RLW39_TheDescriptionOfVerifyAdvisesTheHostThatBatches()
    {
        var description = PublishedReadersLastWords.Folded(DescriptionOf("curia_verify"));
        var missing = PublishedReadersLastWords.BatchClauses().Where(c => !description.Contains(PublishedReadersLastWords.Folded(c), StringComparison.Ordinal)).ToArray();
        Assert.True(missing.Length == 0, $"Expected: curia_verify's description to carry {string.Join(" and ", missing.Select(m => $"\"{m}\""))}");
    }

    /// <summary>The scanner's own file and the one reader, which hold the pattern as text.</summary>
    private static readonly string[] ExemptFiles = ["PublishedReadersLastWords.cs", "ReadersLastWordsPinsTests.cs"];

    [GeneratedRegex(@"GeneratedRegex\(.*curia-mcp.*\\d\+\\\.\\d\+\\\.\\d\+", RegexOptions.CultureInvariant)]
    private static partial Regex VersionReader();

    private static string Tests()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests", "Shared")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("no repository root above " + AppContext.BaseDirectory), "tests");
    }

    /// <summary>
    /// RLW40. <c>ToolCatalogue.Version</c> is the version R11.66 names, parsed through <c>PublishedReadersLastWords.Version()</c>, and that is the one reader of a
    /// <c>curia-mcp</c> version out of an errata paragraph in <c>tests/</c>: the two that exist (<c>PublishedBudgetReadout</c>'s and <c>PublishedChainWords</c>'s) are
    /// replaced by it (ruling 23(b)).
    /// </summary>
    [Fact]
    public void R11_66_RLW40_TheVersionIsTheOneR1166NamesAndHasOneReader()
    {
        var named = PublishedReadersLastWords.Version();
        var problems = new List<string>();
        if (!string.Equals(named, ToolCatalogue.Version, StringComparison.Ordinal)) problems.Add($"Expected: \"{named}\" Actual: \"{ToolCatalogue.Version}\"");

        var readers = Directory.EnumerateFiles(Tests(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !ExemptFiles.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f => File.ReadAllLines(f).Any(l => VersionReader().IsMatch(l)))
            .Select(Path.GetFileName).ToArray();
        if (readers.Length > 0) problems.Add($"another reader of a curia-mcp version out of an errata paragraph: {string.Join(", ", readers)}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// RLW41, a control. G50's words exist and agree: R7.30, R8.80, R10.91, R10.92, R11.66, R11.67 and R11.68 are in the errata with their index rows, and every
    /// reason, member, kind word, definition, span, bound and the version the other probes parse is there, once. Red if a span is removed.
    /// </summary>
    [Fact]
    public void R11_68_RLW41_G50sWordsExistAndAgree()
    {
        foreach (var anchor in PublishedReadersLastWords.Anchors) Assert.NotEmpty(PublishedAcceptBudget.Paragraph(anchor));
        Assert.Equal(PublishedReadersLastWords.IndexRows.Length, PublishedReadersLastWords.IndexRowsPresent().Length);

        Assert.StartsWith("table-10/", PublishedReadersLastWords.OwnResourceFound(), StringComparison.Ordinal);
        Assert.StartsWith("table-10/", PublishedReadersLastWords.OwnResourceOnly(), StringComparison.Ordinal);
        Assert.NotEqual(PublishedReadersLastWords.OwnResourceFound(), PublishedReadersLastWords.OwnResourceOnly());
        Assert.NotEmpty(PublishedReadersLastWords.FoundTitle());
        Assert.Equal(("post_kind", "text_of"), PublishedReadersLastWords.ReceiptMembers());
        Assert.Equal(("answer", "revision"), PublishedReadersLastWords.KindWords());
        Assert.Contains("recheck", PublishedReadersLastWords.DefinitionSentence(), StringComparison.Ordinal);
        Assert.StartsWith("not served", PublishedReadersLastWords.DefinitionSentence(), StringComparison.Ordinal);
        Assert.NotEmpty(PublishedReadersLastWords.PointerTail());
        Assert.Equal("kind NOT STATED", PublishedReadersLastWords.KindNotStated());
        Assert.NotEmpty(PublishedReadersLastWords.TextOfPhrase());
        Assert.NotEmpty(PublishedReadersLastWords.ReadLines().NotRead);
        Assert.NotEmpty(PublishedReadersLastWords.NotReadCause());
        Assert.NotEmpty(PublishedReadersLastWords.NotListedCause().DoesNotList);
        Assert.NotEmpty(PublishedReadersLastWords.PlacementClause());
        Assert.Equal("curia_recheck", PublishedReadersLastWords.NoteTool());
        Assert.Matches(@"^\d+\.\d+\.\d+$", PublishedReadersLastWords.Version());
        Assert.Equal(8, PublishedReadersLastWords.WalkBound());
        Assert.Equal("at most eight posts after the question", PublishedReadersLastWords.BoundSentence());
        Assert.Equal(("NOT WALKED", "NOT CHECKED", "FORKED"), PublishedReadersLastWords.WalkWords());
        Assert.Equal("none was followed", PublishedReadersLastWords.NoneFollowed());
        Assert.NotEmpty(PublishedReadersLastWords.LatestPhrase());
        Assert.Equal(3, PublishedReadersLastWords.BatchClauses().Length);
        Assert.NotEmpty(PublishedReadersLastWords.G50());
    }
}
