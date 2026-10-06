using System.Xml.Linq;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// The assemblies built from <c>src/</c>, for the facts that read their IL: moved here unchanged from
/// <see cref="ConstantArgumentTests"/> when <see cref="ProgramOutputTests"/> came to read the same set
/// (Task 10's review).
/// </summary>
internal static class Shipped
{
    /// <summary>
    /// The assembly each project under <c>src/</c> builds, as it sits beside this test: its
    /// <c>AssemblyName</c> where the project sets one (<c>curia</c>, <c>curia-mcp</c>,
    /// <c>curia-operator</c>), and the project's name otherwise. A project whose assembly is not here
    /// fails the fact rather than going unread.
    /// </summary>
    internal static List<string> ShippedAssemblies()
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
