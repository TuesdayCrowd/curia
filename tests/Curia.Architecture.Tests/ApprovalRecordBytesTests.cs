using System.Diagnostics.CodeAnalysis;
using System.Text;
using Curia.Infrastructure.Lifecycle;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// HSU34 (PR H, register D121 context): the shipped lifecycle approval record is exactly <c>{"version":1,"approved":[]}</c> and one line feed, in the
/// source file and in the resource the assembly embeds. <c>LifecycleGateFenceTests.R4_38_TheApprovedSetIsEmpty</c> parses the record and compares no bytes,
/// so a record that approves nothing and differs from the reviewed one passes it; this does not. The expectation is a literal here, a different artifact
/// from both the file and the resource.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ApprovalRecordBytesTests
{
    private const string Shipped = "{\"version\":1,\"approved\":[]}\n";
    private const string ResourceName = "Curia.Infrastructure.Lifecycle.lifecycle-approval.json";

    [Fact]
    public void R4_38_HSU34_TheShippedApprovalRecordIsTwentyEightBytes()
    {
        var expected = Encoding.UTF8.GetBytes(Shipped);
        Assert.Equal(28, expected.Length);

        var path = Path.Combine(FindRepoRoot(), "src", "Curia.Infrastructure", "Lifecycle", "lifecycle-approval.json");
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    [Fact]
    public void R4_38_HSU34_TheEmbeddedResourceIsTheSameBytesAndApprovesNothing()
    {
        using var stream = typeof(LifecycleApprovalRecord).Assembly.GetManifestResourceStream(ResourceName);
        Assert.NotNull(stream);
        using var copy = new MemoryStream();
        stream!.CopyTo(copy);
        Assert.Equal(Encoding.UTF8.GetBytes(Shipped), copy.ToArray());

        var record = LifecycleApprovalRecord.Embedded();
        Assert.True(record.IsOk);
        Assert.Empty(record.Match(r => r.Approved, _ => default));
    }

    /// <summary>Mirrors <c>Shipped.FindRepoRoot</c>: each file in this suite is self-contained.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not find repo root (a 'src' directory) above " + AppContext.BaseDirectory);
    }
}
