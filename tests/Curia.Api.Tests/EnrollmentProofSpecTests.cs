using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// EPP21 (errata G79): the refusal types R4.46 publishes are the types the code defines, read from the errata at test time and compared with <c>Curia.AuthN.EnrollmentProofErrors</c>. The errata names
/// <c>curia/enroll/proof-required</c> in full and the other six by their last segment (<c>proof-malformed</c> ...); each is read in context, from the paragraph that begins <c>**R4.46**</c>. An eighth type
/// added to the code without the errata, or one the errata renames without the code, fails by name (FSPEC).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class EnrollmentProofSpecTests
{
    [GeneratedRegex(@"`(?:curia/enroll/)?(proof-[a-z-]+)`", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 5000)]
    private static partial Regex Slug();

    private static string R446()
    {
        const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata))) dir = dir.Parent;
        Assert.True(dir is not null, $"{Errata} not found");
        var lines = File.ReadAllLines(Path.Combine(dir!.FullName, Errata));
        var start = Array.FindIndex(lines, l => l.StartsWith("**R4.46** An enrollment request SHALL carry", StringComparison.Ordinal));
        Assert.True(start >= 0, "the errata holds no paragraph that begins **R4.46** An enrollment request SHALL carry");
        return lines[start];
    }

    [Fact]
    public void R4_46_EPP21_TheErrataNamesTheSevenTypesTheCodeDefines()
    {
        var published = Slug().Matches(R446()).Select(m => "curia/enroll/" + m.Groups[1].Value).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(7, published.Length);

        var errors = typeof(Curia.AuthN.EnrollmentProofValidator).Assembly.GetType("Curia.AuthN.EnrollmentProofErrors");
        Assert.True(errors is not null, "Curia.AuthN.EnrollmentProofErrors does not exist: the code defines none of the errata's seven types");
        var defined = errors!.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.IsLiteral && f.Name.EndsWith("Type", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(published, defined);
    }
}
