using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R10.64 (errata G17): another program's bytes are decoded as UTF-8, and a redirected process's
/// own reader does not do that -- it detects a byte order mark at offset 0, so the child's first bytes
/// choose the decoding (Task 10's review). <c>Curia.Client.ProgramOutput</c> reads the raw streams
/// and decodes them by the rule; this fact holds every other read of a process's output to be none.
///
/// <para>It reads the IL of every assembly built from <c>src/</c> and fails on any call to
/// <c>Process.get_StandardOutput</c> or <c>Process.get_StandardError</c> made outside
/// <c>ProgramOutput</c> -- a lambda's closure or an async method's state machine counted as the
/// type it is written in. Not vacuous: it must see both getters inside <c>ProgramOutput</c>, and
/// <c>ProgramOutput</c> called from both of today's child-process sites, <c>curia</c>'s
/// <c>Testis</c> and <c>ExternalSigner</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ProgramOutputTests
{
    private const string ProgramOutput = "Curia.Client.ProgramOutput";

    private static readonly HashSet<string> Getters = new(StringComparer.Ordinal)
    {
        "System.IO.StreamReader System.Diagnostics.Process::get_StandardOutput()",
        "System.IO.StreamReader System.Diagnostics.Process::get_StandardError()",
    };

    [Fact]
    public void R10_64_AnotherProgramsOutputIsReadOnlyThroughProgramOutput()
    {
        var reads = new List<(string Site, string Getter)>();
        var callers = new List<(string Site, string Callee)>();

        foreach (var path in Shipped.ShippedAssemblies())
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            foreach (var type in AllTypes(assembly.MainModule))
            foreach (var method in type.Methods.Where(m => m.HasBody))
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt) || instruction.Operand is not MethodReference callee)
                    continue;

                var site = WrittenIn(type).FullName;
                var name = callee.GetElementMethod().FullName;
                if (Getters.Contains(name))
                    reads.Add((site, name));
                else if (callee.DeclaringType.FullName == ProgramOutput && callee.Name is "ReadAsync" or "Read")
                    callers.Add((site, callee.Name));
            }
        }

        // The offenders first, so a site that reads around ProgramOutput is named rather than reported
        // as a caller gone missing.
        var offenders = reads
            .Where(read => read.Site != ProgramOutput)
            .Select(read => $"{read.Site} calls {read.Getter}")
            .ToList();
        Assert.True(
            offenders.Count == 0,
            "another program's output is read through the process's own reader, which detects a byte order mark and so lets the program's first bytes choose the decoding: "
            + string.Join("; ", offenders));

        foreach (var getter in Getters)
            Assert.Contains(reads, read => read.Site == ProgramOutput && read.Getter == getter);
        Assert.Contains(callers, call => call.Site == "Curia.Client.Cli.Testis" && call.Callee == "ReadAsync");
        Assert.Contains(callers, call => call.Site == "Curia.Client.ExternalSigner" && call.Callee == "Read");
    }

    /// <summary>The type a method's source is written in: a compiler-generated closure or state machine counted as the type that encloses it.</summary>
    private static TypeDefinition WrittenIn(TypeDefinition type)
    {
        while (type.DeclaringType is not null && IsCompilerGenerated(type))
            type = type.DeclaringType;
        return type;
    }

    private static bool IsCompilerGenerated(TypeDefinition type) =>
        type.Name.StartsWith('<')
        || type.CustomAttributes.Any(a => a.AttributeType.FullName == typeof(CompilerGeneratedAttribute).FullName);

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var type in module.Types)
        foreach (var flattened in AllTypesRecursive(type))
            yield return flattened;
    }

    private static IEnumerable<TypeDefinition> AllTypesRecursive(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
        foreach (var flattened in AllTypesRecursive(nested))
            yield return flattened;
    }
}
