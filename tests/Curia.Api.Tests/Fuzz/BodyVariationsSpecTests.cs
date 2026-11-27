using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// Errata G84, amendment 1 ruling 5: the closed fuzz's variation set is "exactly spec section 4.10's table" and nothing compared the two (Task 0.7). The <c>Body</c> row of the table, read from the spec
/// (a document the code does not derive from), names the same variations as <see cref="Variations.Closed"/> holds whose id begins <c>body-</c>. Scope: the one row, so that the stage does not inherit the
/// other rows' drift; a whole-table comparison is register item 399 (j).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class BodyVariationsSpecTests
{
    private const string Spec = "docs/superpowers/specs/2026-10-06-one-part-at-a-time-design.md";

    [Fact]
    public void R14_10_BND12_TheSpecsBodyRowNamesTheBodyVariationsTheFuzzerHolds()
    {
        var text = File.ReadAllText(Path.Combine(PublishedEdge.RepoRoot(), Spec));
        var rows = BodyRow().Matches(text);
        Assert.True(rows.Count == 1, $"{Spec} holds {rows.Count} rows whose class is Body, expected one");
        var inSpec = Names().Matches(rows[0].Groups[1].Value).Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        var inCode = Variations.Closed.Select(v => v.Id).Where(id => id.StartsWith("body-", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        Assert.True(inSpec.SequenceEqual(inCode), $"the spec's Body row names [{string.Join(", ", inSpec)}], Variations.Closed holds [{string.Join(", ", inCode)}]");
    }

    [GeneratedRegex(@"^\| Body \|(.+)\|\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex BodyRow();

    [GeneratedRegex(@"`(body-[a-z-]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Names();
}
