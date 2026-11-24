using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// FNC1 (errata G76 plan, a control): CHN55's rows (<see cref="ParentReadFenceTests"/>) hold no slack, so "a fourth read anywhere fails" is true of the rows as they stand and of the rows after the reasoned
/// edits Task 3 makes (<c>AcceptedAnswerIndex.cs</c> 1 to 2, <c>ForumEndpoints.cs</c> 4 to 3). The fence fails where a file reads <i>more</i> than its row allows; a row that allows more than the file reads would
/// let a new read pass unseen, so each row is held to the exact count the file has, and the scan itself is held sensitive: one more <c>.Parent</c> in a file's text is one more read. The rows and the scan are read
/// from the fence by reflection, so the rows Task 3 edits are the rows this control follows, with no row copied here. Green on the unmodified tree.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ParentReadFenceSlackTests
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    private static (string File, int Reads, string Reason)[] Rows() =>
        (((string, int, string)[])typeof(ParentReadFenceTests).GetField("Rows", Private)!.GetValue(null)!)
            .Select(r => (r.Item1, r.Item2, r.Item3)).ToArray();

    private static Regex Scan() => (Regex)typeof(ParentReadFenceTests).GetMethod("ParentRead", Private)!.Invoke(null, null)!;

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no src directory above " + AppContext.BaseDirectory);
    }

    /// <summary>FNC1, the rows: each row's count is the count of reads the file has. A row with room to spare is a fourth read allowed.</summary>
    [Fact]
    public void R9_48_FNC1_Control_EveryRowAllowsExactlyTheReadsItsFileHas()
    {
        var root = Root();
        var scan = Scan();
        var rows = Rows();
        Assert.NotEmpty(rows);

        var slack = new List<string>();
        foreach (var (file, reads, _) in rows)
        {
            var found = scan.Count(File.ReadAllText(Path.Combine(root, file)));
            if (found != reads) slack.Add($"{file}: the row allows {reads}, the file reads {found}");
        }

        Assert.True(slack.Count == 0, string.Join("; ", slack));
    }

    /// <summary>FNC1, the scan's sensitivity: a file's text with one more member access, one more property pattern, or one more of both, reads one, one and two more; <c>Parents</c> and <c>ParentId</c> read none.</summary>
    [Fact]
    public void R9_48_FNC1_Control_OneMoreReadIsOneMoreCount()
    {
        var scan = Scan();
        var text = File.ReadAllText(Path.Combine(Root(), Rows().First().File));
        var baseline = scan.Count(text);

        Assert.Equal(baseline + 1, scan.Count(text + "\nvar x = post.Parent;\n"));
        Assert.Equal(baseline + 1, scan.Count(text + "\nif (p is { Parent: { Length: > 0 } }) { }\n"));
        Assert.Equal(baseline + 2, scan.Count(text + "\nvar a = p.Parent; var b = q.Parent;\n"));
        Assert.Equal(baseline, scan.Count(text + "\nvar c = p.Parents; var d = p.ParentId;\n"));
    }
}
