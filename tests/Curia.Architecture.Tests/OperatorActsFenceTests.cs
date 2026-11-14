using System.Diagnostics.CodeAnalysis;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// PR N2 (errata G70, R4.43, R6.67), OAS12 and OAS17 (the parts that are green on the tree are said so). OAS12: no production code reads an owner standing or a moderation record from a log nobody verified. The
/// scan counts the calls to <c>AgentStandingProjector.Fold</c>, <c>FlagProjector.Fold</c> and <c>FlagProjector.Extend</c> in every shipped assembly's IL, so the real call list is the red text (the plan's "22" was a grep
/// and counted definitions); the allow-list is empty but for reasoned rows, and each row of it names its reason. To be run in Debug and in Release (D16: the IL differs between them).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OperatorActsFenceTests
{
    private static readonly (string Callee, string Member)[] RawFolds =
    [
        ("Curia.Application.Projections.AgentStandingProjector", "Fold"),
        ("Curia.Application.Projections.FlagProjector", "Fold"),
        ("Curia.Application.Projections.FlagProjector", "Extend"),
    ];

    /// <summary>Callers that may fold a raw log, each with its reason. The verifier's own honoured view is the one reader of the raw fold: it feeds the fold only rows the procedure decided honoured.</summary>
    private static readonly (string Type, string Reason)[] Allowed =
    [
        ("Curia.Application.Credentials.VerifiedLog", "HonouredEvents: the raw fold is fed only the rows the act procedure honoured (R6.67)"),
        ("Curia.Application.Projections.AgentStandingProjector", "the projector's own overloads call each other"),
        ("Curia.Application.Projections.FlagProjector", "the projector's own overloads call each other"),
    ];

    private static string OwningType(string callerType)
    {
        var plus = callerType.IndexOf("+<", StringComparison.Ordinal);
        return plus < 0 ? callerType : callerType[..plus];
    }

    /// <summary>The scan finds the fold's callers on the tree, so an empty list can never be a quiet one.</summary>
    [Fact]
    public void OAS12_TheScanFindsTheCallersOfTheRawFolds()
    {
        var sites = Shipped.ShippedAssemblies().SelectMany(p => IlCallSites.CallSites(p)).Where(c => RawFolds.Any(f => f.Callee == c.CalleeType && f.Member == c.CalleeMember)).ToArray();
        Assert.True(sites.Length > 0, "no call of a raw fold is found: the fence would be vacuous");
    }

    [Fact]
    public void OAS12_NoProductionCodeReadsStandingOrModerationFromAnUnfilteredLog()
    {
        var calls = Shipped.ShippedAssemblies()
            .SelectMany(p => IlCallSites.CallSites(p))
            .Where(c => RawFolds.Any(f => f.Callee == c.CalleeType && f.Member == c.CalleeMember))
            .Where(c => !Allowed.Any(a => a.Type == OwningType(c.CallerType)))
            .GroupBy(c => $"{OwningType(c.CallerType).Split('.')[^1]}.{c.CallerMethod.Split('>')[0].TrimStart('<')}")
            .Select(g => $"{g.Key} ({g.Count()})")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.All(Allowed, a => Assert.False(string.IsNullOrWhiteSpace(a.Reason), $"{a.Type} has no reason"));
        Assert.True(calls.Length == 0, $"Expected: no caller outside the allow-list  Actual: {calls.Length} caller(s): {string.Join(", ", calls)}");
    }

    /// <summary>
    /// OAS17, the string-scan clause: no assembly but <c>curia-operator</c> contains the name of the operator-acts key's variable. GREEN ON THE TREE AND VACUOUS UNTIL THE VARIABLE EXISTS: no assembly
    /// holds the string today, so the test cannot fail; it is left for FOAS22, which adds the string to a Forum assembly and must show it red. The clause that is red today (the implementers of
    /// <c>IOperatorActSigner</c>) is the pending OAS17 file.
    /// </summary>
    [Fact]
    public void OAS17_NoForumAssemblyHoldsTheOperatorActsKeysVariableName()
    {
        var holders = Shipped.ShippedAssemblies()
            .Where(p => Path.GetFileNameWithoutExtension(p) != "curia-operator")
            .Where(p => File.ReadAllBytes(p).AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes("CURIA_OPERATOR_ACTS_SIGNING_KEY_PEM")) >= 0)
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToArray();
        Assert.True(holders.Length == 0, $"Expected: no Forum assembly names the operator-acts key's variable  Actual: {string.Join(", ", holders)}");
    }

    /// <summary>
    /// OAS17, the clause that is red today: the implementers of <c>IOperatorActSigner</c>, across every shipped assembly, are exactly <c>Curia.OperatorTool.OperatorActsSigningKey</c>. The interface is found by name
    /// through reflection so that its absence fails by assertion (<c>implementers: []</c>) and not by compilation, which would stop every other test in this assembly.
    /// </summary>
    [Fact]
    public void OAS17_TheOnlyImplementerOfTheOperatorActSignerIsTheOperatorsKey()
    {
        var implementers = new List<string>();
        foreach (var path in Shipped.ShippedAssemblies())
        {
            var assembly = System.Reflection.Assembly.LoadFrom(path);
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (System.Reflection.ReflectionTypeLoadException e) { types = [.. e.Types.Where(t => t is not null)!]; }
            implementers.AddRange(types.Where(t => !t.IsInterface && t.GetInterfaces().Any(i => i.Name == "IOperatorActSigner")).Select(t => t.FullName!));
        }

        WholeText.Same("implementers: [Curia.OperatorTool.OperatorActsSigningKey]", $"implementers: [{string.Join(", ", implementers.Order(StringComparer.Ordinal))}]");
    }
}
