using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// CS-9 / R11.3: time enters through TimeProvider, never an ambient clock. The rule is
/// unconditional -- these APIs must appear nowhere in the shipped assemblies' IL, full stop.
/// The composition roots (<c>Curia.Api</c>, the <c>curia</c> CLI) are not an exception: they
/// choose <c>TimeProvider.System</c> once and hand it down, which is a different member and
/// leaves no banned reference behind.
///
/// <para><b>The assembly list is derived from <c>src/</c>, not written here.</b> This theory used
/// to name three assemblies of ten by hand, so a clock leak in <c>Curia.Domain</c> -- the assembly
/// R11.3 is most about -- would have gone unscanned, with the test green. A project that exists on
/// disk but is not in the test's output directory fails its row loudly rather than being skipped,
/// because a scan that quietly covers nothing is the shape of every probe this project has been
/// bitten by.</para>
///
/// <para>Reads raw PE metadata (member references) rather than reflecting over loaded types,
/// because the properties being banned (DateTimeOffset.UtcNow, DateTime.Now,
/// DateTime.Today) are BCL members Curia code would only ever *call*, never declare --
/// a call site leaves a MemberReference in the calling assembly's metadata even though
/// the property itself is declared in an assembly this test never loads.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test name carries the requirement ID (CS-9) it enforces verbatim (mirrors " +
        "LayeringTests' CS-6/CS-7 precedent).")]
public sealed class BannedApiTests
{
    private static readonly string[] BannedMembers = ["get_UtcNow", "get_Now", "get_Today"];

    /// <summary>
    /// Every assembly a project under <c>src/</c> produces, read from the project files at test
    /// time. The assembly name comes from <c>&lt;AssemblyName&gt;</c> when the project sets one
    /// (the CLI ships as <c>curia.dll</c>) and from the project file's name otherwise.
    /// </summary>
    public static TheoryData<string> SourceAssemblies()
    {
        var data = new TheoryData<string>();
        foreach (var name in SourceAssemblyNames())
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(SourceAssemblies))]
    public void CS9_NoAmbientClockApis(string assemblyName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll");

        // A missing assembly is a coverage gap, not a pass. Curia.Architecture.Tests.csproj has
        // to reference every project under src/ for its output to land here; a new project that
        // nobody remembered to add fails this row by name instead of going unscanned.
        Assert.True(
            File.Exists(path),
            $"{assemblyName}.dll is not in {AppContext.BaseDirectory}. A project under src/ that " +
            "Curia.Architecture.Tests.csproj does not reference cannot be scanned for CS-9; add the " +
            "ProjectReference.");

        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var offenders = md.MemberReferences
            .Select(md.GetMemberReference)
            .Select(m => md.GetString(m.Name))
            .Where(name => BannedMembers.Contains(name, StringComparer.Ordinal))
            .Distinct()
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"{assemblyName} references an ambient clock (CS-9): {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// The enumeration behind the theory cannot be vacuous: it must have found the real
    /// <c>src/</c>, and it must be reading assembly names from the project files rather than
    /// assuming directory names. <c>Curia.Domain</c> is the assembly this test project cannot lack,
    /// and the CLI is the one project whose assembly name differs from its directory.
    /// </summary>
    [Fact]
    public void CS9_TheScanIsDerivedFromTheSourceTree()
    {
        var names = SourceAssemblyNames();

        Assert.Contains("Curia.Domain", names);
        Assert.DoesNotContain("Curia.Client.Cli", names);
        Assert.Contains("curia", names);
    }

    private static string[] SourceAssemblyNames()
    {
        var src = FindSourceRoot();

        var names = Directory
            .EnumerateFiles(src, "*.csproj", SearchOption.AllDirectories)
            .Select(AssemblyNameOf)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        return names.Length == 0
            ? throw new InvalidOperationException(
                $"No project files found under {src}; CS-9 would be satisfied by scanning nothing.")
            : names;
    }

    private static string AssemblyNameOf(string projectFile) =>
        XDocument.Load(projectFile).Root?
            .Elements("PropertyGroup")
            .Elements("AssemblyName")
            .Select(element => element.Value.Trim())
            .FirstOrDefault(value => value.Length > 0)
        ?? Path.GetFileNameWithoutExtension(projectFile);

    /// <summary>The repository's <c>src</c> directory, found by walking up from the test binary -- the idiom LayeringTests uses.</summary>
    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException("Could not find a 'src' directory above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "src");
    }
}
