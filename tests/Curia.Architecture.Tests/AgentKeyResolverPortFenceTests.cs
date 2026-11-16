using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.AuthN.Ports;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// VBR7 (register item 282, errata G72, R5.25), as an IL fence, to be run in Debug and in Release (D16: the IL differs between them). The token endpoint verifies a client assertion under the key the
/// Registrar's key store holds and only then reads the log, so the validator's port, <see cref="IAgentKeyResolver"/>, must be answered by the store and never by the log: every type in the shipped assemblies
/// that implements it calls no member of <c>LogBoundKeys</c> but <c>ResolveStoredAsync</c> and no member of <c>IEventReader</c> or <c>IEventStore</c>. The implementers are found by the port and not by a
/// name, so a rename cannot silence the fence, and the allowlist (not a denylist of names) catches a log-reading member added to <c>LogBoundKeys</c> later. The endpoint's own order, which an IL scan
/// cannot read (<c>IlCallSites</c> returns the calls of a body and not their order), is carried by the probes of the token endpoint, not by this fence.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AgentKeyResolverPortFenceTests
{
    private const string LogBoundKeysType = "Curia.Application.Credentials.LogBoundKeys";

    private static readonly string[] LogTypes =
    [
        "Curia.Application.Ports.IEventReader", "Curia.Application.Ports.IEventStore", "Curia.Application.Ports.EventReaderExtensions",
    ];

    /// <summary>The path of each shipped assembly with the full names of the types in it that implement the port.</summary>
    private static (string Path, string Type)[] Implementers() =>
        [.. Shipped.ShippedAssemblies().SelectMany(path => Assembly.LoadFrom(path).GetTypes()
            .Where(t => t.IsClass && typeof(IAgentKeyResolver).IsAssignableFrom(t))
            .Select(t => (path, t.FullName!)))];

    private static string OwningType(string callerType)
    {
        var plus = callerType.IndexOf("+<", StringComparison.Ordinal);
        return plus < 0 ? callerType : callerType[..plus];
    }

    [Fact]
    public void VBR7_TheImplementersOfTheKeyPortAreFoundByThePort()
    {
        var found = Implementers();
        var names = string.Join(", ", found.Select(f => f.Type).Order(StringComparer.Ordinal));

        Assert.True(found.Length > 0, "no type in the shipped assemblies implements IAgentKeyResolver, so the fence below would go quiet");
        Assert.True(found.Any(f => f.Type.EndsWith(".PostgresAgentKeyStore", StringComparison.Ordinal)), $"PostgresAgentKeyStore is not among the implementers [{names}]");
        var api = found.Where(f => f.Type.StartsWith("Curia.Api.", StringComparison.Ordinal)).ToArray();
        Assert.True(api.Length == 1, $"Expected: exactly one implementer in Curia.Api (the adapter)  Actual: {api.Length}, among [{names}]");
    }

    [Fact]
    public void VBR7_NoImplementerOfTheKeyPortReadsTheLogOrAsksLogBoundKeysForAnythingButTheStoresHalf()
    {
        var found = Implementers();
        Assert.True(found.Length > 0, "no implementer was found, so this fence would go quiet");
        var names = string.Join(", ", found.Select(f => f.Type).Order(StringComparer.Ordinal));

        var violations = new List<string>();
        foreach (var path in found.Select(f => f.Path).Distinct(StringComparer.Ordinal))
        {
            var types = found.Where(f => f.Path == path).Select(f => f.Type).ToHashSet(StringComparer.Ordinal);
            foreach (var site in IlCallSites.CallSites(path).Where(c => types.Contains(OwningType(c.CallerType))))
            {
                var owner = OwningType(site.CallerType);
                if (site.CalleeType == LogBoundKeysType && site.CalleeMember != "ResolveStoredAsync")
                    violations.Add($"{owner} calls LogBoundKeys.{site.CalleeMember}");
                if (LogTypes.Contains(site.CalleeType, StringComparer.Ordinal))
                    violations.Add($"{owner} calls {site.CalleeType}.{site.CalleeMember}");
            }
        }

        Assert.True(
            violations.Count == 0,
            $"Expected: the implementers of IAgentKeyResolver [{names}] read nothing of the log and call only LogBoundKeys.ResolveStoredAsync  Actual: {string.Join("; ", violations.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))}");
    }
}
