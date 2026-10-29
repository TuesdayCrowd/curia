using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using Curia.Application.Ports;
using Curia.Infrastructure;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R8.77 and R9.47 (errata G45) as fences: a gate must not become a second write path (RVO26 to RVO28, CS-15's spirit), its string parameter has a
/// reasoned row (RVO29), the route's successor check goes through <c>CorrectionIndex.For</c> and no second "served" filter (RVO30), and no structure
/// in <c>src/</c> is keyed by a digest in a way that throws when it repeats (RVO43).
///
/// <para>RVO43 is a lint on source text and says so: it carries no behaviour, which RVO40 to RVO42 and RVO52 carry. RVO26, RVO27 and RVO29 are green on
/// the seam PR 1's Task 0 declared (the port is right and its adapter writes nothing); they carry information under FRVO31 and FRVO33.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class RevisionGateFenceTests
{
    /// <summary>Asserts two descriptions are equal and says both in full when they are not: xUnit shortens a long string's difference, and a red text is what these probes leave behind.</summary>
    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private const string RevisionGate = "Curia.Application.Ports.IRevisionGate";
    private const string ForumEndpointsType = "Curia.Api.ForumEndpoints";

    private static string AssemblyOf(string name) => Path.Combine(AppContext.BaseDirectory, name + ".dll");

    /// <summary>RVO26. The port declares one member, <c>TryEnterAsync</c>, returning <c>Task&lt;Result&lt;IAsyncDisposable&gt;&gt;</c>: it cannot grow an append.</summary>
    [Fact]
    public void R8_77_TheGateDeclaresExactlyOneMemberAndItReturnsAHold()
    {
        var members = typeof(IRevisionGate).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m is MethodInfo method ? $"{method.Name}: {method.ReturnType.Name}<{string.Join(",", method.ReturnType.GetGenericArguments().Select(a => a.Name + "<" + string.Join(",", a.GetGenericArguments().Select(b => b.Name)) + ">"))}>" : m.Name)
            .ToArray();

        Assert.Equal(["TryEnterAsync: Task`1<Result`1<IAsyncDisposable>>"], members);
        Assert.Equal(typeof(Task<Curia.Domain.Primitives.Result<IAsyncDisposable>>), typeof(IRevisionGate).GetMethod("TryEnterAsync")!.ReturnType);
    }

    private static IEnumerable<Type> Implementers() =>
        Shipped.ShippedAssemblies()
            .Select(Assembly.LoadFrom)
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsAbstract && !t.IsInterface && typeof(IRevisionGate).IsAssignableFrom(t));

    /// <summary>RVO27. No type implementing the gate references the event store or any of its members, by constructor, field or call.</summary>
    [Fact]
    public void R8_77_NoImplementerOfTheGateReachesTheEventStore()
    {
        var implementers = Implementers().ToArray();
        Assert.Contains(typeof(PostgresRevisionGate), implementers);

        var eventStore = new[] { typeof(IEventStore), typeof(IEventReader) };
        var reaching = new List<string>();
        foreach (var type in implementers)
        {
            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            if (type.GetConstructors(All).SelectMany(c => c.GetParameters()).Any(p => eventStore.Any(e => e.IsAssignableFrom(p.ParameterType))))
                reaching.Add($"{type.FullName}: a constructor takes the event store");

            if (type.GetFields(All).Any(f => eventStore.Any(e => e.IsAssignableFrom(f.FieldType))))
                reaching.Add($"{type.FullName}: a field holds the event store");

            var path = AssemblyOf(type.Assembly.GetName().Name!);
            foreach (var site in IlCallSites.CallSites(path).Where(s => s.CallerType == type.FullName))
            {
                if (eventStore.Any(e => string.Equals(site.CalleeType, e.FullName, StringComparison.Ordinal)))
                    reaching.Add($"{type.FullName}.{site.CallerMethod} calls {site.CalleeType}::{site.CalleeMember}");
            }
        }

        Assert.Empty(reaching);
    }

    /// <summary>RVO28. The only caller of <c>IRevisionGate.TryEnterAsync</c> in <c>src/</c> is the submission attempt: not the accept route, not a read.</summary>
    [Fact]
    public void R8_77_TheOnlyCallerOfTheGateIsTheSubmissionAttempt()
    {
        var callers = Shipped.ShippedAssemblies()
            .SelectMany(path => IlCallSites.CallSites(path))
            .Where(s => s.CalleeType == RevisionGate && s.CalleeMember == "TryEnterAsync")
            .Select(s => $"{s.CallerType}.{s.CallerMethod}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Said($"[{ForumEndpointsType}.SubmitAttemptAsync]", $"[{string.Join(", ", callers)}]");
    }

    /// <summary>RVO29. <c>PortStringAllowlist</c> carries a reasoned row for the gate's string parameter; <c>PortStringFenceTests</c>' stale-row rule fails if the port stops taking the string.</summary>
    [Fact]
    public void R8_77_TheGatesStringParameterHasAReasonedRow()
    {
        var rows = PortStringAllowlist.Rows.Where(r => r.Member == RevisionGate + ".TryEnterAsync" && r.Parameter == "prevDigest").ToArray();

        var row = Assert.Single(rows);
        Assert.False(string.IsNullOrWhiteSpace(row.Reason));
        Assert.Contains(typeof(IRevisionGate).GetMethod("TryEnterAsync")!.GetParameters(), p => p.Name == "prevDigest" && p.ParameterType == typeof(string));
    }

    /// <summary>
    /// RVO30. The successor check goes through <c>CorrectionIndex.For</c> and no second "served" filter. The check is the submission attempt, or a
    /// <c>ForumEndpoints</c> method it calls directly, that calls <c>CorrectionIndex.For</c>; that method refers to neither
    /// <c>PostKinds.IsServedToReaders</c> nor <c>FlagProjector</c> (the one filter is <c>Servable</c>, which the method may call).
    /// </summary>
    [Fact]
    public void R8_77_TheSuccessorCheckGoesThroughTheCorrectionIndexAndNoSecondServedFilter()
    {
        var sites = IlCallSites.CallSites(AssemblyOf("Curia.Api")).Where(s => s.CallerType == ForumEndpointsType).ToArray();
        var attempt = sites.Where(s => s.CallerMethod == "SubmitAttemptAsync").ToArray();
        Assert.NotEmpty(attempt);

        var reachable = attempt.Where(s => s.CalleeType == ForumEndpointsType).Select(s => s.CalleeMember).Append("SubmitAttemptAsync").ToHashSet(StringComparer.Ordinal);
        var checks = sites
            .Where(s => reachable.Contains(s.CallerMethod) && s.CalleeType == "Curia.Application.Projections.CorrectionIndex" && s.CalleeMember == "For")
            .Select(s => s.CallerMethod)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var secondFilters = sites
            .Where(s => checks.Contains(s.CallerMethod)
                && ((s.CalleeType == "Curia.Domain.Content.PostKinds" && s.CalleeMember == "IsServedToReaders")
                    || s.CalleeType == "Curia.Application.Projections.FlagProjector"))
            .Select(s => $"{s.CallerMethod} calls {s.CalleeType}::{s.CalleeMember}")
            .ToArray();

        Said(
            "a submission-attempt method calls CorrectionIndex.For; no second served filter",
            $"{(checks.Length > 0 ? "a submission-attempt method calls CorrectionIndex.For" : "no submission-attempt method calls CorrectionIndex.For")}; " +
            $"{(secondFilters.Length == 0 ? "no second served filter" : string.Join(", ", secondFilters))}");
    }

    /// <summary>
    /// RVO43's reasoned rows: a source line that builds a digest-keyed map in a way that throws on a repeated key, with why it cannot. The set starts
    /// empty: R9.47 resolves a repeated digest to the first post in log order, so every such map uses <c>TryAdd</c> (as <c>CorrectionIndex</c> does).
    /// A row naming a line that is gone fails the fact.
    /// </summary>
    private static readonly ImmutableArray<(string File, string Line, string Reason)> DigestKeyedRows = [];

    private static readonly Regex DigestKeyedMap = new(
        @"\.(ToDictionary|ToImmutableDictionary|ToFrozenDictionary)\([^;]*[Dd]igest|\.Add\(\s*[\w.!]*[Dd]igest\w*\s*,",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    /// <summary>RVO43. A source-text lint: no map in <c>src/</c> is built keyed by a digest with a method that throws on a repeat.</summary>
    [Fact]
    public void R9_47_NoDigestKeyedMapIsBuiltInAWayThatThrowsOnARepeat()
    {
        var src = FindSrc();
        Assert.True(Directory.Exists(src), $"no src directory found from {AppContext.BaseDirectory}");

        var found = new List<(string File, string Line)>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var relative = Path.GetRelativePath(src, file).Replace(Path.DirectorySeparatorChar, '/');
            foreach (var line in File.ReadLines(file).Select(l => l.Trim()).Where(l => !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith("///", StringComparison.Ordinal)))
            {
                if (DigestKeyedMap.IsMatch(line))
                    found.Add((relative, line));
            }
        }

        var stale = DigestKeyedRows.Where(r => !found.Contains((r.File, r.Line))).Select(r => $"stale row: {r.File}: {r.Line}").ToArray();
        var unreasoned = found
            .Where(f => !DigestKeyedRows.Any(r => r.File == f.File && r.Line == f.Line && !string.IsNullOrWhiteSpace(r.Reason)))
            .Select(f => $"{f.File}: {f.Line}")
            .ToArray();

        Said(
            "no digest-keyed map without a reasoned row; no stale row",
            unreasoned.Length == 0 && stale.Length == 0
                ? "no digest-keyed map without a reasoned row; no stale row"
                : string.Join(" | ", unreasoned.Concat(stale)));
    }

    private static string FindSrc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;

        return dir is null ? string.Empty : Path.Combine(dir.FullName, "src");
    }
}
