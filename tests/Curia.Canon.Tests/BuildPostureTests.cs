using System.Reflection;
using Curia.Canon.Canonical;
using Xunit;

namespace Curia.Canon.Tests;

public sealed class BuildPostureTests
{
    [Fact]
    public void CanonAssemblyReferencesNoThirdPartyPackages()
    {
        var canon = typeof(CanonicalJson).Assembly;
        var offenders = canon.GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !n.StartsWith("System", StringComparison.Ordinal)
                     && !n.Equals("netstandard", StringComparison.Ordinal)
                     && !n.StartsWith("Curia.", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(offenders);
    }
}
