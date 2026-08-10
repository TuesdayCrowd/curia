using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Makes the hexagon's layering a build-time fact rather than a diagram. CS-6: Canon is
/// the pure domain core and references no package, not even its own crypto adapter's
/// dependency. CS-7: the adapter (Curia.Canon.Sodium) may depend on Canon, but Canon may
/// never depend back on the adapter -- the dependency direction points one way, inward.
///
/// Increment 1 has no Application or Infrastructure project, so CS-7's full dependency
/// matrix (domain not depending on infrastructure, application not depending on adapters
/// directly, etc.) cannot be asserted yet -- there is nothing on disk to assert it against.
/// This suite asserts what exists today: Canon depends on nothing but the BCL and its own
/// sibling Curia.Domain.Primitives, and Canon does not depend on its own adapter.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs (CS-6, CS-7) they enforce verbatim, so a " +
        "reader can trace a failing test straight to the spec obligation without a second naming " +
        "scheme to translate through (mirrors CanonProperties' P1-P5 precedent).")]
public sealed class LayeringTests
{
    private static Assembly Canon => typeof(Curia.Canon.Canonical.CanonicalJson).Assembly;
    private static Assembly Sodium => typeof(Curia.Canon.Sodium.Ed25519Adapter).Assembly;

    /// <summary>
    /// CS-6: "Curia.Canon SHALL reference no package." Reads Curia.Canon.csproj's own
    /// declared &lt;PackageReference&gt; items directly, rather than reflecting over the
    /// built assembly's AssemblyRef table the way CS6_OnlySodiumReferencesNativeCrypto and
    /// CS7_CanonDoesNotDependOnSodium below still do (via Assembly.GetReferencedAssemblies
    /// and NetArchTest, both of which walk compiled IL, not project files).
    ///
    /// Those two checks are the right tool for their own questions ("does compiled Canon
    /// code actually use NSec" / "does compiled Canon code actually depend on Sodium
    /// types"), but an assembly-reflection approach is the wrong tool for THIS
    /// requirement: the C# compiler only emits an AssemblyRef for a package whose types
    /// are actually referenced somewhere in the IL. A &lt;PackageReference&gt; added to
    /// Curia.Canon.csproj but never used by any code -- exactly the "sits in the project
    /// file indefinitely, picked up by SBOM tooling and the supply-chain surface, while
    /// this test stays green" failure mode CS-6 exists to catch -- leaves no AssemblyRef
    /// behind at all, so the original Assembly.GetReferencedAssemblies()-based version of
    /// this test could never detect it (confirmed: see the Task 10 fix report's
    /// falsification evidence).
    ///
    /// The project file is also the more direct, authoritative source for this specific
    /// requirement: "Curia.Canon references no package" is a statement about what
    /// Curia.Canon.csproj declares, and that file is where a violation would first be
    /// introduced. packages.lock.json would work too (it is committed per project and
    /// distinguishes direct from transitive dependencies), but it is a restore-generated
    /// artifact one step removed from the csproj, so reading the csproj directly avoids
    /// depending on restore having already run to reflect a just-added reference.
    /// </summary>
    [Fact]
    public void CS6_CanonReferencesNoPackage()
    {
        var csprojPath = FindProjectFile("Curia.Canon");
        var offenders = XDocument.Load(csprojPath)
            .Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? "(unnamed)")
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"Curia.Canon must reference no package (CS-6). Found in {csprojPath}: " + string.Join(", ", offenders));
    }

    [Fact]
    public void CS6_OnlySodiumReferencesNativeCrypto()
    {
        Assert.DoesNotContain(Canon.GetReferencedAssemblies(), a => a.Name == "NSec.Cryptography");
        Assert.Contains(Sodium.GetReferencedAssemblies(), a => a.Name == "NSec.Cryptography");
    }

    [Fact]
    public void CS7_CanonDoesNotDependOnSodium()
    {
        var result = Types.InAssembly(Canon)
            .Should().NotHaveDependencyOn("Curia.Canon.Sodium")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Canon must not depend on its adapter (CS-7). Offenders: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the repo root (the first
    /// ancestor containing a `src` directory), mirroring the same path-discovery idiom
    /// Curia.Canon.Tests.Vectors.VectorLoader.FindRoot already uses in this solution to
    /// locate `conformance/` without a hardcoded absolute or excessively relative path.
    /// </summary>
    private static string FindProjectFile(string projectName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException("Could not find repo root (a 'src' directory) above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "src", projectName, projectName + ".csproj");
    }
}
