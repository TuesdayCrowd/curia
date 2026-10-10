using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R11.34 (errata G18): <i>a caller's string reaches a port or a host-called use case only as a
/// boundary type</i>, held as a build-failing fence rather than as review (spec section 4.8).
///
/// <para><b>The scope is derived, in four steps.</b> (1) Every public interface declared in
/// <c>Curia.Application</c> and <c>Curia.AuthN</c>, and every method on it. (2) Every method and
/// constructor that <c>Curia.Api</c> or <c>curia-operator</c> references in those two assemblies,
/// read from IL (<see cref="IlCallSites.MemberReferencesInto"/>). (3) For each parameter, every type
/// declared in a <c>Curia.*</c> assembly that its type names, seen through an array, nullable,
/// by-reference or a generic instantiation's type arguments: that type's public constructors'
/// parameters, recursively, with a visited set. A parameter whose type is a type parameter fails the
/// fact. (4) A parameter is string-like if its type is <see cref="string"/>,
/// <c>char[]</c>, <c>ReadOnlySpan&lt;char&gt;</c>, <c>ReadOnlyMemory&lt;char&gt;</c>,
/// <c>Memory&lt;char&gt;</c> or <c>StringValues</c>, or an array, nullable, by-reference or generic
/// instantiation any of whose element or type arguments is string-like, recursively.</para>
///
/// <para><b>What a row is.</b> Each string-like parameter in scope needs a row in
/// <see cref="PortStringAllowlist"/> naming why it is not a caller's identifier, by section 4.8's
/// classification. A parameter the rule calls retype is retyped, never given a row.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PortStringFenceTests
{
    private const BindingFlags Members =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>
    /// Spec section 4.8's sentinels, with one substitution: the spec names
    /// <c>EnrollAgent.RecordAsync</c>, but no host calls it (since 1dbe0ff the host calls
    /// <c>EnrollIdentity.EnrollAsync</c>, which calls it), so step 2 cannot reach it and step 3 walks
    /// only constructors. The enrollment use case a host calls stands in for it.
    /// </summary>
    private static readonly string[] Sentinels =
    [
        "Curia.Application.Moderation.RaiseFlag.RecordAsync",
        "Curia.Application.Ingest.IIngestPipeline.VerifyAsync",
        "Curia.AuthN.Ports.IReplayCache.TryInsertAsync",
        "Curia.Application.Credentials.EnrollIdentity.EnrollAsync",
    ];

    /// <summary>Parameters typed <c>Result&lt;AgentId&gt;</c>, which must never be found string-like.</summary>
    private static readonly (string Member, string Parameter)[] ParseOutcomes =
    [
        ("Curia.AuthN.ClientAssertionValidationContext..ctor", "ExpectedSubject"),
        ("Curia.Application.Credentials.AttestOwner.RecordAsync", "agentId"),
    ];

    /// <summary>
    /// Constructors step 3 reaches only as <c>PostEnvelope</c>'s <c>ImmutableArray&lt;&gt;</c> type
    /// arguments, which must be in scope.
    /// </summary>
    private static readonly string[] ReachedThroughATypeArgument =
    [
        "Curia.Domain.Content.Reference..ctor",
        "Curia.Domain.Content.CodeBlock..ctor",
    ];

    /// <summary>Step 2's hosts: <c>Curia.Api</c>, and <c>Curia.Operator</c>, whose assembly is <c>curia-operator</c>.</summary>
    private static readonly string[] Hosts = ["Curia.Api.dll", "curia-operator.dll"];

    private static Assembly Application => typeof(Curia.Application.Moderation.RaiseFlag).Assembly;

    private static Assembly AuthN => typeof(Curia.AuthN.AuthNErrors).Assembly;

    [Fact]
    public void R11_34_NoPortOrHostCalledUseCaseTakesACallersStringWithoutAReason()
    {
        var scope = Scope(out var portCount, out var hostCount);

        Assert.True(portCount >= 10, $"only {portCount} port interfaces were scanned; the scope is looking in the wrong place");
        Assert.True(hostCount >= 20, $"only {hostCount} host-referenced members were found; the IL read is looking in the wrong place");
        foreach (var sentinel in Sentinels)
            Assert.True(scope.Members.Contains(sentinel), $"{sentinel} is not in the fence's scope; a defect in how the scope is derived");
        foreach (var m in ReachedThroughATypeArgument)
            Assert.True(scope.Members.Contains(m), $"{m} is reachable only through a generic type argument and is not in the fence's scope; step 3 is not walking type arguments");

        // A Result<T> carries its value in private state, which step 3 must not walk: these two
        // parameters are parse outcomes (B2's ExpectedSubject, B3's attest-owner agent), and if either
        // appears here the fence is walking into Result<T>, a fence bug to fix rather than a row to add.
        foreach (var (member, parameter) in ParseOutcomes)
        {
            Assert.DoesNotContain(scope.StringParameters, p => p.Member == member && p.Parameter == parameter);
            Assert.DoesNotContain(PortStringAllowlist.Rows, r => r.Member == member && r.Parameter == parameter);
        }

        var failures = new List<string>();
        var rows = PortStringAllowlist.Rows;

        foreach (var (member, parameter, reason) in rows.Where(r => string.IsNullOrWhiteSpace(r.Reason)))
            failures.Add($"row {member}({parameter}) has an empty reason");

        foreach (var (member, _, _) in rows.Where(r => r.Parameter == "*"))
        {
            var typeName = member[..member.LastIndexOf('.')];
            if (FindType(typeName) is not { } type || !IsErrorFactory(type))
                failures.Add($"row {member}(*) names {typeName}, which is not a static class whose every public method returns Error");
        }

        foreach (var (member, parameter, type) in scope.OpenParameters)
            failures.Add($"{member}({parameter}): {type} is a type parameter; the fence cannot see what a host instantiates it with");

        foreach (var (member, parameter, type) in scope.StringParameters)
        {
            if (!rows.Any(r => Covers(r, member, parameter)))
                failures.Add($"{member}({parameter}): {type} has no row");
        }

        foreach (var row in rows)
        {
            if (!scope.StringParameters.Any(p => Covers(row, p.Member, p.Parameter)))
                failures.Add($"row {row.Member}({row.Parameter}) names no string-like parameter in scope (stale)");
        }

        Assert.True(
            failures.Count == 0,
            "R11.34: a caller's string reaches a port or a host-called use case without a reason:"
            + Environment.NewLine + string.Join(Environment.NewLine, failures.Order(StringComparer.Ordinal)));
    }

    private static bool Covers((string Member, string Parameter, string Reason) row, string member, string parameter) =>
        row.Parameter == "*"
            ? member.StartsWith(row.Member[..row.Member.LastIndexOf('.')] + ".", StringComparison.Ordinal)
            : row.Member == member && row.Parameter == parameter;

    private sealed record FenceScope(
        HashSet<string> Members,
        List<(string Member, string Parameter, string Type)> StringParameters,
        List<(string Member, string Parameter, string Type)> OpenParameters);

    private static FenceScope Scope(out int portCount, out int hostCount)
    {
        var methods = new List<MethodBase>();

        var ports = new[] { Application, AuthN }
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsInterface && t.IsVisible)
            .ToList();
        portCount = ports.Count;
        foreach (var port in ports)
            methods.AddRange(port.GetMethods(Members | BindingFlags.DeclaredOnly));

        var hostReferences = Hosts
            .SelectMany(file => IlCallSites.MemberReferencesInto(
                Path.Combine(AppContext.BaseDirectory, file), "Curia.Application", "Curia.AuthN"))
            .Distinct()
            .ToList();
        hostCount = hostReferences.Count;
        foreach (var (typeName, memberName) in hostReferences)
        {
            var type = FindType(typeName)
                ?? throw new InvalidOperationException($"a host references {typeName}, which neither Curia.Application nor Curia.AuthN declares");
            methods.AddRange(memberName == ".ctor"
                ? type.GetConstructors(Members)
                : type.GetMethods(Members).Where(m => m.Name == memberName));
        }

        var members = new HashSet<string>(StringComparer.Ordinal);
        var strings = new List<(string, string, string)>();
        var open = new List<(string, string, string)>();
        var visited = new HashSet<Type>();
        var seen = new HashSet<MethodBase>();
        var queue = new Queue<MethodBase>(methods);
        while (queue.TryDequeue(out var method))
        {
            if (!seen.Add(method))
                continue;
            var key = KeyOf(method);
            members.Add(key);

            foreach (var parameter in method.GetParameters())
            {
                if (IsStringLike(parameter.ParameterType))
                    strings.Add((key, parameter.Name ?? "", parameter.ParameterType.ToString()));

                if (IsOpen(parameter.ParameterType))
                    open.Add((key, parameter.Name ?? "", parameter.ParameterType.ToString()));

                foreach (var curia in CuriaTypesOf(parameter.ParameterType))
                {
                    if (visited.Add(curia))
                    {
                        foreach (var ctor in curia.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
                            queue.Enqueue(ctor);
                    }
                }
            }
        }

        return new FenceScope(members, [.. strings.Distinct()], [.. open.Distinct()]);
    }

    private static string KeyOf(MethodBase method) => method.DeclaringType!.FullName + "." + method.Name;

    private static Type? FindType(string fullName) =>
        Application.GetType(fullName) ?? AuthN.GetType(fullName);

    /// <summary>
    /// Step 3: every type a <c>Curia.*</c> assembly declares that the parameter's type names: the type
    /// itself, or seen through an array, a nullable, a by-reference or a generic instantiation's type
    /// arguments, recursively; the same positions step 4 looks through.
    /// </summary>
    private static IEnumerable<Type> CuriaTypesOf(Type type)
    {
        if (type.HasElementType)
        {
            foreach (var t in CuriaTypesOf(type.GetElementType()!))
                yield return t;
            yield break;
        }
        if (Nullable.GetUnderlyingType(type) is { } underlying)
        {
            foreach (var t in CuriaTypesOf(underlying))
                yield return t;
            yield break;
        }
        if (type.IsGenericParameter)
            yield break;

        if (type.Assembly.GetName().Name?.StartsWith("Curia.", StringComparison.Ordinal) == true)
            yield return type;

        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments())
            {
                foreach (var t in CuriaTypesOf(argument))
                    yield return t;
            }
        }
    }

    /// <summary>
    /// The open-parameter tripwire: a type parameter seen through an array, a nullable or a
    /// by-reference only (a bare <c>T</c>, <c>T[]</c>, <c>T?</c> or by-ref <c>T</c>). A type parameter
    /// inside a generic argument, such as <c>Func&lt;JsonElement, Result&lt;T&gt;&gt;</c>, is not counted.
    /// </summary>
    private static bool IsOpen(Type type)
    {
        if (type.HasElementType)
            return IsOpen(type.GetElementType()!);
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return IsOpen(underlying);
        return type.IsGenericParameter;
    }

    /// <summary>Step 4.</summary>
    private static bool IsStringLike(Type type)
    {
        if (type.IsGenericParameter)
            return false;
        if (type == typeof(string) || type == typeof(char[]))
            return true;
        if (type.FullName == "Microsoft.Extensions.Primitives.StringValues")
            return true;
        if (type.HasElementType)
            return IsStringLike(type.GetElementType()!);
        if (!type.IsGenericType)
            return false;

        var definition = type.GetGenericTypeDefinition();
        var arguments = type.GetGenericArguments();
        if ((definition == typeof(ReadOnlySpan<>) || definition == typeof(ReadOnlyMemory<>) || definition == typeof(Memory<>))
            && arguments[0] == typeof(char))
            return true;

        return arguments.Any(IsStringLike);
    }

    private static bool IsErrorFactory(Type type)
    {
        if (!(type.IsAbstract && type.IsSealed))
            return false;

        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        return methods.Length > 0 && methods.All(m => m.ReturnType == typeof(Error));
    }
}
