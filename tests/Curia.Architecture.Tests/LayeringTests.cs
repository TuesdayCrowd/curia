using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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

    [Fact]
    public void CS6_CanonReferencesNoPackage()
    {
        var offenders = Canon.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !n.StartsWith("System", StringComparison.Ordinal)
                     && n != "netstandard"
                     && !n.StartsWith("Curia.", StringComparison.Ordinal))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Curia.Canon must reference no package (CS-6). Found: " + string.Join(", ", offenders));
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
}
