using System.Diagnostics.CodeAnalysis;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// WAF28 to WAF30 and WAF33 (errata G53): the words the probes read, and that nothing else holds them. <see cref="PublishedClientWords"/> reads each span of
/// G53 from the errata in context with its count asserted; no replaced span of G52 is left in G52; no test hand-copies a sentence G53 publishes; and the wait is
/// one figure in the three places the errata publishes it and in the two places the clients print it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class PublishedClientWordsTests
{
    private static string Fold(string text) => PublishedClientWords.Fold(text);

    /// <summary>
    /// WAF28, a control (green on the seam tree: G53 is applied; red only under a removed span). Every span the probes read is parsed with its count asserted: the
    /// <c>unknown</c> line and its three clause starts, the wait, the store sentence and its two reading pairs, the equal-head line with its three forms and its
    /// <c>NOT STATED</c> form, the closing, the another-check suffix and the two inclusion fragments, the token sentence and the <c>whoami</c> line; and a paragraph that
    /// is not there fails by name.
    /// </summary>
    [Fact]
    public void R11_74_WAF28_EverySpanTheProbesReadIsInG53WithItsCountAsserted()
    {
        Assert.Equal(3, PublishedClientWords.UnknownClauses().Length);
        Assert.Equal(2, PublishedClientWords.ReadingPairs().Length);
        Assert.Equal(3, PublishedClientWords.WhenForms().Length);
        Assert.Equal(3, PublishedClientWords.WaitFigures().Length);
        Assert.Equal(2, PublishedClientWords.InclusionFragments().Length);

        string[] spans =
        [
            PublishedClientWords.UnknownLine, PublishedClientWords.StoreSentence, PublishedClientWords.EqualHeadLine, PublishedClientWords.NotStatedLine,
            PublishedClientWords.PastHeadClosing, PublishedClientWords.AnotherCheckSuffix, PublishedClientWords.TokenSentence, PublishedClientWords.WhoamiExpired,
            PublishedClientWords.Wait, PublishedClientWords.ResolveCondition, PublishedClientWords.OtherKindClause,
            PublishedClientWords.SecondInstantExample, PublishedClientWords.MillisecondInstantExample,
        ];
        Assert.All(spans, span => Assert.True(span.Length > 4, $"a span was parsed as a bare token: \"{span}\""));

        var missing = Assert.Throws<InvalidOperationException>(() => PublishedClientWords.Paragraph("R99.99 words"));
        Assert.Contains("R99.99 words", missing.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// WAF29. No span G53 replaced is left in G52 (decision 7): G52's <c>R11.72 words</c> holds neither the store sentence's old reading ("a repeat of a resolve or of
    /// a flag is safe, because") nor the token sentence's "Nothing was minted", and it holds a pointer to the words that replaced them (R11.73 and R11.74).
    /// </summary>
    [Fact]
    public void R11_74_WAF29_NoSpanG53ReplacedIsLeftBehindInG52()
    {
        var g52 = PublishedStoreFaults.WordsParagraph("R11.72");
        var problems = new List<string>();

        foreach (var replaced in new[] { "a repeat of a resolve or of a flag is safe, because", "Nothing was minted" })
        {
            if (g52.Contains(replaced, StringComparison.Ordinal)) problems.Add($"G52's R11.72 words still hold \"{replaced}\"");
        }

        foreach (var pointer in new[] { "R11.73", "R11.74" })
        {
            if (!g52.Contains(pointer, StringComparison.Ordinal)) problems.Add($"G52's R11.72 words hold no pointer to {pointer}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>
    /// WAF30, a control (green on the seam tree; as ULG33). No test source writes a sentence or clause G53 introduces: the <c>unknown</c> line and its clause starts, the
    /// store sentence, the equal-head line and its forms, the closing and its suffix, the token sentence and the <c>whoami</c> line are written in no test source but
    /// the one that reads them from the errata. A sentence a test had written itself would agree with the code it checks.
    /// </summary>
    [Fact]
    public void R11_74_WAF30_NoTestHandCopiesASentenceG53Publishes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Curia.sln"))) dir = dir.Parent;
        var tests = Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Curia.sln not found"), "tests");

        var published = PublishedClientWords.AllPublished();
        Assert.All(published, p => Assert.True(p.Length > 20 || p.StartsWith("<k>", StringComparison.Ordinal) || p.Length > 12, $"a published span was parsed as a bare token: \"{p}\""));

        var copies = new List<string>();
        foreach (var file in Directory.EnumerateFiles(tests, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || Path.GetFileName(file) == "PublishedClientWords.cs") continue;

            var text = File.ReadAllText(file);
            copies.AddRange(published.Where(p => text.Contains(p, StringComparison.Ordinal)).Select(p => $"{Path.GetFileName(file)} writes \"{p[..Math.Min(60, p.Length)]}\""));
        }

        Assert.True(copies.Count == 0, string.Join('\n', copies));
    }

    /// <summary>
    /// WAF33. The wait is one number in three published places and the clients print it. <c>PublishedClientWords</c> reads the figure of R10.93 wait, of R10.93 words
    /// and of R11.73 words (three places, the same figure), and the line <c>recheck</c> prints for an unknown digest and the store sentence a refusal prints each hold
    /// it in its own place: the unknown line's wait clause and the sentence's "when the refusal is" clause, each followed by the figure and "old".
    /// </summary>
    [Fact]
    public async Task R10_93_WAF33_TheWaitIsOneFigureInThreePublishedPlacesAndTheClientsPrintIt()
    {
        var figures = PublishedClientWords.WaitFigures();
        Assert.Equal(3, figures.Length);
        Assert.True(figures.Distinct().Count() == 1, $"the wait is {string.Join(", ", figures)} in the three published places");

        var problems = new List<string>();
        var wait = PublishedClientWords.Wait;

        using var log = new StubLog();
        var digest = "sha256:" + new string('d', 64);
        log.BatchAnswer = _ => [StubLog.BatchItem(digest, "unknown")];
        var served = await log.Client().BatchAsync([digest], MarkingMode.None, TestContext.Current.CancellationToken);
        Assert.True(served.TryGetValue(out var items, out var refusal), refusal?.Summary);
        var line = Fold(Assert.Single(CitationLines.For([digest], items, DateTimeOffset.UnixEpoch).Lines).Text.Text);
        var waitClause = $"{Fold(PublishedClientWords.UnknownClauses()[1])} {wait} old";
        if (!line.Contains(waitClause, StringComparison.Ordinal)) problems.Add($"the unknown line lacks \"{waitClause}\"");

        var sentence = Fold(StoreWording.Sentence(StoreWording.StoreUnavailableType));
        if (!sentence.Contains($"when the refusal is {wait} old", StringComparison.Ordinal)) problems.Add($"the store sentence lacks \"when the refusal is {wait} old\"");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }
}
