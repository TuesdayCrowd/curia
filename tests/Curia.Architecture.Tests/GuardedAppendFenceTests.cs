using System.Diagnostics.CodeAnalysis;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R6.60 (errata G57), LCB25: a fence. The submit route reaches PERSIST only through the guarded <c>IIngestPipeline.PersistAsync</c> (the unguarded overload stays for the fixtures that
/// store what the door now refuses: trap 119), <c>IngestPipeline</c> has one guarded append and at most one unguarded, and <c>RaiseFlag</c> calls only the guarded
/// <c>IEventStore.AppendAsync</c>. <b>Said plainly: this pins a name</b> (trap 118): a new call site can pass a guard whose function proceeds on everything, so the claim is carried by the
/// behaviour probes (LCB11 to LCB16, with a route handed a stale list) and this is a floor. Read from the compiled IL with Mono.Cecil, as <c>EventStoreWriteSurfaceTests</c> reads it.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class GuardedAppendFenceTests
{
    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private static string AssemblyPath(string name) => Path.Combine(AppContext.BaseDirectory, name + ".dll");

    /// <summary>Every call in the methods of <paramref name="typeName"/> (nested types, which hold the async state machines, included) to a method named <paramref name="methodName"/> declared on <paramref name="declaringType"/>.</summary>
    private static List<MethodReference> Calls(string assembly, string typeName, string declaringType, string methodName)
    {
        using var module = ModuleDefinition.ReadModule(AssemblyPath(assembly));
        var type = module.GetType(typeName) ?? throw new InvalidOperationException($"{assembly} has no type {typeName}");
        var found = new List<MethodReference>();

        void Walk(TypeDefinition t)
        {
            foreach (var method in t.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is Code.Call or Code.Callvirt && instruction.Operand is MethodReference callee
                        && callee.Name == methodName && callee.DeclaringType.FullName == declaringType)
                        found.Add(callee);
                }
            }

            foreach (var nested in t.NestedTypes) Walk(nested);
        }

        Walk(type);
        return found;
    }

    private static bool Guarded(MethodReference callee) => callee.Parameters.Any(p => p.ParameterType.Name == "AppendGuard");

    /// <summary>LCB25 (a). The route calls only the guarded <c>PersistAsync</c>.</summary>
    [Fact]
    public void R8_86_LCB25_TheRouteCallsOnlyTheGuardedPersist()
    {
        var calls = Calls("Curia.Api", "Curia.Api.ForumEndpoints", "Curia.Application.Ingest.IIngestPipeline", "PersistAsync");
        Said("the route calls PersistAsync with a guard 1 time and without one 0 times", $"the route calls PersistAsync with a guard {calls.Count(Guarded)} time and without one {calls.Count(c => !Guarded(c))} times");
    }

    /// <summary>LCB25 (b). <c>IngestPipeline</c> appends once with a guard and at most once without (the fixtures' overload).</summary>
    [Fact]
    public void R8_86_LCB25_ThePipelineAppendsOnceGuardedAndAtMostOnceUnguarded()
    {
        var calls = Calls("Curia.Application", "Curia.Application.Ingest.IngestPipeline", "Curia.Application.Ports.IEventStore", "AppendAsync");
        Said("IngestPipeline appends guarded 1 time and unguarded at most 1 time", $"IngestPipeline appends guarded {calls.Count(Guarded)} time and unguarded {(calls.Count(c => !Guarded(c)) <= 1 ? "at most 1" : calls.Count(c => !Guarded(c)).ToString(System.Globalization.CultureInfo.InvariantCulture))} time");
    }

    /// <summary>LCB25 (c). <c>RaiseFlag</c> calls only the guarded <c>AppendAsync</c> (its flag guard). Red on the seam: the use case is unchanged and appends without one.</summary>
    [Fact]
    public void R10_94_LCB25_RaiseFlagCallsOnlyTheGuardedAppend()
    {
        var calls = Calls("Curia.Application", "Curia.Application.Moderation.RaiseFlag", "Curia.Application.Ports.IEventStore", "AppendAsync");
        Said("RaiseFlag appends guarded 1 time and unguarded 0 times", $"RaiseFlag appends guarded {calls.Count(Guarded)} time and unguarded {calls.Count(c => !Guarded(c))} times");
    }
}
