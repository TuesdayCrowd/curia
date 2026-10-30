using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Application.Tests;

/// <summary>
/// Errata G49, SEB49 and SEB50: the words the other probes parse exist and agree (a control for the parser), and no probe reads a production constant for an
/// expectation. Both are green on the seam tree; SEB49 is red if a span is removed from the errata, SEB50 if a probe reads one.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BudgetPinsTests
{
    private static readonly string[] Entries = ["posting", "acceptance", "flag"];
    private static readonly string[] EntryMembers = ["name", "limit", "used", "remaining", "next_slot_at"];
    private static readonly string[] ReceiptMembers = ["name", "limit", "used", "remaining"];
    private static readonly string[] ResponseMembers = ["tier", "credential", "as_of", "window_seconds", "budgets"];
    private static readonly string[] Credentials = ["active", "quarantined"];
    private static readonly string[] Spans = ["curia budget", "curia_budget", "NOT STATED", "no fixed limit", "curia whoami", "next slot NOT STATED"];
    private static readonly string[] Indexed = ["R7.28", "R7.29", "R9.50", "R11.65"];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// SEB49. The words of G49 exist and agree: R7.28, R7.29, R9.50 and R11.65 are in the errata with the window, names, members, order, route, header, problem
    /// types, credential words, refusal token and version the other probes parse, and each has its index row (a control for the parser).
    /// </summary>
    [Fact]
    public void R9_50_SEB49_TheWordsOfG49ExistAndAgree()
    {
        var problems = new List<string>();
        void Check(string what, object expected, object actual)
        {
            var (e, a) = (expected is string[] x ? string.Join(',', x) : expected.ToString(), actual is string[] y ? string.Join(',', y) : actual.ToString());
            if (e != a) problems.Add($"{what}: Expected: {e} Actual: {a}");
        }

        Check("window", 86_400, PublishedBudgetReadout.WindowSeconds());
        Check("names", Entries, PublishedBudgetReadout.Names());
        Check("entry members", EntryMembers, PublishedBudgetReadout.EntryMembers());
        Check("receipt members", ReceiptMembers, PublishedBudgetReadout.ReceiptMembers());
        Check("response members", ResponseMembers, PublishedBudgetReadout.ResponseMembers());
        Check("credential words", Credentials, PublishedBudgetReadout.CredentialWords());
        Check("route", "GET /v1/budget", PublishedBudgetReadout.RouteSpan());
        Check("header", "Cache-Control: no-store", PublishedBudgetReadout.Header());
        Check("unknown-parameter type", "curia/budget/unknown-parameter", PublishedBudgetReadout.UnknownParameterType());
        Check("flag store type", "curia/flag/detail-store-unavailable", PublishedBudgetReadout.FlagStoreType());
        Check("unreadable log type", "curia/log/unreadable", PublishedBudgetReadout.LogUnreadableType());
        Check("refusal token", "next_slot_at=", PublishedBudgetReadout.RefusalToken());
        Check("table 10 row", "budget/read/✗,✓,✓,✓,✓", $"{PublishedBudgetReadout.Table10Row().Resource}/{PublishedBudgetReadout.Table10Row().Action}/{string.Join(',', PublishedBudgetReadout.Table10Row().Cells)}");
        Check("the spans R11.65 names", "curia budget,curia_budget,NOT STATED,no fixed limit,curia whoami,next slot NOT STATED",
            string.Join(',', Spans.Select(PublishedBudgetReadout.Span)));
        Check("version shape", "True", Regex.IsMatch(PublishedBudgetReadout.Version(), @"^\d+\.\d+\.\d+$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)));

        var errata = File.ReadAllLines(Path.Combine(RepoRoot(), "curia-whitepaper-ERRATA-AND-ADDENDUM.md"));
        foreach (var number in Indexed)
            if (!errata.Any(l => l.StartsWith($"| {number} |", StringComparison.Ordinal) && l.TrimEnd().EndsWith("| G49 |", StringComparison.Ordinal)))
                problems.Add($"{number}: Expected: an index row ending | G49 | Actual: none");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>The pure unit files that may name the production type they check (SEB1 to SEB7, SEB27, SEB28, SEB52), and why.</summary>
    private static readonly string[] PureUnitFiles = ["BudgetReadoutTests.cs", "BudgetKindsTests.cs"];

    /// <summary>
    /// SEB50. No probe reads a production constant for an expectation: a scan of the new probe files finds no <c>TierPolicy.</c>, <c>BudgetKinds.</c>,
    /// <c>BudgetReadout.</c> or <c>BudgetWindow</c> outside the pure unit files of the Application suite. (A dotted name inside a string, such as an IL member
    /// <c>Curia.Application.Budget.BudgetReadout.</c>, is not a read.)
    /// </summary>
    [Fact]
    public void R7_28_SEB50_NoProbeReadsAProductionConstantForAnExpectation()
    {
        var root = RepoRoot();
        var probes = Directory.EnumerateFiles(Path.Combine(root, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Path.GetFileName(f).StartsWith("Budget", StringComparison.Ordinal) || Path.GetFileName(f) == "PublishedBudgetReadout.cs")
            .Where(f => !PureUnitFiles.Contains(Path.GetFileName(f)) && Path.GetFileName(f) != "BudgetPinsTests.cs")
            .ToArray();
        Assert.NotEmpty(probes);

        var pattern = new Regex(@"(?<![\w.""])(TierPolicy|BudgetKinds|BudgetReadout)\.|\bBudgetWindow\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
        var reads = probes
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), Number: i + 1, Line: line)))
            .Where(l => !l.Line.TrimStart().StartsWith("///", StringComparison.Ordinal) && !l.Line.TrimStart().StartsWith("//", StringComparison.Ordinal) && pattern.IsMatch(l.Line))
            .Select(l => $"{l.File}:{l.Number}")
            .ToArray();

        Assert.True(reads.Length == 0, $"Expected: no production constant read for an expectation Actual: {string.Join(", ", reads)}");
    }
}
