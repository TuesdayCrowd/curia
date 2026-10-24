using System.Diagnostics.CodeAnalysis;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R14.11 (errata G27, ruling 1): the server a host runs is the server the gates drive. The Mcp and Api harnesses call
/// <c>ServerComposition.Options</c>; this holds that the host does too, by reading <c>curia-mcp</c>'s IL and counting
/// the methods that construct <c>McpServerOptions</c>. A host that built its own options beside the composition would
/// be a server no fact exercises. Top-level statements compile into a generated <c>Program</c> method, which the scan
/// reads like any other.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class McpHostCompositionTests
{
    private const string Options = "ModelContextProtocol.Server.McpServerOptions";

    [Fact]
    public void R14_11_TheHostBuildsItsServerOptionsThroughTheComposition()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "curia-mcp.dll");
        Assert.True(File.Exists(path), $"curia-mcp.dll is not beside this test: {path}");

        var constructors = new List<string>();
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        foreach (var type in AllTypes(assembly.MainModule))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.Code == Code.Newobj
                && instruction.Operand is MethodReference callee
                && callee.DeclaringType.FullName == Options)
                constructors.Add($"{type.FullName}::{method.Name}");
        }

        Assert.True(constructors.Count > 0, $"no method in curia-mcp.dll constructs {Options}: a zero is a defect in this test, not a clean host");
        Assert.Equal(["Curia.Mcp.ServerComposition::Options"], constructors.Distinct(StringComparer.Ordinal).ToArray());
        Assert.Single(constructors);
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var pending = new Stack<TypeDefinition>(module.Types);
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            yield return type;
            foreach (var nested in type.NestedTypes) pending.Push(nested);
        }
    }
}
