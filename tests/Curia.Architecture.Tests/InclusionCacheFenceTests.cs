using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Application.Projections;
using Curia.Canon.Acta;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Register item 403, the owner's ruling (2026-11-27): no state survives a request. <see cref="MerkleSubtreeCache"/> memoizes subtree hashes, which is state; it is allowed because it lives in
/// the <see cref="ActaLog"/> a request folds and is dropped with it. Three fences keep it there, in every shipped assembly and in Debug and Release (D16):
/// (ICF1) the cache is constructed only by <c>ActaLog</c>'s constructor; (ICF2) no static field of any shipped type holds a cache or an <c>ActaLog</c>, on its own or inside a generic
/// (<c>Lazy</c>, <c>Task</c>, a dictionary, a <c>Result</c>), so nothing holds one past the request that made it; (ICF3) no constructor of a shipped type, a dependency-injection
/// service's included, takes either as a parameter, so the container cannot be handed one to keep, and <c>ActaLog</c>'s own cache field is an instance field.
///
/// <para><b>What this does not hold.</b> A route that stores an <c>ActaLog</c> in a captured local of a long-lived closure is not seen (the IL of a closure's fields is not read); a cache of
/// another kind is not this type. The rule reads the types the shipped assemblies declare.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class InclusionCacheFenceTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
    private static readonly string CacheType = typeof(MerkleSubtreeCache).FullName!;
    private static readonly string LogType = typeof(ActaLog).FullName!;

    /// <summary>The reasoned exceptions to ICF3: each is a value made per call and returned, never a service the container builds.</summary>
    private static readonly (string Type, string Reason)[] PerCallValues =
    [
        ("Curia.Application.Credentials.KeyActs+WholeLifecycleRead", "an internal record that carries the operator's whole lifecycle read (the Acta it folded) back to the declaration, made per call; no route reads it (errata G87)"),
        ("Curia.Application.Credentials.KeyActs+LifecycleRead", "an internal record that carries one lifecycle read's result (the log it folded, the lifecycle, the skipped rows) back to its caller; made by ReadLifecycleAsync per call"),
    ];

    private static bool Holds(Type type) =>
        type == typeof(MerkleSubtreeCache) || type == typeof(ActaLog)
        || (type.HasElementType && Holds(type.GetElementType()!))
        || (type.IsGenericType && type.GetGenericArguments().Any(Holds));

    private static IEnumerable<Type> TypesOf(string path) =>
        Assembly.LoadFrom(path).GetTypes().Where(t => !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), inherit: false));

    [Fact]
    public void Item403_ICF1_TheCacheIsConstructedOnlyByTheConstructorOfActaLog()
    {
        var sites = Shipped.ShippedAssemblies().SelectMany(path => IlCallSites.CallSites(path)).ToArray();
        var constructions = sites.Where(s => s.CalleeType == CacheType && s.CalleeMember == ".ctor").ToArray();

        Assert.True(constructions.Length > 0, "no construction of the cache was found, so this fence would go quiet");
        var offenders = constructions.Where(s => s.CallerType != LogType || s.CallerMethod != ".ctor").Select(s => $"{s.CallerType}.{s.CallerMethod}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Assert.True(offenders.Length == 0, "Expected: only ActaLog..ctor constructs MerkleSubtreeCache  Actual: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Item403_ICF2_NoStaticFieldOfAShippedTypeHoldsTheCacheOrALog()
    {
        var paths = Shipped.ShippedAssemblies();
        Assert.Contains(paths, p => TypesOf(p).Any(t => t == typeof(ActaLog)));

        var offenders = paths
            .SelectMany(TypesOf)
            .SelectMany(t => t.GetFields(All).Where(f => f.IsStatic && Holds(f.FieldType)).Select(f => $"{t.FullName}.{f.Name}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0, "Expected: no static field holds a MerkleSubtreeCache or an ActaLog  Actual: " + string.Join("; ", offenders));
    }

    [Fact]
    public void Item403_ICF3_NoConstructorTakesTheCacheOrALogAndTheCacheFieldIsAnInstanceField()
    {
        var paths = Shipped.ShippedAssemblies();
        var offenders = paths
            .SelectMany(TypesOf)
            .SelectMany(t => t.GetConstructors(All).Where(c => !c.IsStatic && !PerCallValues.Any(v => v.Type == t.FullName && v.Reason.Length > 0) && c.GetParameters().Any(p => Holds(p.ParameterType))).Select(c => $"{t.FullName}..ctor"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(offenders.Length == 0, "Expected: no constructor takes a MerkleSubtreeCache or an ActaLog  Actual: " + string.Join("; ", offenders));

        var field = typeof(ActaLog).GetField("_subtrees", All);
        Assert.NotNull(field);
        Assert.False(field.IsStatic, "ActaLog's cache field is static");
        Assert.True(field.IsInitOnly, "ActaLog's cache field is reassigned after construction");
    }
}
