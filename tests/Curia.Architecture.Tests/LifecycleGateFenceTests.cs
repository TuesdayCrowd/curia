using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;
using Curia.Application.Ports;
using Curia.Infrastructure;
using Curia.Infrastructure.Lifecycle;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R4.38 (add.), spec section 4.13: GF6 and the public factory's tripwire. CP-B forbids every PR after
/// the guard's own to touch this file; it goes red, deliberately, only in the owner's lift PR.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class LifecycleGateFenceTests
{
    private static readonly LifecycleKind[] Kinds = [LifecycleKind.Rotation, LifecycleKind.Retirement, LifecycleKind.Compromise];

    /// <summary>GF6: the record embedded in the production assembly is present, parses, and approves nothing.</summary>
    [Fact]
    public void R4_38_TheApprovedSetIsEmpty()
    {
        var record = LifecycleApprovalRecord.Embedded();

        Assert.True(record.IsOk, record.Match(_ => string.Empty, e => $"{e.Type}: {e.Detail}"));
        Assert.Empty(record.Match(r => r.Approved, _ => default));
    }

    /// <summary>
    /// GF6's production-path tripwire: the gate the Forum and <c>curia-operator</c> run is built by the
    /// public <see cref="EmbeddedLifecycleAppendGate.FromEmbedded(string?)"/>, so this fact reads that
    /// path. Each opt-in passes the parse and the compromise-order rule, so only the approval check can
    /// refuse it. Red when the public factory reads a record that approves a kind (FL-23).
    /// </summary>
    [Fact]
    public void R4_38_ThePublicFactoryRefusesEachKindItIsAsked()
    {
        foreach (var optIn in new[] { "compromise", "compromise,rotation", "compromise,retirement" })
        {
            var refused = EmbeddedLifecycleAppendGate.FromEmbedded(optIn);
            Assert.False(refused.IsOk, $"the public factory built a gate for {optIn}");
            Assert.Equal(LifecycleApprovalErrors.KindNotApprovedType, refused.Match(_ => string.Empty, e => e.Type));
        }

        var none = EmbeddedLifecycleAppendGate.FromEmbedded("none");
        Assert.True(none.IsOk);
        var gate = none.Match(g => g, e => throw new InvalidOperationException(e.Type));
        foreach (var kind in Kinds)
            Assert.False(gate.Enables(kind), $"the opt-in none enables {kind}");
    }

    /// <summary>The four test assemblies that link <c>PermissiveLifecycleAppendGate</c> (Task L.1), and so each hold their own internal copy.</summary>
    private static readonly string[] LinkingTestAssemblies =
        ["Curia.Application.Tests", "Curia.Infrastructure.Tests", "Curia.Api.Tests", "Curia.Architecture.Tests"];

    private const string PermissiveGateFullName = "Curia.Tests.Shared.PermissiveLifecycleAppendGate";

    /// <summary>
    /// GF5: no assembly built from <c>src/</c> references a test assembly or contains a type named as the
    /// permissive gate, and no project under <c>tools/</c> references a test project or names the gate. The
    /// four test assemblies that link the gate each hold a copy, found here by its full name, and no other
    /// test assembly holds one (so a link removed from one of them fails here, and the fence cannot pass by
    /// finding nothing). Run in Release and Debug (D16). Red when a reference is added.
    /// </summary>
    [Fact]
    public void R4_38_NoProductionAssemblyReferencesThePermissiveGate()
    {
        var repoRoot = RepoRoot();
        var testProjects = Directory.GetDirectories(Path.Combine(repoRoot, "tests"))
            .Select(Path.GetFileName)
            .Where(n => n is not null && File.Exists(Path.Combine(repoRoot, "tests", n, n + ".csproj")))
            .Select(n => n!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.Contains("Curia.Api.Tests", testProjects);

        foreach (var path in Shipped.ShippedAssemblies())
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();

            var referenced = md.AssemblyReferences.Select(h => md.GetString(md.GetAssemblyReference(h).Name)).ToList();
            Assert.DoesNotContain(referenced, name => testProjects.Contains(name));

            var typeNames = md.TypeDefinitions
                .Select(h => (md.GetString(md.GetTypeDefinition(h).Namespace), md.GetString(md.GetTypeDefinition(h).Name)))
                .ToList();
            Assert.DoesNotContain(typeNames, t => t.Item2.Contains("PermissiveLifecycleAppendGate", StringComparison.Ordinal));
        }

        foreach (var project in Directory.GetDirectories(Path.Combine(repoRoot, "tools")))
        {
            var csproj = Directory.GetFiles(project, "*.csproj").SingleOrDefault();
            if (csproj is null)
                continue;

            var references = XDocument.Load(csproj).Descendants("ProjectReference").Select(r => (string?)r.Attribute("Include") ?? string.Empty);
            Assert.DoesNotContain(references, r => r.Contains("/tests/", StringComparison.Ordinal) || r.Contains("..\\tests", StringComparison.Ordinal));
            foreach (var source in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/obj/", StringComparison.Ordinal) && !f.Contains("/bin/", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain("PermissiveLifecycleAppendGate", File.ReadAllText(source), StringComparison.Ordinal);
            }
        }

        foreach (var project in testProjects)
        {
            var path = SiblingBuiltAssembly(repoRoot, "tests", project);
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            var md = pe.GetMetadataReader();
            var holdsACopy = md.TypeDefinitions.Any(h =>
                md.GetString(md.GetTypeDefinition(h).Namespace) + "." + md.GetString(md.GetTypeDefinition(h).Name) == PermissiveGateFullName);

            Assert.True(
                holdsACopy == LinkingTestAssemblies.Contains(project, StringComparer.Ordinal),
                $"{project} {(holdsACopy ? "holds" : "does not hold")} a copy of {PermissiveGateFullName}; exactly {string.Join(", ", LinkingTestAssemblies)} link it");
        }
    }

    /// <summary>
    /// GF5, the implementer clause: within the assemblies built from <c>src/</c>, the only type implementing
    /// <see cref="ILifecycleAppendGate"/> is <see cref="EmbeddedLifecycleAppendGate"/>. Matched by
    /// <c>typeof</c> and <c>IsAssignableFrom</c>, never a name string, so a renamed copy, class or struct, is found. Red when a
    /// second implementer is added (FL-18). The projects under <c>tools/</c> reference neither the port nor
    /// the Application layer (held by <see cref="R4_38_NoProductionAssemblyReferencesThePermissiveGate"/>'s
    /// project-reference check and the next fact).
    /// </summary>
    [Fact]
    public void R4_38_OneLifecycleGateImplementerInSrcAndTools()
    {
        var implementers = ImplementersInShipped(typeof(ILifecycleAppendGate));

        Assert.Equal([typeof(EmbeddedLifecycleAppendGate).FullName!], implementers);
        AssertToolsReachNeither();
    }

    /// <summary>
    /// The chokepoint's coverage: <see cref="PostgresEventStore"/> is the only <see cref="IEventStore"/>
    /// implementer in the assemblies built from <c>src/</c>, so the production protection is that one core.
    /// Red when another is added (FL-17).
    /// </summary>
    [Fact]
    public void R4_38_PostgresEventStoreIsTheOnlyEventStoreInSrc()
    {
        var implementers = ImplementersInShipped(typeof(IEventStore));

        Assert.Equal([typeof(PostgresEventStore).FullName!], implementers);
        AssertToolsReachNeither();
    }

    /// <summary>
    /// Within <c>src/</c> and <c>tools/</c>, the only caller of <c>LifecycleApprovalRecord</c>'s constructor
    /// is <c>LifecycleApprovalRecord.Parse</c>, and the only caller of the record-taking
    /// <c>EmbeddedLifecycleAppendGate.Create</c> is <c>EmbeddedLifecycleAppendGate.FromEmbedded</c>; so no
    /// composition builds a non-empty record in code and hands it to a gate. The constructor clause is sound
    /// because the record is a sealed class with one constructor (a C# record would emit <c>&lt;Clone&gt;$</c>,
    /// whose call to the copy constructor would be a second caller). Each clause asserts its expected call
    /// site is found, so the fact cannot pass by reading nothing. Run in Release and Debug (D16). Red when a
    /// second call site is added (FL-21, FL-22).
    /// </summary>
    [Fact]
    public void R4_38_OnlyParseBuildsAnApprovalRecordAndOnlyFromEmbeddedCallsCreate()
    {
        const string Record = "Curia.Infrastructure.Lifecycle.LifecycleApprovalRecord";
        const string Gate = "Curia.Infrastructure.Lifecycle.EmbeddedLifecycleAppendGate";
        var sites = Shipped.ShippedAssemblies().SelectMany(path => IlCallSites.CallSites(path)).ToList();

        var constructors = sites.Where(s => s.CalleeType == Record && s.CalleeMember == ".ctor").ToList();
        Assert.NotEmpty(constructors);
        Assert.All(constructors, c => Assert.True(
            c.CallerType == Record && c.CallerMethod == "Parse",
            $"{c.CallerType}.{c.CallerMethod} constructs a LifecycleApprovalRecord; only {Record}.Parse may"));

        var creates = sites.Where(s => s.CalleeType == Gate && s.CalleeMember == "Create").ToList();
        Assert.NotEmpty(creates);
        Assert.All(creates, c => Assert.True(
            c.CallerType == Gate && c.CallerMethod == "FromEmbedded",
            $"{c.CallerType}.{c.CallerMethod} calls EmbeddedLifecycleAppendGate.Create; only {Gate}.FromEmbedded may"));
    }

    private static List<string> ImplementersInShipped(Type port)
    {
        var all = new List<Type?>();
        foreach (var path in Shipped.ShippedAssemblies())
        {
            try
            {
                all.AddRange(Assembly.LoadFrom(path).GetTypes());
            }
            catch (ReflectionTypeLoadException e)
            {
                // A partly loaded assembly yields nulls; ImplementersAmong refuses them, so a load failure
                // cannot make the fence pass by finding nothing.
                all.AddRange(e.Types);
            }
        }

        return ImplementersAmong(port, all);
    }

    /// <summary>
    /// The full names of the types in <paramref name="types"/> that implement <paramref name="port"/>,
    /// sorted. Anything that is not an interface counts, so a renamed copy, class or struct, is found;
    /// interfaces stay out, because the port itself and an interface extending it implement nothing.
    /// A null entry (a type the loader could not load) is an error, not a skip.
    /// </summary>
    private static List<string> ImplementersAmong(Type port, IEnumerable<Type?> types)
    {
        var found = new List<string>();
        foreach (var t in types)
        {
            Assert.True(t is not null, "an assembly built from src/ or tools/ did not load fully; the implementer fence cannot see its types");
            if (t is { IsInterface: false } && port.IsAssignableFrom(t))
                found.Add(t.FullName!);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private readonly struct ValueTypeGate : ILifecycleAppendGate
    {
        public bool Enables(LifecycleKind kind) => true;
    }

    private interface IGateExtension : ILifecycleAppendGate;

    private static class NotAGate;

    /// <summary>
    /// The implementer fences (FL-17, FL-18) see a value type. A <c>readonly struct</c> implementing the
    /// port is returned, so it cannot be boxed into a composition unseen; controls: a type that does not
    /// implement the port, and an interface extending it, are not. Red when the filter admits classes only (FL-29).
    /// </summary>
    [Fact]
    public void R4_38_TheImplementerScanSeesAValueTypeImplementer()
    {
        var found = ImplementersAmong(
            typeof(ILifecycleAppendGate),
            [typeof(ValueTypeGate), typeof(NotAGate), typeof(IGateExtension), typeof(ILifecycleAppendGate)]);

        Assert.Equal([typeof(ValueTypeGate).FullName!], found);
    }

    /// <summary>The projects under <c>tools/</c> reference no project that holds the port, the store or the gate.</summary>
    private static void AssertToolsReachNeither()
    {
        foreach (var project in Directory.GetDirectories(Path.Combine(RepoRoot(), "tools")))
        {
            var csproj = Directory.GetFiles(project, "*.csproj").SingleOrDefault();
            if (csproj is null)
                continue;

            var references = XDocument.Load(csproj).Descendants("ProjectReference").Select(r => (string?)r.Attribute("Include") ?? string.Empty);
            Assert.DoesNotContain(references, r =>
                r.Contains("Curia.Application", StringComparison.Ordinal) || r.Contains("Curia.Infrastructure", StringComparison.Ordinal));
        }
    }

    /// <summary>The first ancestor of the test's output holding a <c>src</c> directory.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not find repo root (a 'src' directory) above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// <paramref name="project"/>'s built assembly in the configuration and framework this test runs in,
    /// located from <c>Curia.Domain.dll</c>'s own output folder (the idiom of <c>EventStoreWriteSurfaceTests</c>).
    /// </summary>
    private static string SiblingBuiltAssembly(string repoRoot, string topLevelDir, string project)
    {
        var tfmDir = Path.GetDirectoryName(typeof(Curia.Domain.DomainEvent).Assembly.Location)!;
        var path = Path.Combine(repoRoot, topLevelDir, project, "bin", Path.GetFileName(Path.GetDirectoryName(tfmDir))!, Path.GetFileName(tfmDir), project + ".dll");

        return File.Exists(path)
            ? path
            : throw new InvalidOperationException($"Expected {project}.dll at {path}; run `dotnet build Curia.sln` (the whole solution) so every assembly this fact inspects is built.");
    }
}
