using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Errata G50 as fences, PR 3's Task 0: RLW8 (only the flag listing tells a route's knowledge to <c>Discharge</c>), RLW14 (no form writes its own
/// <c>not served</c>), RLW21 (one author of the acceptance receipt's line), RLW29 (one author of the answer receipt's walk line) and RLW42 (no probe reads a
/// production constant for an expectation). Each reads the IL of the shipped assemblies with Mono.Cecil, or the source for what IL cannot tell.
///
/// <para>On the seam tree RLW8, RLW21 and RLW29 are red by assertion ("Expected: ... Actual: none": the seam declares <c>Discharge</c>'s parameter,
/// <c>AcceptWording.ReceiptKind</c>, <c>QuestionChain.WalkAsync</c> and <c>Supersession.QuestionChainStanding</c> and nothing calls them), RLW14 and RLW42 are
/// green controls (the seam moved the literals behind one constant; no probe reads a constant for an expectation), red under FRLW10 to FRLW12 and FRLW52.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class ReadersLastWordsFenceTests
{
    private static string Dll(string name) => Path.Combine(AppContext.BaseDirectory, name + ".dll");

    private static string Repo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no repository root above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"<(\w+)>", RegexOptions.CultureInvariant)]
    private static partial Regex Compiler();

    /// <summary>The source method an IL method belongs to: an async method's state machine, a lambda and a local function are read as the method that wrote them.</summary>
    private static string EnclosingOf(MethodDefinition method)
    {
        var outer = method.DeclaringType;
        while (outer.DeclaringType is not null) outer = outer.DeclaringType;

        var inMethod = Compiler().Match(method.Name);
        if (inMethod.Success) return $"{outer.FullName}::{inMethod.Groups[1].Value}";

        var inType = method.DeclaringType;
        while (inType is not null)
        {
            var named = Compiler().Match(inType.Name);
            if (named.Success && named.Index == 0 && inType.IsNested) return $"{outer.FullName}::{named.Groups[1].Value}";
            inType = inType.DeclaringType;
        }

        return $"{outer.FullName}::{method.Name}";
    }

    private static IEnumerable<TypeDefinition> AllTypes(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes)
        foreach (var inner in AllTypes(nested))
            yield return inner;
    }

    /// <summary>Every call to a method whose full name contains <paramref name="target"/> in <paramref name="assemblies"/>, with the source method that makes it and the instruction before the call.</summary>
    private static List<(string Site, Instruction? Before)> Calls(string target, params string[] assemblies)
    {
        var found = new List<(string, Instruction?)>();
        foreach (var name in assemblies)
        {
            using var assembly = AssemblyDefinition.ReadAssembly(Dll(name));
            foreach (var top in assembly.MainModule.Types)
            foreach (var type in AllTypes(top))
            foreach (var method in type.Methods.Where(m => m.HasBody))
            foreach (var instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code is not (Code.Call or Code.Callvirt)
                    || instruction.Operand is not MethodReference callee
                    || !callee.FullName.Contains(target, StringComparison.Ordinal))
                    continue;

                found.Add((EnclosingOf(method), instruction.Previous));
            }
        }

        return found;
    }

    private static bool LoadsTrue(Instruction? instruction) => instruction?.OpCode.Code is Code.Ldc_I4_1;

    // ----- RLW8 -----

    /// <summary>
    /// A second site that tells <c>Discharge</c> the resource exists must be a reasoned row that shows the existence check it follows. There is none: the
    /// listing's own check (<c>404</c> for an id no post bears, before the ownership question) is the only one.
    /// </summary>
    private static readonly (string Site, string Reason)[] ReasonedDischargeSites = [];

    /// <summary>
    /// RLW8. Exactly one call site of <c>AuthorizationDecision.Discharge</c> in <c>Curia.Api</c> passes <c>resourceKnownToExist: true</c>, and it is in
    /// <c>ListPostFlagsAsync</c>; every other call passes nothing or false. The default is the hedge, so a route that forgets says too little and never too much.
    /// </summary>
    [Fact]
    public void R7_30_RLW8_OnlyTheFlagListingTellsDischargeTheResourceExists()
    {
        var calls = Calls("AuthorizationDecision::Discharge(System.Boolean", "Curia.Api");
        Assert.True(calls.Count >= 4, $"only {calls.Count} calls of Discharge were read in Curia.Api; the scan is looking in the wrong place");

        var passingTrue = calls.Where(c => LoadsTrue(c.Before)).Select(c => c.Site).Distinct().ToArray();
        var unreasoned = passingTrue.Where(s => !s.EndsWith("::ListPostFlagsAsync", StringComparison.Ordinal) && !ReasonedDischargeSites.Any(r => r.Site == s)).ToArray();

        var problems = new List<string>();
        if (passingTrue.Count(s => s.EndsWith("::ListPostFlagsAsync", StringComparison.Ordinal)) != 1)
            problems.Add($"Expected: one site, in ListPostFlagsAsync, that passes resourceKnownToExist: true Actual: {(passingTrue.Length == 0 ? "none" : string.Join(", ", passingTrue))}");
        if (unreasoned.Length > 0) problems.Add($"a site that is not a reasoned row passes true: {string.Join(", ", unreasoned)}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ----- RLW14 -----

    private static readonly string[] ReaderProjects = ["Curia.Client", "Curia.Client.Cli", "Curia.Mcp"];

    /// <summary>The assemblies those projects build: <c>curia</c> and <c>curia-mcp</c> are named by their <c>AssemblyName</c>.</summary>
    private static readonly string[] Readers = ["Curia.Client", "curia", "curia-mcp"];

    /// <summary>
    /// The files whose lines may write the text <c>not served</c>, each with its reason: the one definition (<c>Supersession.cs</c>), a log index that is not served
    /// (a different meaning), a flag receipt's sentence about <c>post_served</c> (its own statement), and the prose of the tool descriptions and the help, which RLW15
    /// holds to carry the definition sentence.
    /// </summary>
    private static readonly (string File, string Reason)[] NotServedRows =
    [
        ("Supersession.cs", "the one definition of the words"),
        ("PostVerifier.cs", "log_index not served: the Forum served no index, a different meaning"),
        ("FlagReceiptWords.cs", "a flag receipt's sentence on post_served, R10.88's own words"),
        ("ToolText.cs", "tool descriptions' prose; RLW15 holds each to the definition sentence"),
        ("Help.cs", "help prose; RLW15 holds each entry to the definition sentence"),
    ];

    private static bool IsComment(string line) => line.TrimStart().StartsWith("//", StringComparison.Ordinal);

    /// <summary>
    /// RLW14. No <c>.cs</c> under <c>Curia.Client</c>, <c>Curia.Client.Cli</c> and <c>Curia.Mcp</c> writes the text <c>not served</c> on a line of code outside the
    /// one definition and the reasoned rows. A literal scan: it cannot see a form that omits the words, which RLW9 and RLW43 do.
    /// </summary>
    [Fact]
    public void R10_91_RLW14_NoFormWritesItsOwnNotServed()
    {
        var files = ReaderProjects.SelectMany(p => Directory.EnumerateFiles(Path.Combine(Repo(), "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToArray();
        Assert.True(files.Length > 40, $"only {files.Length} source files were read");

        var offenders = new List<string>();
        var rowsSeen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var hits = File.ReadAllLines(file).Select((line, i) => (line, number: i + 1))
                .Where(x => !IsComment(x.line) && x.line.Contains("not served", StringComparison.Ordinal)).ToArray();
            if (hits.Length == 0) continue;
            if (NotServedRows.Any(r => r.File == name)) { rowsSeen.Add(name); continue; }
            offenders.AddRange(hits.Select(h => $"{name}:{h.number}"));
        }

        Assert.True(offenders.Count == 0, "a form writes `not served` outside the definition: " + string.Join(", ", offenders));
        Assert.Contains("Supersession.cs", rowsSeen);
    }

    // ----- RLW21 and RLW29 -----

    /// <summary>
    /// RLW21. <c>Program.WriteAcceptance</c> and <c>WriteTools.ResolveAsync</c> each call <c>AcceptWording.ReceiptKind</c>, and nothing else in
    /// <c>Curia.Client</c>, <c>Curia.Client.Cli</c> and <c>Curia.Mcp</c> does; and nothing but the one definition (<c>Passage.AcceptedRevisionStatement</c>)
    /// writes R10.87's statement, the span R10.92 says the line is made of. The fence reads calls and the span, never any use of <c>AcceptWording</c>, so its
    /// existing constants stay legal.
    /// </summary>
    [Fact]
    public void R10_92_RLW21_OneAuthorOfTheReceiptLine()
    {
        var sites = Calls("AcceptWording::ReceiptKind(", Readers).Select(c => c.Site).Distinct().Order(StringComparer.Ordinal).ToArray();
        string[] expected = ["Curia.Client.Cli.Program::WriteAcceptance", "Curia.Mcp.ForumTools::ResolveAsync"];

        var problems = new List<string>();
        if (!sites.SequenceEqual(expected)) problems.Add($"Expected: both call it ({string.Join(", ", expected)}) Actual: {(sites.Length == 0 ? "neither" : string.Join(", ", sites))}");

        var statement = PublishedChainWords.RevisionStatement();
        var writers = ReaderProjects.SelectMany(p => Directory.EnumerateFiles(Path.Combine(Repo(), "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllLines(f).Any(l => !IsComment(l) && l.Contains(statement[..statement.IndexOf(',', StringComparison.Ordinal)], StringComparison.Ordinal)))
            .Select(Path.GetFileName).ToArray();
        if (!writers.SequenceEqual(["Passage.cs"])) problems.Add($"Expected: only Passage.cs writes R10.87's statement Actual: {string.Join(", ", writers)}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// RLW29. <c>WriteTools</c> (<c>curia_answer</c>'s <c>ReplyAsync</c>) and <c>Program</c> (<c>curia answer</c>'s <c>PostAsync</c>) each call
    /// <c>QuestionChain.WalkAsync</c> and <c>Supersession.QuestionChainStanding</c>; neither writes the words (no reader outside <c>Supersession.cs</c> writes
    /// the walk's stops).
    /// </summary>
    [Fact]
    public void R11_67_RLW29_OneAuthorOfTheWalkLine()
    {
        string[] expected = ["Curia.Client.Cli.Program::PostAsync", "Curia.Mcp.ForumTools::ReplyAsync"];
        var walkers = Calls("QuestionChain::WalkAsync(", Readers).Select(c => c.Site).Distinct().Order(StringComparer.Ordinal).ToArray();
        var wordsFrom = Calls("Supersession::QuestionChainStanding(", Readers).Select(c => c.Site).Distinct().Order(StringComparer.Ordinal).ToArray();

        var problems = new List<string>();
        if (!walkers.SequenceEqual(expected)) problems.Add($"Expected: both call QuestionChain.WalkAsync ({string.Join(", ", expected)}) Actual: {(walkers.Length == 0 ? "neither" : string.Join(", ", walkers))}");
        if (!wordsFrom.SequenceEqual(expected)) problems.Add($"Expected: both take the words from Supersession.QuestionChainStanding Actual: {(wordsFrom.Length == 0 ? "neither" : string.Join(", ", wordsFrom))}");

        var stops = PublishedReadersLastWords.WalkWords().NotWalked;
        var writers = ReaderProjects.SelectMany(p => Directory.EnumerateFiles(Path.Combine(Repo(), "src", p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => File.ReadAllLines(f).Any(l => !IsComment(l) && l.Contains(stops, StringComparison.Ordinal) && !Path.GetFileName(f).Equals("ToolText.cs", StringComparison.Ordinal) && !Path.GetFileName(f).Equals("Help.cs", StringComparison.Ordinal)))
            .Select(Path.GetFileName).ToArray();
        if (writers.Any(w => w != "Supersession.cs")) problems.Add($"a reader other than Supersession.cs writes {stops}: {string.Join(", ", writers)}");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ----- RLW42 -----

    private const string Label = "CONSISTENCY CHECK (RLW42)";

    [GeneratedRegex(@"\b(?:AccessPolicy|Supersession|AcceptWording|QuestionChain)\.|(?<!PublishedReadersLastWords\.)\bWalkBound\b", RegexOptions.CultureInvariant)]
    private static partial Regex ProductionRead();

    /// <summary>
    /// RLW42. No probe of this PR reads a production constant for an expectation: every test file that names an <c>_RLW</c> probe, and is not a pure unit file
    /// labelled <c>CONSISTENCY CHECK (RLW42)</c> in its header, holds no <c>AccessPolicy.</c>, <c>Supersession.</c>, <c>AcceptWording.</c>, <c>QuestionChain.</c> or
    /// <c>WalkBound</c> on a line of code. A control: a label without a read of one of them is stale and fails here too.
    /// </summary>
    [Fact]
    public void R11_67_RLW42_NoProbeReadsAProductionConstantForAnExpectation()
    {
        var tests = Directory.EnumerateFiles(Path.Combine(Repo(), "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f) != "ReadersLastWordsFenceTests.cs")
            .Select(f => (File: f, Lines: File.ReadAllLines(f)))
            .Where(x => x.Lines.Any(l => RlwProbe().IsMatch(l)))
            .ToArray();
        Assert.True(tests.Length >= 12, $"only {tests.Length} files name an RLW probe; the scan is looking in the wrong place");

        var problems = new List<string>();
        foreach (var (file, lines) in tests)
        {
            var labelled = lines.Any(l => l.Contains(Label, StringComparison.Ordinal));
            var reads = lines.Select((l, i) => (l, n: i + 1)).Where(x => !IsComment(x.l) && ProductionRead().IsMatch(x.l)).ToArray();
            if (!labelled && reads.Length > 0) problems.Add($"{Path.GetFileName(file)}:{reads[0].n} reads {ProductionRead().Match(reads[0].l).Value} and is not labelled a consistency check");
            if (labelled && reads.Length == 0) problems.Add($"{Path.GetFileName(file)} is labelled a consistency check and reads no production constant");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [GeneratedRegex(@"_RLW\d+_", RegexOptions.CultureInvariant)]
    private static partial Regex RlwProbe();
}
