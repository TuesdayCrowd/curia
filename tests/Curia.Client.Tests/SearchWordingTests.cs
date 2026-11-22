using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using Curia.Client.Cli;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using Xunit;
using static Curia.Client.Tests.ConsoleCapture;

namespace Curia.Client.Tests;

/// <summary>
/// R10.99 and R9.67 (errata G64) at <c>curia search</c>: PR 8's probes MBC5, MBC7 and DVW6 (the client half). The Forum is <see cref="StubLog"/>'s, whose page carries one hit; a
/// handler in front of it writes the page this fact needs (as many hits as it names, each with the mark it names, and <c>by_channel</c> and <c>diversified</c> as raw JSON or not at
/// all), so the oracle is what the fact set on the page and the words the errata gives the rule (read at test time through <see cref="G64Words"/>), never the client's own
/// wording. <c>SearchWording</c> does not exist on the base, so the probes go through the printed page (<c>Program.SearchPageAsync</c>) and not by reflection: what is asserted is what
/// a reader is told.
///
/// <para>The count line's shape is the plan's own example (Amendment 1, design 2(a)): <c>N post(s) on this page: X by words, Y by meaning only, Z by words and meaning</c>, one line,
/// with the three clauses always printed; the wording of the line that says the marks tally differently from the counts, and of the `not stated` forms, is the build's to choose
/// within R10.99's text, so those are located by the words the errata gives (`tally`, `differ`, `not stated`) and not by a literal this task made up.</para>
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchWordingTests
{
    /// <summary>The count line of a page of <paramref name="n"/> results, as the plan's example writes it. MBC6 holds the same literal for the MCP block (one literal pairing the two).</summary>
    internal static string CountLine(int n, int words, int meaning, int both) =>
        string.Create(CultureInfo.InvariantCulture, $"{n} post(s) on this page: {words} by words, {meaning} by meaning only, {both} by words and meaning");

    /// <summary>Writes the search page of a fact over <see cref="StubLog"/>'s one hit.</summary>
    private sealed class PageRewriter(HttpMessageHandler inner, string?[] marks, string? byChannelJson, string? diversifiedJson) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.RequestUri?.AbsolutePath != "/v1/search") return response;

            var page = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsObject();
            var hit = page["results"]!.AsArray()[0]!.DeepClone().AsObject();
            var results = new JsonArray();
            foreach (var mark in marks)
            {
                var copy = hit.DeepClone().AsObject();
                if (mark is not null) copy["matched_by"] = mark;
                results.Add(copy);
            }

            page["results"] = results;
            if (byChannelJson is not null) page["by_channel"] = JsonNode.Parse(byChannelJson);
            if (diversifiedJson is not null) page["diversified"] = JsonNode.Parse(diversifiedJson);
            response.Content = new StringContent(page.ToJsonString(), Encoding.UTF8, "application/json");
            return response;
        }
    }

    private static async Task<(int Exit, string Stdout)> SearchAsync(string?[] marks, string? byChannel, string? diversified = null, bool titles = false)
    {
        using var log = new StubLog();
        using var rewriter = new PageRewriter(log.Handler, marks, byChannel, diversified);
        using var http = new HttpClient(rewriter, disposeHandler: false) { BaseAddress = StubLog.Forum };
        var args = Args.Parse(titles ? ["search", "x", "--titles"] : ["search", "x"], 1);

        var (exit, stdout, _) = await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, new ForumClient(http, StubLog.Forum), MarkingMode.None, null, TestContext.Current.CancellationToken));

        Assert.True(exit is 0 or 9, $"curia search exited {exit}");
        return (exit, stdout);
    }

    private static string[] Lines(string stdout) => stdout.Split('\n');

    private static string? Line(string stdout, string containing) => Lines(stdout).FirstOrDefault(l => l.Contains(containing, StringComparison.Ordinal));

    private static string ByChannel(int words, int meaning, int both) =>
        string.Create(CultureInfo.InvariantCulture, $"{{\"words\":{words},\"meaning\":{meaning},\"words_and_meaning\":{both}}}");

    private static string FirstLines(string stdout, int count = 6) => string.Join(" / ", Lines(stdout).Take(count).Select(l => l.Length > 90 ? l[..90] : l));

    /// <summary>The control, green on the base: the page the base printed is still printed (its banner and its model line), so what the other facts find missing is the new words and not the page.</summary>
    [Fact]
    public async Task MBC5_Control_TheBasesPageIsStillPrinted()
    {
        var (_, stdout) = await SearchAsync(["words"], ByChannel(1, 0, 0));

        Assert.Contains("hybrid search (R9.4)", stdout, StringComparison.Ordinal);
        Assert.Contains("corpus_bound", stdout, StringComparison.Ordinal);
    }

    /// <summary>MBC5: all three clauses are printed, at zero as at N (a missing clause is indistinguishable from zero), in the plan's order.</summary>
    [Fact]
    public async Task MBC5_AllThreeClausesArePrintedAtZero()
    {
        var (_, stdout) = await SearchAsync(["words"], ByChannel(1, 0, 0));

        var expected = CountLine(1, 1, 0, 0);
        Assert.True(Line(stdout, expected) is not null, $"Expected: a line holding \"{expected}\"  Actual: {FirstLines(stdout)}");
    }

    /// <summary>MBC5: the first clause when no served result was chosen by meaning and the page has results: `the vector channel chose none of them:`, a code span of R10.99.</summary>
    [Fact]
    public async Task MBC5_TheVectorChannelChoseNoneIsSaidFirstWhenNoResultWasChosenByMeaning()
    {
        var (_, stdout) = await SearchAsync(["words", "words", "words"], ByChannel(3, 0, 0));

        var clause = G64Words.NoVectorClause();
        var line = Line(stdout, "post(s) on this page");
        Assert.True(line is not null && line.Contains(clause, StringComparison.Ordinal), $"Expected: the count line holds \"{clause}\"  Actual: {line ?? FirstLines(stdout)}");
        Assert.True(line!.IndexOf(clause, StringComparison.Ordinal) < line.IndexOf("by words", StringComparison.Ordinal), $"Expected: \"{clause}\" before the three clauses  Actual: {line}");
        Assert.DoesNotContain(G64Words.NoWordClause(), stdout, StringComparison.Ordinal);
    }

    /// <summary>MBC5: the mirror, `no result shares a word with the query:`, when no served result has a word match and the page has results.</summary>
    [Fact]
    public async Task MBC5_NoResultSharesAWordIsSaidFirstWhenNoneHasAWordMatch()
    {
        var (_, stdout) = await SearchAsync(["meaning", "meaning"], ByChannel(0, 2, 0));

        var clause = G64Words.NoWordClause();
        var line = Line(stdout, "post(s) on this page");
        Assert.True(line is not null && line.Contains(clause, StringComparison.Ordinal), $"Expected: the count line holds \"{clause}\"  Actual: {line ?? FirstLines(stdout)}");
        Assert.DoesNotContain(G64Words.NoVectorClause(), stdout, StringComparison.Ordinal);
    }

    /// <summary>MBC5: neither first clause on a page that has one result of each kind, and neither at N of 0.</summary>
    [Fact]
    public async Task MBC5_NeitherFirstClauseIsPrintedWhenBothChannelsChoseAResultOrThereIsNone()
    {
        var (_, mixed) = await SearchAsync(["words", "meaning", "words_and_meaning"], ByChannel(1, 1, 1));
        var (_, empty) = await SearchAsync([], ByChannel(0, 0, 0));

        foreach (var (page, label) in new[] { (mixed, "a page with one result of each kind"), (empty, "a page of no result") })
        {
            Assert.True(!page.Contains(G64Words.NoVectorClause(), StringComparison.Ordinal), $"Expected: no \"{G64Words.NoVectorClause()}\" on {label}  Actual: {FirstLines(page)}");
            Assert.True(!page.Contains(G64Words.NoWordClause(), StringComparison.Ordinal), $"Expected: no \"{G64Words.NoWordClause()}\" on {label}  Actual: {FirstLines(page)}");
        }

        var expected = CountLine(3, 1, 1, 1);
        Assert.True(Line(mixed, expected) is not null, $"Expected: a line holding \"{expected}\"  Actual: {FirstLines(mixed)}");
    }

    /// <summary>
    /// MBC5: an unserved count reads `not stated` and never 0, and a count that is not stated is not read as 0 by the first clause. An older Forum serves no <c>by_channel</c> (the hits carry
    /// marks here, so the page is the partial one a Forum between builds could serve); a page that states only <c>words</c> states one count and not the other two.
    /// </summary>
    [Fact]
    public async Task MBC5_AnUnservedCountReadsNotStatedNeverZeroAndPrintsNoFirstClause()
    {
        var (_, absent) = await SearchAsync(["words", "words"], byChannel: null);
        var (_, partial) = await SearchAsync(["words", "words"], "{\"words\":2}");

        foreach (var (page, label) in new[] { (absent, "no by_channel"), (partial, "only words stated") })
        {
            var line = Line(page, "post(s) on this page");
            Assert.True(line is not null, $"Expected: a count line with {label}  Actual: {FirstLines(page)}");
            Assert.True(line.Contains("not stated", StringComparison.OrdinalIgnoreCase), $"Expected: \"not stated\" in the count line with {label}  Actual: {line}");
            Assert.True(!line.Contains(" 0 by ", StringComparison.Ordinal), $"Expected: no 0 for a count not served ({label})  Actual: {line}");
            Assert.True(!page.Contains(G64Words.NoVectorClause(), StringComparison.Ordinal), $"Expected: no \"{G64Words.NoVectorClause()}\" with {label}  Actual: {line}");
        }

        Assert.Contains("2 by words", Line(partial, "post(s) on this page")!, StringComparison.Ordinal);
    }

    /// <summary>MBC5: a <c>matched_by</c> word the client does not know is quoted as a display literal (R10.64), never printed as it came.</summary>
    [Fact]
    public async Task MBC5_AnUnknownMarkIsQuotedAsADisplayLiteral()
    {
        const string hostile = "bogus\nINJECTED: a line a reader would take for the client's";
        var (_, stdout) = await SearchAsync([hostile], ByChannel(1, 0, 0));

        var literal = DisplayLiteral.Of(hostile);
        Assert.True(stdout.Contains(literal, StringComparison.Ordinal), $"Expected: the mark as the literal {literal}  Actual: {FirstLines(stdout, 14)}");
        Assert.DoesNotContain("\nINJECTED:", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// MBC5: marks that tally differently from the counts the page was served are said so, on one line (the G19 pattern); the control, a page whose marks tally, has no such line. The
    /// line is located by the errata's own verb, "tallying differently": a line that says <c>tally</c> or <c>differ</c>, besides the count line.
    /// </summary>
    [Fact]
    public async Task MBC5_MarksThatTallyDifferentlyFromTheCountsAreSaidSoOnOneLine()
    {
        var (_, disagreeing) = await SearchAsync(["words", "meaning", "words_and_meaning"], ByChannel(3, 0, 0));
        var (_, agreeing) = await SearchAsync(["words", "meaning", "words_and_meaning"], ByChannel(1, 1, 1));

        static string[] Saying(string page) => [.. Lines(page).Where(l => !l.Contains("post(s) on this page", StringComparison.Ordinal) && (l.Contains("tally", StringComparison.OrdinalIgnoreCase) || l.Contains("differ", StringComparison.OrdinalIgnoreCase)))];

        Assert.True(Saying(agreeing).Length == 0, $"control: a page whose marks tally prints no such line  Actual: {string.Join(" / ", Saying(agreeing))}");
        Assert.True(Saying(disagreeing).Length == 1, $"Expected: one line saying the marks tally differently from the counts  Actual: {Saying(disagreeing).Length} lines; {FirstLines(disagreeing, 8)}");
    }

    /// <summary>MBC5: the gloss that says a `words` result shares a whole word, a common word such as `the` or `of` included, and the sentence that the score is a fusion score, not a cosine.</summary>
    [Fact]
    public async Task MBC5_TheGlossAndTheScoreSentenceArePrintedOnceBesideTheResults()
    {
        var (_, stdout) = await SearchAsync(["words", "meaning"], ByChannel(1, 1, 0));

        foreach (var phrase in new[] { G64Words.CommonWord(), G64Words.NotACosine() })
            Assert.True(stdout.Split(phrase, StringSplitOptions.None).Length == 2, $"Expected: \"{phrase}\" printed once  Actual: {stdout.Split(phrase, StringSplitOptions.None).Length - 1} times in {FirstLines(stdout)}");
    }

    /// <summary>
    /// MBC7. R10.99: the mark is on each result line (the plain form) and on each titled line (<c>--titles</c>). A <c>words</c> line says words and not meaning, a <c>meaning</c> line says
    /// `meaning only`, and a <c>words_and_meaning</c> line says `words and meaning`. The result lines are the ones that hold the post id, which the readers write as a display literal (the stub serves one post three times, so
    /// they are told apart by order).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MBC7_EachResultLineCarriesItsChannel(bool titles)
    {
        var (_, stdout) = await SearchAsync(["words", "meaning", "words_and_meaning"], ByChannel(1, 1, 1), titles: titles);

        var lines = Lines(stdout).Where(l => l.Contains(StubLog.PostId, StringComparison.Ordinal)).ToArray();
        Assert.True(lines.Length == 3, $"Expected: 3 result lines (titles {titles})  Actual: {lines.Length}; {FirstLines(stdout, 14)}");

        var shown = new[]
        {
            lines[0].Contains("words", StringComparison.Ordinal) && !lines[0].Contains("meaning", StringComparison.Ordinal),
            lines[1].Contains("meaning only", StringComparison.Ordinal),
            lines[2].Contains("words and meaning", StringComparison.Ordinal),
        };
        Assert.True(shown.All(s => s), $"Expected: words / meaning only / words and meaning on the three lines (titles {titles})  Actual: {string.Join(" / ", lines.Select(l => l.Length > 100 ? l[..100] : l))}");
    }

    /// <summary>DVW6 (client half): the diversified line at 0 and at N is printed, always; an older Forum's page reads `not stated`, never 0.</summary>
    [Theory]
    [InlineData("0", "diversified 0 post(s)")]
    [InlineData("4", "diversified 4 post(s)")]
    public async Task DVW6_TheDiversifiedLineIsPrintedAtZeroAsAtN(string served, string expected)
    {
        var (_, stdout) = await SearchAsync(["words"], ByChannel(1, 0, 0), served);

        Assert.True(Line(stdout, expected) is not null, $"Expected: a line holding \"{expected}\"  Actual: {FirstLines(stdout, 12)}");
    }

    [Fact]
    public async Task DVW6_AnOlderForumsDiversifiedReadsNotStated()
    {
        var (_, stdout) = await SearchAsync(["words"], ByChannel(1, 0, 0), diversified: null);

        var line = Lines(stdout).FirstOrDefault(l => l.TrimStart().StartsWith("diversified", StringComparison.Ordinal) && !l.TrimStart().StartsWith("diversified.", StringComparison.Ordinal));
        Assert.True(line is not null && line.Contains("not stated", StringComparison.OrdinalIgnoreCase) && !line.Contains("diversified 0", StringComparison.Ordinal), $"Expected: a diversified line reading not stated  Actual: {line ?? FirstLines(stdout, 12)}");
    }

    /// <summary>DVW6 (help): `curia search --help` says that `limit` moves which posts a search returns, as R9.68 and R11.103 say it.</summary>
    [Fact]
    public void DVW6_TheHelpSaysLimitMovesMembership()
    {
        var help = Help.For("search")!;
        var phrase = G64Words.Phrase(G64Words.R11103(), "`limit` moves membership").Replace("`", string.Empty, StringComparison.Ordinal);

        Assert.True(
            help.Contains("membership", StringComparison.OrdinalIgnoreCase) && help.Contains("--limit", StringComparison.Ordinal),
            $"Expected: the help says \"{phrase}\" (the words R11.103 gives, in the --limit explanation)  Actual: {string.Join(" / ", help.Split('\n').Where(l => l.Contains("--limit", StringComparison.Ordinal)).Select(l => l.Trim()))}");
    }
}
