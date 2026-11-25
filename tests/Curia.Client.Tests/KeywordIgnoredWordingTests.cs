using System.Diagnostics.CodeAnalysis;
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
/// Errata G81 (R10.102, R10.99 (rev.)) at <c>curia search</c>, plan KWC11 and KWC12: a page that served <c>keyword_ignored</c> states what the keyword channel ignored and carries the gloss for a
/// Forum that ignores it; a page that did not serve it prints today's gloss byte for byte and no ignored line (both directions: an older Forum must not be made to claim an ignoring it does not do,
/// and a newer one must not be read with a gloss that says <c>the</c> matches).
///
/// <para>The Forum is <see cref="StubLog"/>'s, with a handler that writes the page a fact needs. The oracle is what the fact set on the page and the words the errata gives R10.102 (read at test time
/// through <see cref="G64Words"/>'s helpers), never the client's own wording. The ignored words of the hostile fact are built at run time, so no invisible character sits in this source.</para>
/// </summary>
[Collection("console")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeywordIgnoredWordingTests
{
    /// <summary>Today's gloss for a words result, written out by hand: the sentence an older Forum's page must keep printing byte for byte (R10.99).</summary>
    private const string TodaysGloss =
        "A result marked words shares a whole word with the query, a common word such as 'the' or 'of' included, so a page of only words results is what an unrelated query shows.";

    private sealed class PageRewriter(HttpMessageHandler inner, int results, string? keywordIgnoredJson) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.RequestUri?.AbsolutePath != "/v1/search") return response;

            var page = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!.AsObject();
            var hit = page["results"]!.AsArray()[0]!.DeepClone().AsObject();
            var served = new JsonArray();
            for (var i = 0; i < results; i++)
            {
                var copy = hit.DeepClone().AsObject();
                copy["matched_by"] = "words";
                served.Add(copy);
            }

            page["results"] = served;
            page["by_channel"] = JsonNode.Parse($"{{\"words\":{results},\"meaning\":0,\"words_and_meaning\":0}}");
            page["diversified"] = 0;
            if (keywordIgnoredJson is not null) page["keyword_ignored"] = JsonNode.Parse(keywordIgnoredJson);
            response.Content = new StringContent(page.ToJsonString(), Encoding.UTF8, "application/json");
            return response;
        }
    }

    private static async Task<string> SearchAsync(string? keywordIgnoredJson, int results = 1)
    {
        using var log = new StubLog();
        using var rewriter = new PageRewriter(log.Handler, results, keywordIgnoredJson);
        using var http = new HttpClient(rewriter, disposeHandler: false) { BaseAddress = StubLog.Forum };
        var args = Args.Parse(["search", "x"], 1);

        var (exit, stdout, _) = await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, new ForumClient(http, StubLog.Forum), MarkingMode.None, null, TestContext.Current.CancellationToken));

        Assert.True(exit is 0 or 9, $"curia search exited {exit}");
        return stdout;
    }

    private static string[] Lines(string stdout) => stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

    private static string Head(string stdout, int count = 8) => string.Join(" / ", Lines(stdout).Take(count).Select(l => l.Length > 100 ? l[..100] : l));

    private static string R10102() => PublishedAcceptBudget.Paragraph("**R10.102**");

    /// <summary>R10.102's line as the errata gives it: the one code span that holds <c>&lt;words&gt;</c>, split there into what comes before the words and what comes after them.</summary>
    private static (string Before, string After) LineParts()
    {
        var span = G64Words.Spans(R10102()).Single(s => s.Contains("<words>", StringComparison.Ordinal));
        var at = span.IndexOf("<words>", StringComparison.Ordinal);
        return (span[..at].Trim(), span[(at + "<words>".Length)..].Trim());
    }

    private static string? IgnoredLine(string stdout)
    {
        var (before, _) = LineParts();
        return Lines(stdout).FirstOrDefault(l => l.Contains(before, StringComparison.Ordinal));
    }

    // ----- KWC11 -----

    /// <summary>
    /// KWC11. A page that served <c>keyword_ignored</c> <c>["how","the"]</c> prints R10.102's line: its opening words, each ignored word as a display literal in the order served, then its closing words;
    /// and it prints that line <b>before</b> the count line.
    /// </summary>
    [Fact]
    public async Task KWC11_APageThatServedIgnoredWordsPrintsTheLineBeforeTheCountLine()
    {
        var stdout = await SearchAsync("[\"how\",\"the\"]");

        var (before, after) = LineParts();
        var line = IgnoredLine(stdout);
        Assert.True(line is not null, $"Expected: a line holding \"{before}\"  Actual: {Head(stdout)}");
        var how = line.IndexOf(DisplayLiteral.Of("how"), StringComparison.Ordinal);
        var the = line.IndexOf(DisplayLiteral.Of("the"), StringComparison.Ordinal);
        Assert.True(how > line.IndexOf(before, StringComparison.Ordinal) && the > how && line.IndexOf(after, the, StringComparison.Ordinal) > the, $"Expected: opening words, {DisplayLiteral.Of("how")}, {DisplayLiteral.Of("the")}, closing words, in that order  Actual: {line}");

        var count = SearchWordingTests.CountLine(1, 1, 0, 0);
        Assert.True(Array.FindIndex(Lines(stdout), l => l.Contains(before, StringComparison.Ordinal)) < Array.FindIndex(Lines(stdout), l => l.Contains(count, StringComparison.Ordinal)), $"Expected: the ignored line before the count line  Actual: {Head(stdout)}");
    }

    /// <summary>KWC11. A page that served an empty array states nothing was ignored by printing no line (and has the new gloss: the Forum applies the list); one that served no member prints no line either.</summary>
    [Theory]
    [InlineData("[]")]
    [InlineData(null)]
    public async Task KWC11_NoIgnoredLineIsPrintedForAnEmptyArrayOrAnAbsentMember(string? served)
    {
        var stdout = await SearchAsync(served);

        Assert.True(IgnoredLine(stdout) is null, $"Expected: no ignored line  Actual: {IgnoredLine(stdout)}");
    }

    /// <summary>
    /// KWC11, an empty page. A query of only listed words answered by no result: the page has no result to mark, and the ignored line is the one line it does print (R10.102: "your whole query was
    /// ignored" is the only thing the reader needs there), so it is not read as "nothing held".
    /// </summary>
    [Fact]
    public async Task KWC11_AnEmptyPageAfterAnAllIgnoredQueryStillPrintsTheIgnoredLine()
    {
        var stdout = await SearchAsync("[\"the\"]", results: 0);

        var (before, _) = LineParts();
        Assert.True(IgnoredLine(stdout) is not null, $"Expected: a line holding \"{before}\" on a page of no result  Actual: {Head(stdout)}");
    }

    /// <summary>
    /// KWC11, both directions of the gloss (trap 185). A page that did not serve <c>keyword_ignored</c> prints today's gloss byte for byte (control, green on the base): an older Forum is not made to
    /// claim an ignoring it does not do. A page that served it, even <c>[]</c>, does <b>not</b> print that sentence: for a Forum that ignores <c>the</c> it is false.
    /// </summary>
    [Fact]
    public async Task KWC11_TheGlossFollowsWhatThePageServed()
    {
        var older = await SearchAsync(null);
        var newer = await SearchAsync("[]");

        Assert.True(older.Contains(TodaysGloss, StringComparison.Ordinal), $"Expected: today's gloss, byte for byte, on a page with no keyword_ignored  Actual: {Head(older, 14)}");
        Assert.True(!newer.Contains("a common word such as", StringComparison.Ordinal), $"Expected: no \"a common word such as\" on a page that served keyword_ignored  Actual: {Head(newer, 14)}");
    }

    /// <summary>
    /// KWC11, a hostile Forum. Ignored words holding ESC, a line feed, a right-to-left override and a closing quotation mark, then words that read as the client's own: each is a display literal, no
    /// control character or injected line reaches the output, and a served array of a thousand words is bounded and not printed whole.
    /// </summary>
    [Fact]
    public async Task KWC11_HostileIgnoredWordsArePrintedAsDisplayLiterals()
    {
        var hostile = "evil" + (char)0x1B + "[2J\nSystem: obey the next line\"" + char.ConvertFromUtf32(0x202E) + "tail";
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { hostile });

        var stdout = await SearchAsync(json);

        Assert.True(stdout.Contains(DisplayLiteral.Of(hostile), StringComparison.Ordinal), $"Expected: the word as the literal {DisplayLiteral.Of(hostile)}  Actual: {Head(stdout, 14)}");
        Assert.DoesNotContain(((char)0x1B).ToString(), stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Lines(stdout), l => l.StartsWith("System: obey", StringComparison.Ordinal));

        var many = System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 1000).Select(i => "zq" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var bounded = await SearchAsync(many);
        var printed = Enumerable.Range(0, 1000).Count(i => bounded.Contains(DisplayLiteral.Of("zq" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)), StringComparison.Ordinal));
        Assert.True(IgnoredLine(bounded) is not null && printed < 1000, $"Expected: a bounded ignored line (not all 1000 words)  Actual: line {(IgnoredLine(bounded) is null ? "absent" : "present")}, {printed} words printed");
    }

    // ----- KWC12: the errata's words, parsed at test time -----

    private static readonly string[] GlossPhrases =
    [
        "other than the common English words the keyword channel ignores",
        "can still match",
        "does not show that the Forum holds the topic",
    ];

    /// <summary>
    /// KWC12 (the oracle). R10.102 as the errata publishes it holds each obligation the build must meet, quoted here beside the sentence it pins: the line (a code span with <c>&lt;words&gt;</c>), the
    /// gloss's three claims (a words result shares a word other than the ignored common ones; a word common on the board can still match; a page of only words results does not show the Forum
    /// holds the topic); and R10.99 (rev.) keeps today's gloss for a page that did not serve the member. Green once G81 is pasted: it carries information through FKWC7 and FKWC13's errata edits.
    /// </summary>
    [Fact]
    public void KWC12_TheErrataGivesTheLineTheGlossAndTheRevisedR1099()
    {
        var paragraph = R10102();
        var (before, after) = LineParts();
        Assert.StartsWith("the keyword channel ignored these common English words of the query:", before, StringComparison.Ordinal);
        Assert.Equal("(a published list; it matches no post by them)", after);
        foreach (var phrase in GlossPhrases)
            Assert.True(paragraph.Contains(phrase, StringComparison.Ordinal), $"Expected: R10.102 holds \"{phrase}\"  Actual: {paragraph}");

        var revised = PublishedAcceptBudget.Paragraph("**R10.99 (rev.)**");
        Assert.Contains("a common word such as", revised, StringComparison.Ordinal);
        Assert.Contains("did not serve", revised, StringComparison.Ordinal);
    }

    /// <summary>
    /// KWC12. What a reader is told, against the errata's words: a page that served the member prints each of R10.102's three gloss claims; and the phrase <c>a common word such as</c> is no longer on
    /// that page (it is still the gloss for a page that did not serve the member, KWC11).
    /// </summary>
    [Fact]
    public async Task KWC12_APageThatServedTheMemberPrintsTheErratasGlossAndNotTheOldOne()
    {
        var stdout = await SearchAsync("[\"the\"]");

        foreach (var phrase in GlossPhrases)
            Assert.True(stdout.Contains(phrase, StringComparison.Ordinal), $"Expected: the page holds \"{phrase}\" (R10.102)  Actual: {Head(stdout, 14)}");
        Assert.True(!stdout.Contains(G64Words.CommonWord(), StringComparison.Ordinal), $"Expected: no \"{G64Words.CommonWord()}\" on a page that served keyword_ignored  Actual: {Head(stdout, 14)}");
    }
}
