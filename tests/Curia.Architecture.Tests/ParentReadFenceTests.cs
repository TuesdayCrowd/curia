using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// PR 3 (errata G47, plan ruling 23), CHN55: every read of a post's <c>.Parent</c> in <c>src/</c> is a reasoned row, held as a build-failing fence by
/// file and count, as <see cref="PortStringFenceTests"/> holds a port's <c>string</c>. A revision's <c>parent</c> is its author's word and no listing
/// reads it to place a post (R9.48); the reads below are the ones Task 0's M4 found, none of which places a revision. A read in a file with no row, or
/// more reads in a file than its row allows, fails and names the file, so a new reader of the author's word that decides something is a review
/// by build failure and not the next exercise's finding.
///
/// <para>The scan is a source scan of every <c>*.cs</c> under <c>src/</c> (outside <c>bin</c> and <c>obj</c>), for the member access <c>.Parent</c>
/// as a whole word and the property pattern <c>Parent:</c>. It is a control, green on the unmodified tree, and goes red under FCHN68 (a new placement read).</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ParentReadFenceTests
{
    /// <summary>The reads, by file (repository-relative, forward slashes): how many, and why none of them places a revision.</summary>
    private static readonly (string File, int Reads, string Reason)[] Rows =
    [
        ("src/Curia.Application/Ingest/IngestPipeline.cs", 3, "ADMIT parses the submitted parent as a reference (two reads on one line), and PERSIST copies it into the event payload; neither decides a thread"),
        ("src/Curia.Application/Projections/PostProjection.cs", 1, "placement of a post of any kind but a revision (R9.48): the one place a parent places a post"),
        ("src/Curia.Application/Projections/AcceptedAnswerIndex.cs", 1, "ThreadOf: the question an answer is attached to, read from the chain root's parent, never from a revision's"),
        ("src/Curia.Application/Projections/InboxSelection.cs", 1, "the inbox counts the answers whose parent is a question; answers only"),
        ("src/Curia.Api/ForumEndpoints.cs", 4, "the existence check of a submitted answer or comment, G38 (944), the accept route reading the chain root's parent (1760), the duplicate refusal's filter on answers (1923), the wire copy (2582)"),
        ("src/Curia.Client/TitledIndex.cs", 1, "prints the author's parent beside a title, escaped, as the author's word"),
        ("src/Curia.Client/Passage.cs", 1, "prints the author's parent as a line, escaped, as the author's word"),
        ("src/Curia.Client/SubmissionBuilder.cs", 1, "writes the draft's parent into the envelope being signed"),
        ("src/Curia.Mcp/ForumTools.cs", 2, "curia_read's walk to a root and its cut line (plan ruling 5)"),
        ("src/Curia.Mcp/WriteTools.cs", 2, "two reads: the receipt prints the draft's parent (.Parent), and RevisedQuestionPointerAsync matches a verified revision's signed envelope by the property pattern `Parent: { Length: > 0 }` to name the question a revision revises in a refusal; it decides no placement because each step is accepted only when the parent's Forum-served superseded_by lists the digest recomputed from the revision (the author's word is never trusted alone), and it places no post, only a pointer"),
        ("src/Curia.Client.Cli/Screen/PassageHeader.cs", 1, "the screen's header prints the author's parent"),
    ];

    // The member access `.Parent`, and the property pattern `Parent:` (`is { Parent: { Length: > 0 } p }`), which reads the same member without the dot.
    [GeneratedRegex(@"\.Parent\b|\bParent\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex ParentRead();

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not find a 'src' directory above " + AppContext.BaseDirectory);
    }

    /// <summary>CHN55. Every file under <c>src/</c> that reads <c>.Parent</c> has a row, and none reads more than its row allows.</summary>
    [Fact]
    public void R9_48_CHN55_EveryReadOfAPostsParentIsAReasonedRow()
    {
        var root = RepositoryRoot();
        var scanned = 0;
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal)) continue;
            scanned++;
            var reads = ParentRead().Count(File.ReadAllText(file));
            if (reads > 0) found[relative] = reads;
        }

        // The scan must not be vacuous: it read source files, and it found the reads the rows describe.
        Assert.True(scanned > 100, $"the scan read {scanned} source files under src/");
        Assert.NotEmpty(found);

        var problems = new List<string>();
        foreach (var (file, count) in found)
        {
            var row = Rows.FirstOrDefault(r => r.File == file);
            if (row.File is null) problems.Add($"{file} reads .Parent {count} time(s) and has no row (a new reader of the author's word)");
            else if (count > row.Reads) problems.Add($"{file} reads .Parent {count} time(s); its row allows {row.Reads}");
        }

        foreach (var row in Rows.Where(r => !found.ContainsKey(r.File)))
            problems.Add($"{row.File} has a row and reads no .Parent (a stale row: {row.Reason})");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
