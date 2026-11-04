using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R11.84 (errata G60, ruling 21), RNC26's fence: the client's two credential-refusal types are told apart by one predicate, <c>ClientErrors.IsCredentialMaterial</c>, and
/// their literals are private to <c>ClientErrors.cs</c>. A name-based <b>floor</b> (trap 118): it stops a consumer that compares a type literal and so reads the second type as a
/// generic build error, and it is not the claim. The claim is RNC26's table, which runs the four category sets through both clients. Its positive leg proves the scan reads a file
/// that holds the literal.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class CredentialErrorTypeFenceTests
{
    private const string Literal = "curia/client/credential-material";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>RNC26's fence: no production file outside <c>ClientErrors.cs</c> holds either type literal, and <c>ClientErrors.cs</c> holds both.</summary>
    [Fact]
    public void R11_84_RNC26_NoProductionFileOutsideClientErrorsHoldsEitherCredentialTypeLiteral()
    {
        var src = Path.Combine(RepoRoot(), "src");
        var files = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        var holders = files.Where(f => File.ReadAllText(f).Contains(Literal, StringComparison.Ordinal)).Select(f => Path.GetRelativePath(src, f)).Order(StringComparer.Ordinal).ToArray();

        var allowed = Path.Combine("Curia.Client", "ClientErrors.cs");
        Assert.Equal([allowed], holders.Where(h => h == allowed).ToArray());
        Assert.True(
            holders.All(h => h == allowed),
            $"expected the literal in {allowed} alone, found it in {string.Join(", ", holders.Where(h => h != allowed))}");

        var own = File.ReadAllText(Path.Combine(src, allowed));
        Assert.True(own.Split(Literal).Length - 1 >= 2, "ClientErrors.cs no longer holds both type literals; the fence reads nothing");
    }
}
