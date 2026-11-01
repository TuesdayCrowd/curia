using System.Diagnostics.CodeAnalysis;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// Errata G54's words themselves (CPF26) and the neighbours it must leave byte for byte (CPF29). CPF26: the entry is in the errata with its index rows, every figure, name,
/// piece, sentence and paragraph the other probes parse is there exactly once, and G51's replaced spans are pointers (red until the build edits them). CPF29: the store
/// sentence and the act line equal G53's published words and the receipt's three lines equal G51's, so PR 3 (D104) finds them where it left them.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PublishedCheckWordsTests
{
    private static void Said(string expected, string actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
            throw new Xunit.Sdk.XunitException($"Expected: {expected}{Environment.NewLine}Actual:   {actual}");
    }

    /// <summary>
    /// CPF26. G54's words exist and agree: the four requirements and their index rows, the three figures read one at a time, the first wait and the bound, the three log
    /// events with their ids, the members, the reset's limit and wait, every piece of the line, the tool's sentence and the paragraph; and G51's <c>R11.70 words</c> hold
    /// neither the old search line (<c>none pending</c> as the line for 0) nor the old <c>curia_search</c> paragraph and do hold a pointer to R11.75's words.
    /// </summary>
    [Fact]
    public void R11_75_CPF26_G54sWordsExistOnceAndG51sReplacedSpansArePointers()
    {
        foreach (var anchor in PublishedCheckWords.Anchors) Assert.NotEmpty(PublishedAcceptBudget.Paragraph(anchor));
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "curia-whitepaper-ERRATA-AND-ADDENDUM.md"));
        var missingRows = PublishedCheckWords.IndexRows.Where(r => !text.Contains("\n" + r, StringComparison.Ordinal)).ToArray();

        var read = new List<string>
        {
            $"repeat {PublishedCheckWords.SweepRepeat().TotalSeconds}",
            $"settle {PublishedCheckWords.SweepSettle().TotalSeconds}",
            $"floor {PublishedCheckWords.SweepFloor().TotalMinutes}",
            $"failed wait {PublishedCheckWords.FailedCheckWait().TotalSeconds}",
            $"bound {PublishedCheckWords.UnplaceableBound()}",
            $"events {string.Join(", ", PublishedCheckWords.Events().Select(e => e.Name + " " + e.Id))}",
            $"members {PublishedCheckWords.MemberNames()}",
            $"reset {PublishedCheckWords.ResetLimit().TotalSeconds}/{PublishedCheckWords.ResetWait().TotalSeconds}",
            PublishedCheckWords.CountClause(2),
            PublishedCheckWords.NonePending(),
            PublishedCheckWords.NoneCounted(),
            PublishedCheckWords.LineNotStated(),
            PublishedCheckWords.NotOwed(),
            PublishedCheckWords.Owed("T"),
            PublishedCheckWords.OwedNotStated(),
            PublishedCheckWords.Checked("T"),
            PublishedCheckWords.CheckedNotStated(),
            PublishedCheckWords.OwedSentence(),
            PublishedCheckWords.Paragraph(),
        };
        Assert.Equal(19, read.Count);

        var g51 = PublishedIndexLag.ClientWords();
        var problems = new List<string>();
        if (missingRows.Length > 0) problems.Add($"index rows missing: {string.Join(", ", missingRows)}");
        if (g51.Contains("`vector none pending` for 0", StringComparison.Ordinal)) problems.Add("G51's R11.70 words still hold the old search-line spans");
        if (g51.Contains("The vector line states how many committed posts", StringComparison.Ordinal) && !g51.Contains("R11.75 words", StringComparison.Ordinal)) problems.Add("G51's R11.70 words hold the old curia_search paragraph and no pointer");
        if (!g51.Contains("R11.75 words", StringComparison.Ordinal)) problems.Add("G51's R11.70 words hold no pointer to R11.75 words");

        Said("G54's words exist once, its index rows are present and G51's replaced spans are pointers", problems.Count == 0
            ? "G54's words exist once, its index rows are present and G51's replaced spans are pointers"
            : string.Join("; ", problems));
    }

    /// <summary>
    /// CPF29 (a control). The neighbours stay byte for byte: the store sentence and the act line the clients print equal G53's published words, and the receipt's three
    /// lines (stored, pending, not applicable) equal G51's.
    /// </summary>
    [Fact]
    public void R11_75_CPF29_TheStoreSentenceTheActLineAndTheReceiptLinesAreAsTheirEntriesPublishedThem()
    {
        const string digest = "sha256:0000000000000000000000000000000000000000000000000000000000000000";
        var (indexed, pending, notApplicable) = PublishedIndexLag.Values();
        var pairs = new (string What, string Published, string Printed)[]
        {
            ("the store sentence", PublishedStoreFaults.StoreUnavailableSentence, StoreWording.Sentence(StoreWording.StoreUnavailableType)),
            ("the act line", PublishedStoreFaults.ActLine(digest), StoreWording.ActLine(digest)),
            ("the stored receipt line", PublishedIndexLag.ReceiptLine(indexed), IndexWording.ReceiptLine(VectorIndexState.Indexed)),
            ("the pending receipt line", PublishedIndexLag.ReceiptLine(pending), IndexWording.ReceiptLine(VectorIndexState.Pending)),
            ("the not-applicable receipt line", PublishedIndexLag.ReceiptLine(notApplicable), IndexWording.ReceiptLine(VectorIndexState.NotApplicable)),
        };

        Said(
            string.Join("; ", pairs.Select(p => $"{p.What}: \"{PublishedIndexLag.Fold(p.Published)}\"")),
            string.Join("; ", pairs.Select(p => $"{p.What}: \"{PublishedIndexLag.Fold(p.Printed)}\"")));
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found above " + AppContext.BaseDirectory);
    }
}
