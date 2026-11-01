using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// CPF28 (errata G54, decision 3): the obligation of a check is created at one site, so the confirm of a faulted append (D104, PR 3) extends the same <c>catch</c> and
/// cannot add a second. A scan of the source of <c>Curia.Api</c> holds that <c>IndexSweep.Owe</c> has exactly one call, and that it is inside the <c>catch</c> of the
/// <c>try</c> that wraps <c>PersistAsync</c> in <c>PersistSubmissionAsync</c>.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class OweCallSiteTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\.Owe\(", RegexOptions.CultureInvariant)]
    private static partial Regex OweCall();

    [GeneratedRegex(@"\btry\s*\{[^{}]*?PersistAsync\([^;]*;[\s\S]*?\}\s*catch\s*\(\s*Exception[^)]*\)\s*\{(?<body>[\s\S]*?)\bthrow\s*;", RegexOptions.CultureInvariant)]
    private static partial Regex PersistTryCatch();

    /// <summary>CPF28. Exactly one call to <c>Owe</c> in <c>src/Curia.Api</c>, inside the <c>catch (Exception)</c> of the <c>try</c> around <c>PersistAsync</c>, followed by a rethrow.</summary>
    [Fact]
    public void R8_83_CPF28_TheObligationIsCreatedAtOneSiteInsideTheCatchAroundPersist()
    {
        var api = Path.Combine(Root(), "src", "Curia.Api");
        var files = Directory.EnumerateFiles(api, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        var calls = files.Select(f => (File: Path.GetFileName(f), Count: OweCall().Count(File.ReadAllText(f)))).Where(x => x.Count > 0).ToArray();
        var total = calls.Sum(c => c.Count);

        var endpoints = File.ReadAllText(Path.Combine(api, "ForumEndpoints.cs"));
        var persist = endpoints.IndexOf("PersistSubmissionAsync", StringComparison.Ordinal);
        var inside = persist >= 0 && PersistTryCatch().Match(endpoints, persist) is { Success: true } m && OweCall().IsMatch(m.Groups["body"].Value);

        var expected = "1 call site of IndexSweep.Owe in src/Curia.Api, inside the catch around PersistAsync in PersistSubmissionAsync, followed by a rethrow";
        var actual = $"{total} call site{(total == 1 ? string.Empty : "s")} of IndexSweep.Owe in src/Curia.Api{(total is 0 or 1 ? string.Empty : " (" + string.Join(", ", calls.Select(c => $"{c.File} x{c.Count}")) + ")")}" +
            (total == 1 && inside ? ", inside the catch around PersistAsync in PersistSubmissionAsync, followed by a rethrow" : total == 0 ? string.Empty : ", " + (inside ? "inside" : "not inside") + " the catch around PersistAsync in PersistSubmissionAsync");
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }
}
