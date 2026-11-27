using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R11.112 (errata G85): the guard is part of the origin's resolution, and nothing else. DEV7: <c>Curia.Api</c> reads the origin from configuration at exactly one call site and calls
/// <c>HostingGuard.Check</c> at exactly one, in the same method, so nothing can obtain a <c>PublicOrigin</c> without passing the guard and a new composition path cannot skip it. <c>IlCallSites</c> returns the
/// calls of a body and not their order, so the guard's place before every store read is DEV5's (Api.Tests), not this fence's. DEV8: the guard reads no configuration and no process variable, so it has no
/// setting that lifts it and reads the host's own resolution of its environment (the plan's ruling 3 and amendment 1 ruling 4). Held in both configurations by the CI step that runs this assembly in Debug and Release (D16).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class HostingGuardFenceTests
{
    private const string Origin = "Curia.Api.PublicOrigin";
    private const string Guard = "Curia.Api.HostingGuard";

    private static string ApiAssembly() =>
        Shipped.ShippedAssemblies().Single(p => string.Equals(Path.GetFileName(p), "Curia.Api.dll", StringComparison.Ordinal));

    [Fact]
    public void R11_112_DEV7_TheOneRegistrationOfThePublicOriginCallsTheGuard()
    {
        var sites = IlCallSites.CallSites(ApiAssembly());
        var reads = sites.Where(s => s.CalleeType == Origin && s.CalleeMember == "Read").ToArray();
        var checks = sites.Where(s => s.CalleeType == Guard && s.CalleeMember == "Check").ToArray();
        Assert.True(reads.Length == 1, $"Curia.Api reads the origin at {reads.Length} call sites, expected the one registration: {string.Join("; ", reads.Select(r => r.CallerType + "." + r.CallerMethod))}");
        Assert.True(checks.Length == 1, $"Curia.Api calls {Guard}.Check at {checks.Length} call sites, expected exactly one: the guard is the origin's resolution and a second or absent call is a path that skips it");
        Assert.Equal((reads[0].CallerType, reads[0].CallerMethod), (checks[0].CallerType, checks[0].CallerMethod));
    }

    [Fact]
    public void R11_112_DEV8_TheGuardReadsNoConfigurationAndNoProcessVariable()
    {
        var sites = IlCallSites.CallSites(ApiAssembly()).Where(s => s.CallerType == Guard || s.CallerType.StartsWith(Guard + "/", StringComparison.Ordinal) || s.CallerType.StartsWith(Guard + "+", StringComparison.Ordinal)).ToArray();
        Assert.True(sites.Length > 0, $"{Guard} has no calls at all: the type does not exist or does nothing");
        var offenders = sites
            .Where(s => s.CalleeType.StartsWith("Microsoft.Extensions.Configuration", StringComparison.Ordinal)
                || (s.CalleeType == "System.Environment" && s.CalleeMember.Contains("Variable", StringComparison.Ordinal)))
            .Select(s => $"{s.CallerMethod} calls {s.CalleeType}.{s.CalleeMember}")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.True(offenders.Length == 0, "the guard reads a setting or a variable, which is a door that lifts it (R11.112): " + string.Join("; ", offenders));
    }
}
