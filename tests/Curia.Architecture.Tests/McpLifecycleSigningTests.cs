using System.Diagnostics.CodeAnalysis;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R4.39 and R11.20 (errata G21, spec section 4.12): the MCP adapter never asks an external signer to sign a lifecycle
/// statement. The external signer's protocol splits <c>sign</c> from <c>sign-lifecycle</c> so that a process that
/// may call the signer to post cannot thereby obtain a signature that adds or ends a key; that holds only while the
/// adapter, which posts, never calls the second. It reads <c>curia-mcp</c>'s IL, not its source, so a method group
/// or a lambda counts as the call it makes.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class McpLifecycleSigningTests
{
    private const string Client = "Curia.Client";

    /// <summary>What <c>curia-mcp</c> must not reach: the statements, the signer's lifecycle operation, and the two routes.</summary>
    private static bool Forbidden(MemberReference callee)
    {
        var type = callee.DeclaringType.FullName;
        return type.StartsWith(Client + ".KeyActs", StringComparison.Ordinal)
            || (type == Client + ".ExternalSigner" && callee.Name == "SignLifecycle")
            || (type == Client + ".ForumSession" && callee.Name is "RotateKeyAsync" or "RetireKeyAsync");
    }

    [Fact]
    public void R11_20_CuriaMcpNeverCallsSignLifecycle()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "curia-mcp.dll");
        Assert.True(File.Exists(path), $"curia-mcp.dll is not beside this test: {path}");

        var reached = new List<string>();
        var intoClient = new HashSet<string>(StringComparer.Ordinal);

        using var assembly = AssemblyDefinition.ReadAssembly(path);
        foreach (var type in AllTypes(assembly.MainModule))
        foreach (var method in type.Methods.Where(m => m.HasBody))
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj or Code.Ldftn or Code.Ldvirtftn)
                || instruction.Operand is not MethodReference callee)
                continue;

            var declaring = callee.DeclaringType.GetElementType().FullName;
            if (declaring.StartsWith(Client + ".", StringComparison.Ordinal)) intoClient.Add(declaring + "::" + callee.Name);
            if (Forbidden(callee.GetElementMethod())) reached.Add($"{type.FullName}.{method.Name} reaches {declaring}::{callee.Name}");
        }

        // Not vacuous: the adapter does reach the client, including the session it posts through and the client it reads with.
        Assert.Contains(intoClient, c => c.StartsWith("Curia.Client.ForumSession::", StringComparison.Ordinal));
        Assert.Contains(intoClient, c => c.StartsWith("Curia.Client.ForumClient::", StringComparison.Ordinal));

        Assert.True(reached.Count == 0, "curia-mcp reaches the lifecycle's signing path: " + string.Join("; ", reached));
    }

    /// <summary>The scan can see the call it forbids: the client's own <c>KeyActs</c> makes it, so a reference to either is a path to it.</summary>
    [Fact]
    public void R11_20_TheScanSeesTheCallItForbids()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Curia.Client.dll");
        using var assembly = AssemblyDefinition.ReadAssembly(path);

        var signs = AllTypes(assembly.MainModule)
            .Where(t => t.FullName.StartsWith(Client + ".KeyActs", StringComparison.Ordinal))
            .SelectMany(t => t.Methods.Where(m => m.HasBody))
            .SelectMany(m => m.Body.Instructions)
            .Any(i => i.Operand is MethodReference callee && callee.Name == "SignLifecycle" && Forbidden(callee.GetElementMethod()));

        Assert.True(signs, "KeyActs does not call SignLifecycle, so the scan above forbids a call nothing makes");
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
