using System.Diagnostics.CodeAnalysis;
using System.Xml.Linq;
using Curia.Client;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R10.63 (errata G17): a parameter that must be a constant is passed one, because the method that
/// takes it is only ever called.
///
/// <para><b>Why the analyzer is not enough.</b> <see cref="ConstantExpectedAttribute"/> is checked by
/// CA1857 where a method is called, and a method-group conversion is not a call:
/// <c>Func&lt;string, FrameBuilder&gt; line = frame.Line;</c> builds with no diagnostic, and
/// <c>line(served)</c> then writes a stranger's words as a line of the client's own (Task 4's review,
/// m1). A lambda that calls the method is a call, and CA1857 sees its argument. So this fact reads
/// the IL of every assembly built from <c>src/</c> and fails on any <c>ldftn</c> or
/// <c>ldvirtftn</c> -- the instructions a delegate over a method is made from -- whose target has a
/// parameter so marked. The one way past the attribute is then a build that fails its tests.</para>
///
/// <para><b>Not vacuous.</b> The scan must read every project under <c>src/</c>, must find
/// <c>FrameBuilder.Line</c> among the methods it guards, must see delegates at all, and must find the
/// one bypass this file holds on purpose, and nothing else in this assembly.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class ConstantArgumentTests
{
    private const string ConstantExpected = "System.Diagnostics.CodeAnalysis.ConstantExpectedAttribute";

    [Fact]
    public void R10_63_NoMethodWhoseParameterMustBeAConstantIsTakenAsADelegate()
    {
        var shipped = ShippedAssemblies();
        Assert.True(shipped.Count >= 12, $"only {shipped.Count} projects were found under src/; the scan is looking in the wrong place");

        var guarded = new HashSet<string>(StringComparer.Ordinal);
        var delegates = new List<(string Site, string Target)>();
        foreach (var path in shipped)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);
            Scan(assembly, guarded, delegates);
        }

        Assert.Contains(guarded, method => method.Contains("Curia.Client.FrameBuilder::Line(System.String)", StringComparison.Ordinal));
        Assert.True(delegates.Count > 0, "no delegate over any method was seen in src/; the IL scan reads nothing");

        var offenders = delegates
            .Where(d => guarded.Contains(d.Target))
            .Select(d => $"{d.Site} takes {d.Target} as a delegate")
            .ToList();
        Assert.True(
            offenders.Count == 0,
            "a method whose parameter must be a constant is taken as a delegate, which passes any string past CA1857: "
            + string.Join("; ", offenders));

        // The scan finds a bypass where there is one: this assembly holds exactly one, below.
        Assert.NotNull(Bypass(new FrameBuilder()));
        using var self = AssemblyDefinition.ReadAssembly(typeof(ConstantArgumentTests).Assembly.Location);
        var own = new List<(string Site, string Target)>();
        Scan(self, new HashSet<string>(StringComparer.Ordinal), own);
        var found = Assert.Single(own, d => guarded.Contains(d.Target));
        Assert.Equal("Curia.Architecture.Tests.ConstantArgumentTests::Bypass", found.Site);
    }

    /// <summary>The bypass this fact exists to find, held here so the fact can show it finds one.</summary>
    private static Func<string, FrameBuilder> Bypass(FrameBuilder frame) => frame.Line;

    /// <summary>
    /// Every method of <paramref name="assembly"/> with a parameter marked
    /// <see cref="ConstantExpectedAttribute"/>, into <paramref name="guarded"/>; and every delegate
    /// its IL makes over a method, with the method that makes it, into <paramref name="delegates"/>.
    /// </summary>
    private static void Scan(AssemblyDefinition assembly, HashSet<string> guarded, List<(string Site, string Target)> delegates)
    {
        foreach (var type in AllTypes(assembly.MainModule))
        {
            foreach (var method in type.Methods)
            {
                if (method.Parameters.Any(p => p.CustomAttributes.Any(a => a.AttributeType.FullName == ConstantExpected)))
                    guarded.Add(method.FullName);

                if (!method.HasBody)
                    continue;

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.OpCode.Code is Code.Ldftn or Code.Ldvirtftn && instruction.Operand is MethodReference target)
                        delegates.Add(($"{type.FullName}::{method.Name}", target.GetElementMethod().FullName));
                }
            }
        }
    }

    /// <summary>
    /// The assembly each project under <c>src/</c> builds, as it sits beside this test: its
    /// <c>AssemblyName</c> where the project sets one (<c>curia</c>, <c>curia-mcp</c>,
    /// <c>curia-operator</c>), and the project's name otherwise. A project whose assembly is not here
    /// fails the fact rather than going unread.
    /// </summary>
    private static List<string> ShippedAssemblies()
    {
        var paths = new List<string>();
        foreach (var project in Directory.GetDirectories(Path.Combine(FindRepoRoot(), "src")))
        {
            var name = Path.GetFileName(project);
            var file = Path.Combine(project, name + ".csproj");
            if (!File.Exists(file))
                continue;

            var assemblyName = XDocument.Load(file).Descendants("AssemblyName").FirstOrDefault()?.Value ?? name;
            var path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");
            Assert.True(File.Exists(path), $"{assemblyName}.dll, which {name} builds, is not beside this test, so it would go unread; reference the project");
            paths.Add(path);
        }

        return paths;
    }

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

    /// <summary>Mirrors EventStoreWriteSurfaceTests.FindRepoRoot: each file in this suite is self-contained.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not find repo root (a 'src' directory) above " + AppContext.BaseDirectory);
    }
}
