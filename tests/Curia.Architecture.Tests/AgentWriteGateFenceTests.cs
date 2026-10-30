using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text.RegularExpressions;
using Curia.Application.Budget;
using Curia.Application.Ports;
using Curia.Infrastructure;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R7.27 (errata G48) as fences: the port cannot grow an append or take a string (BRB50), every write path of the Forum is gated and nothing else enters the gate
/// (BRB51), the gate is one instance per host and holds no static state (BRB56), no new probe reads a production constant for an expectation (BRB49), and no
/// document or comment says what R7.27 retired (BRB55). BRB50 and BRB56 are green on the seam PR 1's Task 0 declared (they carry information under FBRB48 and a
/// <c>string</c> parameter); BRB51 and BRB55 are red until the routes enter the gate and the stale sentences go.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AgentWriteGateFenceTests
{
    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    private const string AgentGate = "Curia.Application.Ports.IAgentWriteGate";
    private const string Endpoints = "Curia.Api.ForumEndpoints";

    private static string AssemblyOf(string name) => Path.Combine(AppContext.BaseDirectory, name + ".dll");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>BRB50. The port declares one member, <c>EnterAsync(AgentId, CancellationToken)</c>, returning <c>Task&lt;Result&lt;IAsyncDisposable&gt;&gt;</c>, and no string parameter, so no <c>PortStringAllowlist</c> row exists for it.</summary>
    [Fact]
    public void R7_27_BRB50_TheGateDeclaresExactlyOneMemberAndTakesNoString()
    {
        var members = typeof(IAgentWriteGate).GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Select(m => m is MethodInfo method
                ? $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))}): {method.ReturnType.Name}<{string.Join(",", method.ReturnType.GetGenericArguments().Select(a => a.Name + "<" + string.Join(",", a.GetGenericArguments().Select(b => b.Name)) + ">"))}>"
                : m.Name)
            .ToArray();
        var rows = PortStringAllowlist.Rows.Where(r => r.Member.Contains("IAgentWriteGate", StringComparison.Ordinal)).Select(r => r.Member).ToArray();

        Said(
            "EnterAsync(AgentId, CancellationToken): Task`1<Result`1<IAsyncDisposable>>; no allowlist row",
            $"{string.Join("; ", members)}; {(rows.Length == 0 ? "no allowlist row" : "allowlist rows " + string.Join(", ", rows))}");
    }

    /// <summary>The reasoned rows of BRB51: a method that calls <c>PersistAsync</c> or the accept <c>RecordAsync</c> without the gate, with why it is safe. The set starts empty.</summary>
    private static readonly (string Method, string Reason)[] UngatedWritePaths = [];

    /// <summary>
    /// BRB51. Every write path is gated, data-driven: over <c>Curia.Api</c>'s IL, each caller of <c>IIngestPipeline.PersistAsync</c> and of <c>AcceptAnswer.RecordAsync</c>
    /// is in the call closure of a method that calls <c>IAgentWriteGate.EnterAsync</c>, and <c>EnterAsync</c> is called by those two routes alone (not the flag
    /// route, a read, enrolment), the reasoned allowlist being empty.
    /// </summary>
    [Fact]
    public void R7_27_BRB51_EveryWritePathIsGatedAndNothingElseEntersTheGate()
    {
        var sites = Shipped.ShippedAssemblies().SelectMany(path => IlCallSites.CallSites(path)).ToArray();
        var enterers = sites.Where(s => s.CalleeType == AgentGate && s.CalleeMember == "EnterAsync").Select(s => $"{s.CallerType}.{s.CallerMethod}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        var writers = sites
            .Where(s => (s.CalleeType == "Curia.Application.Ingest.IIngestPipeline" && s.CalleeMember == "PersistAsync")
                || (s.CalleeType == "Curia.Application.Moderation.AcceptAnswer" && s.CalleeMember == "RecordAsync"))
            .Select(s => $"{s.CallerType}.{s.CallerMethod}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var calls = sites.GroupBy(s => $"{s.CallerType}.{s.CallerMethod}").ToDictionary(g => g.Key, g => g.Select(s => $"{s.CalleeType}.{s.CalleeMember}").Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        var closure = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(enterers);
        while (queue.Count > 0)
        {
            var method = queue.Dequeue();
            if (!closure.Add(method) || !calls.TryGetValue(method, out var callees)) continue;
            foreach (var callee in callees.Where(c => calls.ContainsKey(c))) queue.Enqueue(callee);
        }

        var ungated = writers.Where(w => !closure.Contains(w) && !UngatedWritePaths.Any(u => u.Method == w && u.Reason.Length > 0)).ToArray();
        var wanted = new[] { $"{Endpoints}.AcceptAnswerAsync", $"{Endpoints}.SubmitAsync" };

        Said(
            $"[{string.Join(", ", wanted)}]; every write path gated",
            $"[{string.Join(", ", enterers)}]; {(ungated.Length == 0 ? "every write path gated" : "ungated: " + string.Join(", ", ungated))}");
    }

    private static bool IsReadonlyConstant(FieldInfo field) =>
        field.IsLiteral || (field.IsInitOnly && (field.FieldType.IsValueType || field.FieldType == typeof(string)));

    private static IEnumerable<string> StaticStateOf(Type type)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
        foreach (var field in type.GetFields(All).Where(f => !IsReadonlyConstant(f) && f.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null))
            yield return $"{type.FullName}.{field.Name}";

        foreach (var nested in type.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(n => n.GetCustomAttribute<System.Runtime.CompilerServices.CompilerGeneratedAttribute>() is null))
            foreach (var state in StaticStateOf(nested)) yield return state;
    }

    /// <summary>
    /// BRB56. No static state in the gate (a fence): <c>AgentWriteQueue</c>, <c>PostgresAgentWriteGate</c> and the in-memory test adapter hold no static field that is
    /// not a readonly constant. A static queue would let two hosts in one OS process share it and pass BRB22 with the lock leg removed.
    /// </summary>
    [Fact]
    public void R7_27_BRB56_TheGateHoldsNoStaticState()
    {
        var found = StaticStateOf(typeof(AgentWriteQueue)).Concat(StaticStateOf(typeof(PostgresAgentWriteGate))).ToList();

        var inMemory = Path.Combine(RepoRoot(), "tests", "Curia.Application.Tests", "InMemory", "InMemoryAgentWriteGate.cs");
        Assert.True(File.Exists(inMemory), "the in-memory adapter is not where the fence reads it: " + inMemory);
        found.AddRange(File.ReadLines(inMemory)
            .Select(l => l.Trim())
            .Where(l => !l.StartsWith("//", StringComparison.Ordinal) && Regex.IsMatch(l, @"\bstatic\b(?!.*\bconst\b)(?!.*\breadonly\b)", RegexOptions.None, TimeSpan.FromSeconds(5)) && !l.Contains('(', StringComparison.Ordinal))
            .Select(l => "InMemoryAgentWriteGate: " + l));

        Said("no static state", found.Count == 0 ? "no static state" : string.Join(" | ", found));
    }

    private static readonly string[] BannedExpectationSources = ["TierPolicy.", "AccessPolicy.", "AgentWriteGateBounds."];

    /// <summary>
    /// BRB49. No probe reads a production constant for an expectation: a scan of the files this PR adds to the test projects (found by name, the
    /// bound's own pin and this fence excepted) finds none of <c>TierPolicy.</c>, <c>AccessPolicy.</c> or <c>AgentWriteGateBounds.</c>.
    /// </summary>
    [Fact]
    public void R7_27_BRB49_NoProbeReadsAProductionConstantForAnExpectation()
    {
        var tests = Path.Combine(RepoRoot(), "tests");
        var files = Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(Path.GetFileName(f), @"AgentWriteGate|BudgetRefusal|PublishedBudgetGate|ReadmeConnectionString|TimerManualTimeProvider", RegexOptions.None, TimeSpan.FromSeconds(5)))
            .Where(f => !Path.GetFileName(f).Equals("AgentWriteGatePinTests.cs", StringComparison.Ordinal) && !Path.GetFileName(f).Equals("AgentWriteGateFenceTests.cs", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(files);

        var reads = files
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line.TrimStart())))
            .Where(l => !l.Text.StartsWith("//", StringComparison.Ordinal) && !l.Text.StartsWith("///", StringComparison.Ordinal) && BannedExpectationSources.Any(b => l.Text.Contains(b, StringComparison.Ordinal)))
            .Select(l => $"{l.File}:{l.Line}")
            .ToArray();

        Said("no probe reads TierPolicy, AccessPolicy or AgentWriteGateBounds", reads.Length == 0 ? "no probe reads TierPolicy, AccessPolicy or AgentWriteGateBounds" : "read at " + string.Join(", ", reads));
    }

    /// <summary>The sentences R7.27 retired, and why each is false now; the errata and the register are history and are not scanned (G48 quotes the retired sentence on purpose).</summary>
    private static readonly (Regex Pattern, string Reason)[] StaleSentences =
    [
        (new Regex(@"does not yet bind|yet binds", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)), "a budget now binds simultaneous requests (R7.27)"),
        (new Regex(@"count is not gated", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)), "the count is read under the agent's hold (R7.27)"),
        (new Regex(@"eventual gate", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)), "the gate exists (R7.27)"),
        (new Regex(@"overshot by at most the number in flight", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)), "the budget is no longer overshot by a burst (R7.27, register D88)"),
    ];

    /// <summary>BRB55. No stale word (data-driven): no tracked document, help text, tool description or code comment says a budget does not yet bind simultaneous requests, that the count is not gated, or names an eventual gate.</summary>
    [Fact]
    public void R7_27_BRB55_NoDocumentOrCommentSaysWhatR727Retired()
    {
        var root = RepoRoot();
        var scanned = new List<string> { Path.Combine(root, "README.md"), Path.Combine(root, "CLAUDE.md") };
        scanned.AddRange(Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)));

        var hits = scanned.Where(File.Exists)
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetRelativePath(root, f), Line: i + 1, Text: line)))
            .SelectMany(l => StaleSentences.Where(s => s.Pattern.IsMatch(l.Text)).Select(s => $"{l.File}:{l.Line} ({s.Reason})"))
            .ToArray();

        Said("no stale sentence", hits.Length == 0 ? "no stale sentence" : string.Join("; ", hits));
    }
}
