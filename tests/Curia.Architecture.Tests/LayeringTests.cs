using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Xml.Linq;
using NetArchTest.Rules;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Makes the hexagon's layering a build-time fact rather than a diagram. CS-6: Canon is
/// the pure domain core and references no package, not even its own crypto adapter's
/// dependency. CS-7: the adapter (Curia.Canon.Sodium) may depend on Canon, but Canon may
/// never depend back on the adapter -- the dependency direction points one way, inward.
/// The same CS-7 dependency direction extends to Curia.Domain (BCL + Canon +
/// Domain.Primitives only) and Curia.Application (Domain + Canon + Domain.Primitives,
/// never Infrastructure or a host project) as those projects have landed.
///
/// Increment 3/Stage 4 still has no Curia.Infrastructure or host project (Api, Issuer,
/// Gateway, Mcp), so the rules guarding against Curia.Application depending on them, and
/// against anything outside Infrastructure linking Npgsql/NSec/OpenIddict/ONNX, cannot be
/// tripped by anything that exists on disk today. They are written anyway (see each
/// test's remarks for why that is not vacuous) so the day Curia.Infrastructure's project
/// file is added, a stray `&lt;ProjectReference&gt;` or `&lt;PackageReference&gt;` fails
/// the build immediately rather than waiting for someone to notice in review.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs (CS-6, CS-7) they enforce verbatim, so a " +
        "reader can trace a failing test straight to the spec obligation without a second naming " +
        "scheme to translate through (mirrors CanonProperties' P1-P5 precedent).")]
public sealed class LayeringTests
{
    private static Assembly Canon => typeof(Curia.Canon.Canonical.CanonicalJson).Assembly;
    private static Assembly Sodium => typeof(Curia.Canon.Sodium.Ed25519Adapter).Assembly;
    private static Assembly Domain => typeof(Curia.Domain.DomainEvent).Assembly;
    private static Assembly Application => typeof(Curia.Application.Ports.IEventStore).Assembly;
    private static Assembly AuthN => typeof(Curia.AuthN.AccessTokenClaims).Assembly;

    /// <summary>
    /// Every production assembly currently in play, paired with the name NetArchTest/
    /// reflection reports for it. Used by the cross-cutting native/host-package pin below
    /// so that adding a sixth hexagon project only means adding one line here rather than
    /// a new test.
    /// </summary>
    private static (string Name, Assembly Assembly)[] HexagonAssemblies =>
    [
        ("Curia.Canon", Canon),
        ("Curia.Canon.Sodium", Sodium),
        ("Curia.Domain.Primitives", typeof(Curia.Domain.Primitives.Result<>).Assembly),
        ("Curia.Domain", Domain),
        ("Curia.Application", Application),
        ("Curia.AuthN", AuthN),
    ];

    /// <summary>
    /// CS-7: "Nothing outside Infrastructure references Npgsql, NSec, OpenIddict, or ONNX types."
    ///
    /// <para><b>This rule was stated, violated, and green.</b> The cross-cutting native-package check
    /// iterates <see cref="HexagonAssemblies"/>, which excludes host projects on the reasonable
    /// ground that a composition root may reference Infrastructure — but "may reference
    /// Infrastructure" is not "may reference Npgsql", and <c>Curia.Api</c> was constructing an
    /// <c>NpgsqlDataSource</c> directly. Found during review of the durability work, not by a
    /// test, which is the argument for this one existing.</para>
    ///
    /// <para>The fix was <c>PostgresAdapters</c>: a host hands over a connection string and receives
    /// ports. A composition root has to wire adapters; it does not have to know what they are made
    /// of.</para>
    /// </summary>
    [Fact]
    public void CS7_HostProjectsDoNotNameDatabaseOrCryptoTypes()
    {
        // Checked over source rather than IL, deliberately. A host project acquires Npgsql
        // *transitively* through Curia.Infrastructure, which is legitimate and unavoidable, so an
        // assembly-reference check would either fire on every host forever or have to allow the
        // package and thereby allow the misuse. What CS-7 forbids is the host *naming* the type.
        //
        // The scoping document says of its own R6.12 CI rule that a grep-gate is "crude, and honest
        // about being crude, which beats a sophisticated check nobody wrote". Same reasoning here,
        // and the same honesty: this catches a `using`, not a fully-qualified reference buried
        // mid-expression. It would have caught the violation that prompted it.
        string[] forbidden = ["Npgsql", "NSec", "OpenIddict", "Microsoft.ML.OnnxRuntime"];
        string[] hostProjects = ["Curia.Api", "Curia.Issuer", "Curia.Gateway", "Curia.Mcp"];

        var repoRoot = FindSourceRoot();
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var host in hostProjects)
        {
            var directory = Path.Combine(repoRoot, host);
            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                    continue;

                scanned++;

                foreach (var line in File.ReadLines(file))
                {
                    var trimmed = line.TrimStart();
                    if (!trimmed.StartsWith("using ", StringComparison.Ordinal)) continue;

                    foreach (var package in forbidden)
                        if (trimmed.StartsWith($"using {package}", StringComparison.Ordinal))
                            offenders.Add($"{Path.GetFileName(file)}: {trimmed.TrimEnd()}");
                }
            }
        }

        // Not vacuous: a scan that found no files would pass while checking nothing, which is the
        // failure mode this whole suite exists to make impossible.
        Assert.True(scanned > 0, "No host-project source was scanned; the rule checked nothing.");

        Assert.True(
            offenders.Count == 0,
            "A host project names a type CS-7 confines to Curia.Infrastructure. A composition root "
            + "wires adapters; it does not name what they are made of -- see PostgresAdapters. "
            + "Offenders: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// CS-6: "Curia.Canon SHALL reference no package." Reads Curia.Canon.csproj's own
    /// declared &lt;PackageReference&gt; items directly, rather than reflecting over the
    /// built assembly's AssemblyRef table the way CS6_OnlySodiumReferencesNativeCrypto and
    /// CS7_CanonDoesNotDependOnSodium below still do (via Assembly.GetReferencedAssemblies
    /// and NetArchTest, both of which walk compiled IL, not project files).
    ///
    /// Those two checks are the right tool for their own questions ("does compiled Canon
    /// code actually use NSec" / "does compiled Canon code actually depend on Sodium
    /// types"), but an assembly-reflection approach is the wrong tool for THIS
    /// requirement: the C# compiler only emits an AssemblyRef for a package whose types
    /// are actually referenced somewhere in the IL. A &lt;PackageReference&gt; added to
    /// Curia.Canon.csproj but never used by any code -- exactly the "sits in the project
    /// file indefinitely, picked up by SBOM tooling and the supply-chain surface, while
    /// this test stays green" failure mode CS-6 exists to catch -- leaves no AssemblyRef
    /// behind at all, so the original Assembly.GetReferencedAssemblies()-based version of
    /// this test could never detect it (confirmed: see the Task 10 fix report's
    /// falsification evidence).
    ///
    /// The project file is also the more direct, authoritative source for this specific
    /// requirement: "Curia.Canon references no package" is a statement about what
    /// Curia.Canon.csproj declares, and that file is where a violation would first be
    /// introduced. packages.lock.json would work too (it is committed per project and
    /// distinguishes direct from transitive dependencies), but it is a restore-generated
    /// artifact one step removed from the csproj, so reading the csproj directly avoids
    /// depending on restore having already run to reflect a just-added reference.
    /// </summary>
    [Fact]
    public void CS6_CanonReferencesNoPackage()
    {
        var csprojPath = FindProjectFile("Curia.Canon");
        var offenders = XDocument.Load(csprojPath)
            .Descendants("PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? "(unnamed)")
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"Curia.Canon must reference no package (CS-6). Found in {csprojPath}: " + string.Join(", ", offenders));
    }

    [Fact]
    public void CS6_OnlySodiumReferencesNativeCrypto()
    {
        Assert.DoesNotContain(Canon.GetReferencedAssemblies(), a => a.Name == "NSec.Cryptography");
        Assert.Contains(Sodium.GetReferencedAssemblies(), a => a.Name == "NSec.Cryptography");
    }

    [Fact]
    public void CS7_CanonDoesNotDependOnSodium()
    {
        var result = Types.InAssembly(Canon)
            .Should().NotHaveDependencyOn("Curia.Canon.Sodium")
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Canon must not depend on its adapter (CS-7). Offenders: " +
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    /// <summary>
    /// CS-7 / R11.1-R11.2: "Curia.Domain SHALL depend on nothing outside the BCL" plus its
    /// two explicitly sanctioned siblings, Curia.Canon (the canonicalization/JSON types
    /// DomainEvent's payload is built from) and Curia.Domain.Primitives (Result, the
    /// strongly typed ID base machinery). An allow-list (OnlyHaveDependenciesOn) rather
    /// than a deny-list on today's known offenders (Npgsql, etc. -- see below) is the
    /// right shape for this specific rule, because the thing CS-7 forbids here is
    /// unbounded ("anything beyond the BCL, Canon, and Domain.Primitives -- in particular
    /// any third-party package"): a deny-list can only ever name packages someone already
    /// thought of, so it would silently miss the next one; an allow-list fails on anything
    /// not already vetted, including a package nobody has heard of yet.
    ///
    /// Not vacuous: <c>Types.InAssembly(Domain)</c> selects Curia.Domain's real, non-empty
    /// type set (DomainEvent, AppendedEvent, EventId, EventSequence, ...) -- asserted
    /// below via <see cref="Types.GetTypes"/> -- and the allow-list was
    /// calibrated against Curia.Domain.dll's actual TypeReference table (Curia.Canon.Json,
    /// Curia.Domain.Primitives, and the System.* namespaces the compiler itself emits for
    /// records/nullable annotations), not guessed.
    /// </summary>
    [Fact]
    public void CS7_DomainOnlyDependsOnBclCanonAndDomainPrimitives()
    {
        // Compiler-emitted types are excluded, and the exclusion is precise rather than convenient.
        // Roslyn emits `<>z__ReadOnlyArray<T>` for collection expressions and
        // `<PrivateImplementationDetails>` for data blobs; both sit in the global namespace, so an
        // allow-list keyed on namespace prefixes reports them the moment any Curia.Domain file uses
        // `[...]` syntax. They are artifacts of how the compiler lowered code that was already
        // written, not dependencies anyone took -- no package reference can arrive this way.
        //
        // Filtered by the leading `<`, which is not a legal character in a C# identifier: a type
        // named this way is necessarily compiler-emitted, so this cannot accidentally excuse
        // anything a person wrote. The rule keeps its teeth -- a hand-written type in the global
        // namespace is still an offender, and so is any type reaching a namespace outside the list.
        var scanned = Types.InAssembly(Domain).GetTypes();
        Assert.NotEmpty(scanned); // guards against the predicate silently matching nothing

        var result = Types.InAssembly(Domain)
            .Should().OnlyHaveDependenciesOn(
                "System",
                "Curia.Domain",
                "Curia.Canon",
                "Curia.Domain.Primitives")
            .GetResult();

        var offenders = HandWrittenOffenders(result);
        Assert.True(offenders.Length == 0,
            "Curia.Domain must depend on nothing beyond the BCL, Curia.Canon, and " +
            "Curia.Domain.Primitives (CS-7, R11.1-R11.2). Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// CS-7: "Application → Domain + Canon, never Infrastructure." Curia.Application is
    /// allowed to see Curia.Canon.Json (DomainEvent's payload type flows through the port
    /// signatures) alongside Curia.Domain and Curia.Domain.Primitives, but nothing that
    /// would name a future Curia.Infrastructure or host project (Api, Issuer, Gateway,
    /// Mcp).
    ///
    /// Not vacuous in the "predicate matched zero types" sense the brief warns about:
    /// <c>Types.InAssembly(Application)</c> selects Curia.Application's real, non-empty
    /// type set (IEventReader, IEventStore) -- asserted below. It IS vacuous in a
    /// different, unavoidable sense: none of Curia.Infrastructure/Api/Issuer/Gateway/Mcp
    /// exist yet, so there is nothing on disk this rule could currently find a dependency
    /// on, and no code change today can make it fail. That is the exact case the brief
    /// says is still worth writing -- the day a Curia.Infrastructure project file appears
    /// and Curia.Application.csproj gains a ProjectReference to it, this test starts
    /// failing without anyone having to remember to add it then.
    /// </summary>
    [Fact]
    public void CS7_ApplicationDoesNotDependOnInfrastructureOrHostProjects()
    {
        var scanned = Types.InAssembly(Application).GetTypes();
        Assert.NotEmpty(scanned); // guards against the predicate silently matching nothing

        var result = Types.InAssembly(Application)
            .Should().OnlyHaveDependenciesOn(
                "System",
                "Curia.Domain",
                "Curia.Canon",
                "Curia.Domain.Primitives",
                "Curia.Application")
            .GetResult();

        var offenders = HandWrittenOffenders(result);
        Assert.True(offenders.Length == 0,
            "Curia.Application must depend on nothing beyond Domain, Canon, " +
            "Domain.Primitives, and the BCL -- in particular never Infrastructure or a " +
            "host project (CS-7). Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// CS-6 / CS-7: "Nothing outside Infrastructure references Npgsql, NSec, OpenIddict,
    /// or ONNX types. Curia.Canon.Sodium is the only assembly permitted to link native
    /// crypto." Reflects over every currently-built hexagon assembly's AssemblyRef table
    /// (the same technique CS6_OnlySodiumReferencesNativeCrypto already uses for Canon
    /// specifically) rather than NetArchTest, and widens the check to all five: the two
    /// existing NetArchTest-based tests above are typed against namespaces used in IL, and
    /// while that generalizes cleanly to "Domain must not reference Curia.Infrastructure's
    /// namespace," it does not generalize as cleanly to "must not reference a *package*"
    /// -- GetReferencedAssemblies is the direct, unambiguous way to ask "does this
    /// assembly's manifest name a dependency on assembly X," independent of whatever
    /// namespace that package happens to use.
    ///
    /// Prefix-matches OpenIddict and the ONNX runtime package family (both ship as several
    /// assemblies -- OpenIddict.Abstractions, OpenIddict.Server, Microsoft.ML.OnnxRuntime,
    /// Microsoft.ML.OnnxRuntime.Managed, ...) and exact-matches Npgsql and NSec.Cryptography
    /// (single assemblies today), mirroring CS6_OnlySodiumReferencesNativeCrypto's own
    /// exact-match for NSec.
    ///
    /// Not vacuous for the NSec/Sodium half: Curia.Canon.Sodium really does reference
    /// NSec.Cryptography today, and the other four assemblies really do not -- a live,
    /// two-sided check. Vacuous by necessity for Npgsql/OpenIddict/ONNX (no project in the
    /// solution references any of them yet, so this half of the check cannot currently
    /// fail) for the same reason CS7_ApplicationDoesNotDependOnInfrastructureOrHostProjects
    /// is: there is nothing to violate it with until Curia.Infrastructure exists. The scan
    /// itself is proven non-trivial regardless, via the per-assembly
    /// Assert.NotEmpty(refs) below -- each of the five really does have a non-empty
    /// AssemblyRef table (e.g. Curia.Application really does reference Curia.Domain), so
    /// this is not a scan that would stay green even if it were silently scanning nothing.
    /// </summary>
    [Fact]
    public void CS6_CS7_OnlySodiumLinksNativeCryptoAndNothingOutsideInfrastructureLinksHostPackages()
    {
        var offenders = new List<string>();

        foreach (var (name, assembly) in HexagonAssemblies)
        {
            var refs = assembly.GetReferencedAssemblies().Select(a => a.Name ?? "(unnamed)").ToArray();
            Assert.NotEmpty(refs); // guards against silently scanning an assembly with no references at all

            var isSodium = name == "Curia.Canon.Sodium";

            if (refs.Any(r => r.Equals("NSec.Cryptography", StringComparison.Ordinal)) && !isSodium)
                offenders.Add($"{name} references NSec.Cryptography (CS-6: only Curia.Canon.Sodium may)");

            if (refs.Any(r => r.Equals("Npgsql", StringComparison.Ordinal)))
                offenders.Add($"{name} references Npgsql (CS-7: Infrastructure-only)");

            if (refs.Any(r => r.StartsWith("OpenIddict", StringComparison.Ordinal)))
                offenders.Add($"{name} references OpenIddict (CS-7: Infrastructure-only)");

            if (refs.Any(r => r.StartsWith("Microsoft.ML.OnnxRuntime", StringComparison.Ordinal)))
                offenders.Add($"{name} references the ONNX runtime (CS-7: Infrastructure-only)");
        }

        Assert.True(offenders.Count == 0,
            "Native crypto / host-package linkage escaped its intended assembly: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// Walks up from the test assembly's output directory to the repo root (the first
    /// ancestor containing a `src` directory), mirroring the same path-discovery idiom
    /// Curia.Canon.Tests.Vectors.VectorLoader.FindRoot already uses in this solution to
    /// locate `conformance/` without a hardcoded absolute or excessively relative path.
    /// </summary>
    /// <summary>
    /// The offenders a dependency rule reported, minus the ones the compiler emitted.
    ///
    /// <para>Roslyn lowers collection-expression syntax (<c>[...]</c>) into types such as
    /// <c>&lt;&gt;z__ReadOnlyArray`1</c> and <c>&lt;PrivateImplementationDetails&gt;</c>, which
    /// live in the global namespace and so fail any namespace-prefix allow-list the moment a file
    /// uses that syntax. They are artifacts of how already-written code was lowered, not
    /// dependencies anyone took -- no package reference can arrive this way.</para>
    ///
    /// <para>Filtered on <c>&lt;</c> appearing anywhere in the reported name rather than only at
    /// the start, because a compiler-emitted type's nested members report as
    /// <c>&lt;&gt;z__ReadOnlySingleElementList`1/Enumerator</c> -- whose own name is perfectly
    /// legal. <c>&lt;</c> is not a valid C# identifier character anywhere, so nothing hand-written
    /// can be excused by this, and a real offender still fails.</para>
    /// </summary>
    private static string[] HandWrittenOffenders(NetArchTest.Rules.TestResult result) =>
        (result.FailingTypeNames ?? [])
            .Where(name => !name.Contains('<', StringComparison.Ordinal))
            .ToArray();

    /// <summary>The repository's <c>src</c> directory, found by walking up from the test binary.</summary>
    private static string FindSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException("Could not find a 'src' directory above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "src");
    }

    private static string FindProjectFile(string projectName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir is null
            ? throw new InvalidOperationException("Could not find repo root (a 'src' directory) above " + AppContext.BaseDirectory)
            : Path.Combine(dir.FullName, "src", projectName, projectName + ".csproj");
    }
    /// <summary>
    /// CS-7: <c>Curia.AuthN</c> SHALL NOT depend on <c>Curia.Domain</c>.
    /// </summary>
    /// <remarks>
    /// This layering is not incidental — it is what forced <c>ServerTimestamp</c> down into
    /// <c>Curia.Domain.Primitives</c> during T1.2, because that is the only project both
    /// <c>Curia.AuthN</c> and <c>Curia.Domain</c> can see, and CS-5 forbids solving it by
    /// sharing internals ("sharing internals is how hexagonal seams rot").
    ///
    /// It was previously enforced only by the absence of a &lt;ProjectReference&gt; — real,
    /// but a different mechanism than an architecture test, and one that fails silently the
    /// moment someone adds the reference to make a compile error go away. A verification pass
    /// flagged the gap; this closes it.
    /// </remarks>
    [Fact]
    public void CS7_AuthNDoesNotDependOnDomain()
    {
        // Two checks, because either alone is too weak.
        //
        // The compiled AssemblyRef table only lists an assembly whose types are actually
        // *used*, so an unused <ProjectReference> to Curia.Domain would not appear here at
        // all — a falsification attempt during review confirmed exactly that, and the
        // reference-only version of this test passed while the forbidden reference sat in
        // the csproj. Reading the project file catches the declaration; reflecting over the
        // assembly catches the usage. A breach needs both to be absent to go unnoticed, and
        // it cannot be.
        var csprojPath = FindProjectFile("Curia.AuthN");
        var declared = XDocument.Load(csprojPath)
            .Descendants("ProjectReference")
            .Select(e => (e.Attribute("Include")?.Value ?? string.Empty).Replace('\\', '/'))
            .ToArray();
        Assert.NotEmpty(declared); // guards against a csproj with no references at all
        Assert.DoesNotContain(declared, r => r.EndsWith("/Curia.Domain.csproj", StringComparison.Ordinal));

        var refs = AuthN.GetReferencedAssemblies().Select(a => a.Name ?? "(unnamed)").ToArray();
        Assert.NotEmpty(refs); // guards against silently scanning an assembly with no references
        Assert.DoesNotContain("Curia.Domain", refs);

        // Curia.Domain.Primitives is the shared floor and is expressly allowed — asserting it
        // is present keeps this test from passing for the wrong reason if AuthN's references
        // were ever gutted entirely.
        Assert.Contains("Curia.Domain.Primitives", refs);
    }
}
