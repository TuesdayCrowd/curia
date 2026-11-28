using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R7.4 (rev.) (errata G87, the held R7.34; the owner's ruling of 2026-11-27): nothing survives a request. The Forum holds between requests no row of the event log, no verdict of a signature check and no
/// decision made from either, the policy decision point's included; state held for one request (a memo, a fold's intermediate) is dropped with it. RC8 is the structural half of that
/// (a behavioural probe cannot fail on a cache whose decision is a function of its key, so only a fence can): no field of a shipped type holds one of the request-derived types
/// (<see cref="Forbidden"/>), on its own or inside a generic, an array or another shipped type's field, unless the declaring type is a reasoned per-call value (<see cref="PerCallValues"/>)
/// that no service registration builds. A static field is never excused. It is red on the tree that held <c>CachingPolicyDecisionPoint</c> (a <c>ConcurrentDictionary</c> of
/// <c>Entry</c> holding an <c>AuthorizationDecision</c>), and a canary singleton in this project (<see cref="CanarySingleton"/>) is the positive control: the same rule is run over it and
/// must name it exactly once, so a fence that came to find nothing is red.
///
/// <para><b>What this does not hold.</b> A field of a compiler-generated closure or state machine is not read (the IL of a closure's fields is not a declared type), so a long-lived
/// closure that captured a row is not seen; a request-derived value held as <c>object</c> or behind a type this list does not name is not seen. <c>ActaLog</c>'s memo
/// (<c>_subtrees</c>) is the one cache the rule admits, held by <c>InclusionCacheFenceTests</c>. The rule reads the declared types of the shipped assemblies.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class NothingSurvivesARequestFenceTests
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    /// <summary>The request-derived types, by simple name (some are declared in different assemblies and a rename must fail here and not pass).</summary>
    private static readonly string[] Forbidden =
    [
        "AppendedEvent", "PositionedEvent", "LogSlice", "VerifiedLog", "VerifiedLifecycle", "ActDecision", "AuthorizationDecision", "MerkleSubtreeCache",
    ];

    /// <summary>
    /// The reasoned exceptions: each type is a value one call makes and hands back or holds for its own length, and none is registered with the container
    /// (a registered one would be a singleton holding a row, which is what this fence is for). The key is the declaring type's full name.
    /// </summary>
    private static readonly (string Type, string Reason)[] PerCallValues =
    [
        ("Curia.Application.Credentials.VerifiedLog", "the verdict over one read's rows, made by LifecycleVerifier.OfLog per call"),
        ("Curia.Application.Credentials.VerifiedLifecycle", "the lifecycle fold of one read, made per call"),
        ("Curia.Application.Credentials.LogBoundKeys+StreamRead", "one stream read's verified lifecycle, returned to its caller"),
        ("Curia.Application.Credentials.AgentKeySet", "the key set one request reads, with the identity's verified lifecycle, returned to the route that publishes it"),
        ("Curia.Application.Credentials.KeyActs+WholeLifecycleRead", "the operator's whole lifecycle read, made per call"),
        ("Curia.Application.Reads.AgentRead", "one request's read for one agent and the view over it (errata G87): made by AgentRead.ReadAsync per call and dropped with the request"),
        ("Curia.Application.Reads.LogSlice", "the rows one read plan returned, made per call"),
        ("Curia.Application.Reads.PositionedEvent", "one row of a read with its log index, made per call"),
        ("Curia.Application.Reads.PositionedLog", "a whole list seen as positioned rows, a view over the list it is given for the length of a fold"),
        ("Curia.Application.Reads.PostStreamRead", "one post's stream read, with the view over it, made per call"),
        ("Curia.Application.Reads.ReadPlan", "a plan's stage functions take the events the earlier stages returned; a plan is made per call and holds none"),
        ("Curia.Application.Credentials.KeyActs+LifecycleRead", "one lifecycle read's result, returned to its caller"),
        ("Curia.Application.Credentials.LifecycleFold", "the fold of one agent's stream, made by Decide per call"),
        ("Curia.Application.Credentials.LifecycleFold+Decided", "the fold's result, returned to its caller"),
        ("Curia.Application.Ingest.LogFacts", "the facts of one submission's read, extended by the append's guard within the request"),
        ("Curia.Application.Ports.AppendGuard", "the guard of one append: a position and a function of the delta, made per append"),
        ("Curia.Application.Projections.ActaLog", "the log of one request, with its memo (InclusionCacheFenceTests holds that it is held by no static and taken by no constructor)"),
        ("Curia.Application.Projections.HonouredEvents", "the rows a verified lifecycle honours, made per call"),
        ("Curia.Api.ForumEndpoints+DecidedLog", "one submit attempt's read and decision, local to the handler"),
        ("Curia.Api.ForumEndpoints+FlagListContext", "one flag-list request's decision and read, local to the handler"),
    ];

    private static readonly string[] MustExist = ["AppendedEvent", "PositionedEvent", "LogSlice", "VerifiedLog", "VerifiedLifecycle", "ActDecision", "AuthorizationDecision", "MerkleSubtreeCache"];

    private static bool Holds(Type type, HashSet<Type> seen)
    {
        if (Forbidden.Contains(type.Name, StringComparer.Ordinal))
            return true;
        if (type.HasElementType)
            return Holds(type.GetElementType()!, seen);
        if (type.IsGenericType && type.GetGenericArguments().Any(a => Holds(a, seen)))
            return true;
        if (!seen.Add(type) || type.Assembly.GetName().Name?.StartsWith("Curia", StringComparison.Ordinal) != true)
            return false;
        return type.GetFields(All).Any(f => !f.IsStatic && Holds(f.FieldType, seen));
    }

    private static bool Generated(Type t) =>
        t.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || (t.DeclaringType is { } d && Generated(d));

    /// <summary>Every field that holds a request-derived type, as "Type.field", over <paramref name="types"/>; a per-call value's instance fields are excused, a static field never.</summary>
    internal static string[] Offenders(IEnumerable<Type> types) =>
        [.. types
            .Where(t => !Generated(t))
            .SelectMany(t => t.GetFields(All)
                .Where(f => Holds(f.FieldType, []) && (f.IsStatic || !PerCallValues.Any(v => v.Type == t.FullName && v.Reason.Length > 0)))
                .Select(f => $"{t.FullName}.{f.Name}"))
            .Order(StringComparer.Ordinal)];

    private static IEnumerable<Type> ShippedTypes() =>
        Shipped.ShippedAssemblies().SelectMany(p => Assembly.LoadFrom(p).GetTypes());

    [Fact]
    public void R7_4_RC8_NoFieldOfAShippedTypeHoldsARequestDerivedValue()
    {
        var offenders = Offenders(ShippedTypes());
        Assert.True(offenders.Length == 0, "Expected: no field of a shipped type holds " + string.Join(", ", Forbidden) + "  Actual: " + string.Join("; ", offenders));
    }

    [Fact]
    public void R7_4_RC8_ThePositiveControlIsFoundExactlyOnce()
    {
        Assert.Equal(0, new CanarySingleton().Count);
        // The fence run over its own canary, beside the shipped types: a singleton that holds a row, in the test project, must be named once (FRC14).
        var named = Offenders(ShippedTypes().Append(typeof(CanarySingleton))).Where(o => o.StartsWith(typeof(CanarySingleton).FullName!, StringComparison.Ordinal)).ToArray();
        Assert.True(named.Length == 1, "Expected: the canary named exactly once  Actual: " + named.Length + " (" + string.Join("; ", named) + ")");
    }

    [Fact]
    public void R7_4_RC8_TheFenceReadsTheTypesItNames()
    {
        var shipped = ShippedTypes().ToArray();
        var names = shipped.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var missing = MustExist.Where(n => !names.Contains(n)).ToArray();
        Assert.True(missing.Length == 0, "Expected: every listed type that exists is declared in a shipped assembly  Actual missing: " + string.Join(", ", missing));
        foreach (var v in PerCallValues)
            Assert.True(shipped.Any(t => t.FullName == v.Type), $"a per-call row names {v.Type}, which no shipped assembly declares");
    }
}

/// <summary>RC8's positive control: a singleton-shaped type that holds a row. The fence must name it once; it is never registered anywhere.</summary>
[SuppressMessage("Performance", "CA1823:Avoid unused private fields", Justification = "The field is the thing the fence reads.")]
[SuppressMessage("Style", "IDE0052:Remove unread private members", Justification = "The field is the thing the fence reads.")]
internal sealed class CanarySingleton
{
    private readonly List<Curia.Domain.AppendedEvent> _rows = [];

    public int Count => _rows.Count;
}
