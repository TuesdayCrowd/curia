using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Xml.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// CS-15: closes the gap Stage 1's report left open verbatim -- "C# has no access modifier
/// narrower than the assembly. Once InternalsVisibleTo is granted, any code in that
/// assembly can call the internal constructor, not just one designated method." Stage 1
/// narrowed the write surface to the assembly level (<see cref="Curia.Domain.AppendedEvent"/>'s
/// constructor is <see langword="internal"/>, and Curia.Domain.csproj grants
/// InternalsVisibleTo to exactly two assemblies). This suite narrows it the rest of the
/// way, to the type level.
///
/// <b>Definition used throughout this file:</b> "the event store's write surface" means
/// every code location capable of producing a value of type
/// <see cref="Curia.Domain.AppendedEvent"/> that claims to have actually been appended --
/// i.e. every call site of the constructor that takes the four fields the store assigns
/// (seq, aggregate id, server timestamp, and the original event). That is deliberately
/// narrower than "everywhere the type AppendedEvent is *used*" (read access -- pattern
/// matching on one, projecting its properties, holding a reference to one someone else
/// constructed) and *not* narrower than the record's compiler-generated copy constructor
/// (the one `with { }` calls): AppendedEvent's properties are get-only, not init-only, so
/// `with { }` can only ever reproduce an existing, already-legitimate claim byte-for-byte
/// -- it cannot forge a new seq/timestamp/event, which is the actual thing CS-15 exists to
/// prevent. The tests below therefore key specifically on the four-parameter constructor,
/// not on any reference to the type.
///
/// Bounded in two layers, tested separately below:
/// 1. Assembly level (<see cref="CS15_InternalsVisibleToGrantIsExactlyIntended"/>): which
///    assemblies could even attempt the call. C# access control already makes this a
///    compiler error for anyone not on the list, so this test exists only to catch the
///    list itself growing without a corresponding decision being made here too.
/// 2. Type level (<see cref="CS15_AppendedEventConstructedOnlyByIntendedTypes"/>): among
///    the assemblies the first test bounds, which types actually call the constructor.
///    This is the layer C# cannot express at all -- internal is an assembly-wide grant --
///    so it is reconstructed here by reading the compiled IL directly with Mono.Cecil
///    (already a transitive dependency of NetArchTest.Rules, which uses it for the same
///    purpose) rather than NetArchTest's own fluent API: NetArchTest's dependency
///    predicates (HaveDependencyOn et al.) operate at the *type* level -- "does type A
///    reference type B anywhere" -- which cannot distinguish "constructs a B" from "has a
///    field/parameter/return type of B," and every legitimate reader of the event store
///    (IEventReader implementations, projections, tests asserting on a returned
///    AppendedEvent) necessarily has the latter. Only a direct IL instruction scan --
///    specifically, an newobj opcode whose operand is AppendedEvent's four-argument
///    constructor -- can express "constructs," so that is what this file does by hand.
///
/// <b>What this cannot close:</b> reflection. <c>Activator.CreateInstance</c> or
/// <c>ConstructorInfo.Invoke</c> with <c>BindingFlags.NonPublic</c> bypasses C#
/// accessibility (and therefore InternalsVisibleTo) entirely in modern .NET -- there is no
/// CAS-era "ReflectionPermission" gate left to stop it, from any assembly, not only the
/// three this file inspects. Detecting that statically in general is equivalent to solving
/// the halting problem for string-typed inputs (`Type.GetType(someComputedString)` defeats
/// any IL-pattern scan), so no rule below attempts it; see the Stage 4 report for why this
/// is reported as a known residual gap rather than silently ignored.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement ID (CS-15) they enforce verbatim (mirrors " +
        "LayeringTests' CS-6/CS-7 precedent).")]
public sealed class EventStoreWriteSurfaceTests
{
    /// <summary>
    /// The only types this suite currently intends to be able to construct a "real"
    /// AppendedEvent: Curia.Domain.Tests exercises the type's shape directly (there is no
    /// production code inside Curia.Domain itself that constructs one), Curia.Application.Tests'
    /// in-memory IEventStore adapter is where Stage 1 placed the R11.4 fake, and
    /// Curia.Infrastructure.PostgresEventStore is Stage 2's real adapter -- the one the R11.4
    /// fake was always standing in for. Changing this set is a deliberate CS-15 decision, not
    /// an incidental side effect of adding a call site -- that is the entire point of pinning
    /// it here. See the Stage 2 report for why adding PostgresEventStore is the correct move
    /// here rather than a quiet widening of the rule: it is the intended production write
    /// surface CS-15 exists to bound, not an exception to it.
    /// </summary>
    private static readonly string[] IntendedWriteSurface =
    [
        "Curia.Domain.Tests.DomainEventTests",
        "Curia.Application.Tests.InMemory.InMemoryEventStore",
        "Curia.Infrastructure.PostgresEventStore",
    ];

    /// <summary>
    /// Layer 1: bounds which assemblies could even attempt to construct an AppendedEvent.
    /// Reads Curia.Domain.csproj's own &lt;InternalsVisibleTo&gt; items directly (same
    /// idiom as LayeringTests.CS6_CanonReferencesNoPackage), rather than reflecting over
    /// the built assembly's InternalsVisibleToAttribute list, for the same reason that
    /// test reads the csproj: the project file is where a violation would first be
    /// introduced, and reading it does not depend on a restore/build having already run.
    /// </summary>
    [Fact]
    public void CS15_InternalsVisibleToGrantIsExactlyIntended()
    {
        var csprojPath = FindProjectFile("Curia.Domain");
        var actual = XDocument.Load(csprojPath)
            .Descendants("InternalsVisibleTo")
            .Select(e => e.Attribute("Include")?.Value ?? "(unnamed)")
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        var expected = new[] { "Curia.Application.Tests", "Curia.Domain.Tests", "Curia.Infrastructure" };

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// AppendedEvent's persisting constructor must stay <see langword="internal"/>, not
    /// <see langword="public"/> -- if it were public, the two tests bounding "which
    /// assembly/type" could still both pass while any assembly in the solution (or a
    /// future one) constructed the type freely. GetConstructors with BindingFlags.NonPublic
    /// works here without needing InternalsVisibleTo itself: reflection *metadata
    /// discovery* (as opposed to *invocation*) bypasses accessibility for any caller, which
    /// is exactly why this check is cheap to write and also exactly why it cannot be relied
    /// on to stop reflection-based construction -- see the class remarks.
    /// </summary>
    [Fact]
    public void CS15_AppendedEventPersistingConstructorStaysInternal()
    {
        var ctors = typeof(Curia.Domain.AppendedEvent).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var persistingCtor = Assert.Single(ctors, c => c.GetParameters().Length == 4);

        Assert.False(persistingCtor.IsPublic,
            "AppendedEvent's four-argument constructor must not be public (CS-15).");
        Assert.True(persistingCtor.IsAssembly,
            "AppendedEvent's four-argument constructor must be internal, not private/protected (CS-15) -- " +
            "Curia.Domain.Tests and Curia.Application.Tests need assembly-level access to it.");
    }

    /// <summary>
    /// Layer 2: among the assemblies InternalsVisibleTo bounds (Curia.Domain.Tests,
    /// Curia.Application.Tests) plus the declaring assembly itself (Curia.Domain, whose own
    /// code can call its own internal members without any grant), walks every method body
    /// for a newobj instruction targeting AppendedEvent's four-argument constructor and
    /// asserts the set of types containing one is exactly <see cref="IntendedWriteSurface"/>
    /// -- no more (a new caller anywhere fails the build), no less (if the intended adapter
    /// stopped constructing one, that is also worth knowing).
    ///
    /// Records the *root* declaring type of each call site (walking up through
    /// DeclaringType), not the immediate one, so a call hidden inside a lambda or local
    /// function -- which the compiler lowers into a nested display-class type -- is
    /// attributed to the outer type a reader actually wrote, not an anonymous compiler
    /// artifact.
    ///
    /// Deliberately excludes the record's own compiler-generated copy constructor (the
    /// single-parameter `AppendedEvent(AppendedEvent original)` `with { }` calls, which
    /// Roslyn emits as a private member of AppendedEvent itself) by matching on parameter
    /// count: only the four-argument constructor is "the write surface" per this file's
    /// class-level definition, because the copy constructor cannot fabricate new content
    /// (see the class remarks).
    /// </summary>
    [Fact]
    public void CS15_AppendedEventConstructedOnlyByIntendedTypes()
    {
        var domainAssemblyPath = typeof(Curia.Domain.DomainEvent).Assembly.Location;
        var repoRoot = FindRepoRoot();

        var assemblyPaths = new[]
        {
            domainAssemblyPath,
            FindSiblingBuiltAssembly(repoRoot, domainAssemblyPath, "tests", "Curia.Domain.Tests"),
            FindSiblingBuiltAssembly(repoRoot, domainAssemblyPath, "tests", "Curia.Application.Tests"),
            FindSiblingBuiltAssembly(repoRoot, domainAssemblyPath, "src", "Curia.Infrastructure"),
        };

        var foundCallers = new SortedSet<string>(StringComparer.Ordinal);
        var offenders = new List<string>();

        foreach (var path in assemblyPaths)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(path);

            foreach (var type in AllTypes(assembly.MainModule))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                        continue;

                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode != OpCodes.Newobj)
                            continue;

                        if (instruction.Operand is not MethodReference ctorRef)
                            continue;

                        if (ctorRef.Name != ".ctor" ||
                            ctorRef.DeclaringType.FullName != "Curia.Domain.AppendedEvent" ||
                            ctorRef.Parameters.Count != 4)
                            continue;

                        var caller = RootType(type).FullName;
                        foundCallers.Add(caller);

                        if (!IntendedWriteSurface.Contains(caller, StringComparer.Ordinal))
                            offenders.Add($"{caller}.{method.Name} in {Path.GetFileName(path)}");
                    }
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "AppendedEvent's persisting constructor was called outside the intended write surface (CS-15): " +
            string.Join(", ", offenders));

        // Guards against the scan silently matching nothing (a passing "found zero call
        // sites" result would mean the IL walk itself is broken, not that CS-15 holds).
        Assert.NotEmpty(foundCallers);
        Assert.Equal(
            IntendedWriteSurface.OrderBy(x => x, StringComparer.Ordinal),
            foundCallers);
    }

    private static TypeDefinition RootType(TypeDefinition type)
    {
        while (type.DeclaringType is not null)
            type = type.DeclaringType;
        return type;
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

    /// <summary>
    /// Curia.Domain.Tests and Curia.Application.Tests are deliberately *not*
    /// ProjectReferences of Curia.Architecture.Tests (referencing one test project from
    /// another is not this solution's idiom, and would pull each one's full xunit runner
    /// closure along for no reason). Instead, locates their built output on disk relative
    /// to a build artifact this project *does* legitimately have a reference to --
    /// Curia.Domain.dll's own real location -- so the configuration/TFM folder names
    /// (Debug vs. Release, in case that ever changes) are read from an authoritative
    /// source rather than assumed to match Curia.Architecture.Tests' own output layout.
    /// </summary>
    private static string FindSiblingBuiltAssembly(
        string repoRoot, string domainAssemblyPath, string topLevelDir, string projectName)
    {
        var tfmDir = Path.GetDirectoryName(domainAssemblyPath)
            ?? throw new InvalidOperationException($"Could not determine the directory of {domainAssemblyPath}.");
        var configDir = Path.GetDirectoryName(tfmDir)
            ?? throw new InvalidOperationException($"Could not determine the parent directory of {tfmDir}.");
        var tfm = Path.GetFileName(tfmDir);
        var config = Path.GetFileName(configDir);

        var path = Path.Combine(repoRoot, topLevelDir, projectName, "bin", config, tfm, projectName + ".dll");

        return File.Exists(path)
            ? path
            : throw new InvalidOperationException(
                $"Expected {projectName}.dll at {path} -- run `dotnet build Curia.sln` (the whole solution, " +
                "not just Curia.Architecture.Tests) so every assembly this test inspects is actually built.");
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the repo root (the first
    /// ancestor containing a `src` directory) -- the same idiom LayeringTests.FindProjectFile
    /// and Curia.Canon.Tests.Vectors.VectorLoader.FindRoot already use in this solution.
    /// </summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not find repo root (a 'src' directory) above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// Mirrors LayeringTests.FindProjectFile (duplicated rather than shared: each file in
    /// this suite is self-contained, matching how BannedApiTests does not share code with
    /// LayeringTests either).
    /// </summary>
    private static string FindProjectFile(string projectName)
    {
        var repoRoot = FindRepoRoot();
        return Path.Combine(repoRoot, "src", projectName, projectName + ".csproj");
    }
}
