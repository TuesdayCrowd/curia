using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// Errata G51 as an entry and as a source of expectations (the D92 PR, PIL32 and PIL33): the entry holds the words every probe reads and its parts agree
/// with each other, and no probe of this PR takes an expectation from a production constant. Both are controls on the seam (the entry is written and no
/// probe yet reads the code for an expectation); FPIL28 and FPIL29 make them red.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IndexLagEntryTests
{
    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory);
    }

    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>
    /// PIL32. G51's four requirements and their words exist, each has an index row, and the parts agree: the three values of <c>vector_index</c> are named by
    /// R8.81 itself, the member names appear in their requirements, the bounds in the words are those the requirement states (2 seconds, 4,096, 5 seconds,
    /// 5 minutes), both event names appear in their requirements, the four receipt lines and the search lines are the shapes R11.70 says, and R11.70 names a
    /// version. The problems are listed; none is the expectation.
    /// </summary>
    [Fact]
    public void R11_70_PIL32_TheEntryHoldsItsWordsAndItsPartsAgree()
    {
        var problems = new List<string>();
        void Check(bool ok, string what)
        {
            if (!ok) problems.Add(what);
        }

        var text = File.ReadAllText(Path.Combine(RepoRoot(), Errata));
        foreach (var row in PublishedIndexLag.IndexRows)
            Check(text.Contains("\n" + row, StringComparison.Ordinal), $"no index row {row}");

        var answer = PublishedIndexLag.Answer();
        var (indexed, pending, notApplicable) = PublishedIndexLag.Values();
        foreach (var value in new[] { indexed, pending, notApplicable })
            Check(answer.Contains($"`{value}`", StringComparison.Ordinal), $"R8.81 does not name the value {value}");
        Check(PublishedIndexLag.ReceiptMember() == "vector_index", $"R8.81's receipt member is {PublishedIndexLag.ReceiptMember()}");
        Check(answer.Contains($"`{PublishedIndexLag.PendingEvent()}`", StringComparison.Ordinal), "R8.81 does not name its log event");
        Check(PublishedIndexLag.StepBound() == TimeSpan.FromSeconds(2), $"R8.81's bound is {PublishedIndexLag.StepBound()}");

        Check(PublishedIndexLag.SearchMember() == "vector_pending", $"R9.51's member is {PublishedIndexLag.SearchMember()}");
        Check(PublishedIndexLag.Search().Contains($"`{PublishedIndexLag.SearchMember()}`", StringComparison.Ordinal), "R9.51 does not name its member");

        var words = PublishedIndexLag.RepairWords();
        Check(PublishedIndexLag.BacklogBound() == 4096 && words.Contains("4,096", StringComparison.Ordinal), "R11.69's backlog bound and its words disagree");
        Check(PublishedIndexLag.FirstWait() == TimeSpan.FromSeconds(5) && words.Contains("5 seconds", StringComparison.Ordinal), "R11.69's first wait and its words disagree");
        Check(PublishedIndexLag.WaitCap() == TimeSpan.FromMinutes(5) && words.Contains("5 minutes", StringComparison.Ordinal), "R11.69's cap and its words disagree");
        Check(PublishedIndexLag.Tick() == TimeSpan.FromSeconds(1), $"R11.69's tick is {PublishedIndexLag.Tick()}");
        Check(PublishedIndexLag.CaughtUpEvent() == "VectorIndexCaughtUp", $"R11.69's words name the event {PublishedIndexLag.CaughtUpEvent()}");

        Check(PublishedIndexLag.ReceiptLine(indexed).StartsWith("search  ", StringComparison.Ordinal) || PublishedIndexLag.ReceiptLine(indexed).StartsWith("search ", StringComparison.Ordinal), "the indexed line is not a search line");
        Check(PublishedIndexLag.ReceiptLine(pending).Contains("PENDING", StringComparison.Ordinal), "the pending line does not say PENDING");
        Check(PublishedIndexLag.ReceiptLine(notApplicable).Contains("not placed", StringComparison.Ordinal), "the not_applicable line does not say not placed");
        Check(PublishedIndexLag.NotStatedLine().Contains("NOT STATED", StringComparison.Ordinal), "the no-member line does not say NOT STATED");
        Check(PublishedCheckWords.Line(0, null, true, "2026-11-01T10:00:00.0000000+00:00").StartsWith("vector ", StringComparison.Ordinal), "the none-pending line is not a vector line");
        Check(PublishedCheckWords.CountClause(7).Contains("7 committed", StringComparison.Ordinal), "the count clause does not carry the count");
        Check(PublishedCheckWords.LineNotStated().Contains("NOT STATED", StringComparison.Ordinal), "the search no-member line does not say NOT STATED");
        Check(PublishedIndexLag.ToolCountLine(3) == "vector_pending=3" && PublishedIndexLag.ToolNotStatedLine() == "vector_pending=not stated", "the tool's members are not vector_pending=<n> and vector_pending=not stated");
        Check(PublishedIndexLag.Sentence().Contains("PENDING", StringComparison.Ordinal) && PublishedIndexLag.Sentence().Contains("do not send it again", StringComparison.Ordinal), "the sentence does not tell an agent not to send the post again");
        Check(PublishedReadersLastWords.Version() == "0.9.5", $"the version the one parser reads is {PublishedReadersLastWords.Version()}");   // the parser reads the newest entry's (R11.72, errata G52), which moves with each bump; it was 0.9.2 while it read R11.70

        Said("the entry holds its words and its parts agree", problems.Count == 0 ? "the entry holds its words and its parts agree" : string.Join("; ", problems));
    }

    /// <summary>The test files of this PR whose expectations PIL33 holds free of the code under test, by name; a file added to the PR is added here.</summary>
    private static readonly string[] ProbeFiles =
    [
        "tests/Curia.Api.Tests/CommittedPostEndpointTests.cs",
        "tests/Curia.Api.Tests/IndexRepairEndpointTests.cs",
        "tests/Curia.Api.Tests/IndexRepairDeepGapEndpointTests.cs",
        "tests/Curia.Api.Tests/VectorPendingSearchTests.cs",
        "tests/Curia.Application.Tests/Retrieval/IndexBacklogTests.cs",
        "tests/Curia.Application.Tests/Retrieval/EmbeddingCatchUpTests.cs",
        "tests/Curia.Infrastructure.Tests/CommittedIndexInfrastructureTests.cs",
        "tests/Curia.Client.Tests/IndexWordsTests.cs",
        "tests/Curia.Mcp.Tests/IndexWordsToolTests.cs",
        "tests/Curia.Architecture.Tests/IndexLagEntryTests.cs",
    ];

    /// <summary>The unit files that construct the production backlog type as the object under test, and say so in a line the scan looks for.</summary>
    private static readonly string[] ConsistencyUnits =
    [
        "tests/Curia.Application.Tests/Retrieval/IndexBacklogTests.cs",
        "tests/Curia.Application.Tests/Retrieval/EmbeddingCatchUpTests.cs",
    ];

    private const string ConsistencyMarker = "PIL33: constructs the production type as the object under test, not as a source of expectation.";

    /// <summary>
    /// PIL33. No probe of this PR reads a production constant for an expectation: none of the probe files names <c>IndexWording.</c>, <c>IndexBacklog.Bound</c> or
    /// <c>CommittedPostIndexer.Bound</c>, and <c>PendingEntry</c> appears only in the two unit files that carry the marker line saying they build it as the
    /// object under test. A scan of tracked text, so a file that is missing is itself a finding (a probe moved or was never written). The names are matched as member
    /// accesses (word boundaries), so a fact may name the file <c>IndexWording.cs</c>.
    /// </summary>
    [Fact]
    public void R11_70_PIL33_NoProbeTakesAnExpectationFromAProductionConstant()
    {
        var root = RepoRoot();
        var problems = new List<string>();
        foreach (var relative in ProbeFiles)
        {
            var path = Path.Combine(root, relative);
            if (!File.Exists(path))
            {
                problems.Add($"{relative} is missing");
                continue;
            }

            var source = File.ReadAllText(path);
            // A member access on the wording class, not its file's name (a fact may name the file it holds the phrase to).
            foreach (var forbidden in new[] { @"\bIndexWording\.(?!cs\b)[A-Za-z_]", @"\bIndexBacklog\.Bound\b", @"\bCommittedPostIndexer\.Bound\b" })
                if (!relative.EndsWith("IndexLagEntryTests.cs", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(source, forbidden, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)))
                    problems.Add($"{relative} names {forbidden}");

            if (CountOutsideThisFile(source, "PendingEntry", relative) > 0
                && !(ConsistencyUnits.Contains(relative, StringComparer.Ordinal) && source.Contains(ConsistencyMarker, StringComparison.Ordinal)))
                problems.Add($"{relative} names PendingEntry without being a labelled unit");
        }

        Said("no probe takes an expectation from a production constant", problems.Count == 0 ? "no probe takes an expectation from a production constant" : string.Join("; ", problems));
    }

    /// <summary>How often <paramref name="needle"/> occurs in <paramref name="source"/>, not counting this file's own list of forbidden names.</summary>
    private static int CountOutsideThisFile(string source, string needle, string relative)
    {
        if (!relative.EndsWith("IndexLagEntryTests.cs", StringComparison.Ordinal)) return CountOf(source, needle);

        // This file spells the names it forbids; only its string literals do, and they are not uses.
        return 0;
    }

    private static int CountOf(string source, string needle)
    {
        var count = 0;
        for (var at = source.IndexOf(needle, StringComparison.Ordinal); at >= 0; at = source.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
