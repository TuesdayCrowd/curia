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

    private sealed class PageRewriter(HttpMessageHandler inner, int results, string? keywordIgnoredJson, string? keywordTermsJson = null) : DelegatingHandler(inner)
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
            // The member is spliced in as the text the test names, so a value such as 1.0 reaches the reader as written (a JsonNode would normalise it to 1).
            var text = page.ToJsonString();
            if (keywordTermsJson is not null) text = text[..^1] + ",\"keyword_terms\":" + keywordTermsJson + "}";
            response.Content = new StringContent(text, Encoding.UTF8, "application/json");
            return response;
        }
    }

    private static async Task<string> SearchAsync(string? keywordIgnoredJson, int results = 1, string? modelJson = null, string? keywordTermsJson = null)
    {
        using var log = new StubLog();
        if (modelJson is not null) log.SearchModelJson = modelJson;
        using var rewriter = new PageRewriter(log.Handler, results, keywordIgnoredJson, keywordTermsJson);
        using var http = new HttpClient(rewriter, disposeHandler: false) { BaseAddress = StubLog.Forum };
        var args = Args.Parse(["search", "x"], 1);

        var (exit, stdout, _) = await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, new ForumClient(http, StubLog.Forum), MarkingMode.None, null, TestContext.Current.CancellationToken));

        Assert.True(exit is 0 or 9, $"curia search exited {exit}");
        return stdout;
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> SearchRawAsync(string? keywordIgnoredJson, string? keywordTermsJson)
    {
        using var log = new StubLog();
        using var rewriter = new PageRewriter(log.Handler, 1, keywordIgnoredJson, keywordTermsJson);
        using var http = new HttpClient(rewriter, disposeHandler: false) { BaseAddress = StubLog.Forum };
        var args = Args.Parse(["search", "x"], 1);

        return await CaptureAsync(
            () => Program.SearchPageAsync(args, "x", null, new ForumClient(http, StubLog.Forum), MarkingMode.None, null, TestContext.Current.CancellationToken));
    }

    /// <summary>The keyword clause of R11.93 (rev.), and the sentence of the model without it, as main printed it before G81.</summary>
    private const string KeywordClause = "The keyword channel ignores the common English words of a published list in a query (R9.72), so a word that is common on the board can still list posts. ";

    private const string BgeSentenceOnMain =
        "The vector channel's model is bge-small-en-v1.5@1: a learned model that reads at most 512 tokens of its input (two are the model's own markers, and a query carries a fixed instruction as well), so a long post is found by its opening and not by its end; it can place a paraphrase near the post it paraphrases, and it can miss one. " +
        "Results below a vector score of 0.64 are removed from the vector channel's candidates: on the measured corpus of about 190 posts that removed every candidate of 16 of 16 (100%) off-topic and made-up queries and kept 27 of 27 (100%) relevant scores. " +
        "The keyword channel has no floor: a post that shares a word with the query is listed whatever its vector score, so a long list of results does not show that the Forum holds the topic. " +
        "A result list can omit a near match that falls below the floor, and an empty or short list does not show that the Forum holds nothing on the topic. " +
        "The `removed by floor` line counts the verification floor, not this one. " +
        "A hit that shares no word with the query may be a paraphrase or may be unrelated: its rank does not say which.";

    /// <summary>
    /// KWC18 (errata G81, R11.93 (rev.), argument 6 at the banner). A page with no <c>keyword_ignored</c> prints the model sentence as main printed it, without the keyword clause: an older Forum is not made
    /// to claim an ignoring it does not do. A page that served the member, even <c>[]</c>, prints the sentence with the clause.
    /// </summary>
    [Fact]
    public async Task KWC18_TheBannerCarriesTheKeywordClauseOnlyForAPageThatServedTheMember()
    {
        var older = await SearchAsync(null, modelJson: "\"bge-small-en-v1.5@1\"");
        var newer = await SearchAsync("[]", modelJson: "\"bge-small-en-v1.5@1\"");
        static string Folded(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

        Assert.True(Folded(older).Contains(BgeSentenceOnMain, StringComparison.Ordinal), $"Expected: the banner holds main's sentence byte for byte  Actual: {Head(older, 6)}");
        Assert.True(!older.Contains("keyword channel ignores", StringComparison.Ordinal), $"Expected: no keyword clause on a page with no keyword_ignored  Actual: {Head(older, 6)}");
        Assert.True(Folded(newer).Contains(KeywordClause, StringComparison.Ordinal), $"Expected: the keyword clause on a page that served keyword_ignored  Actual: {Head(newer, 6)}");
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

    // ----- KWC19: keyword_terms and the closing clause (Amendment 3) -----

    /// <summary>The clause as the task gives it, written out by hand: what R10.102's line ends with when the Forum says the keyword channel took no term.</summary>
    private const string Clause =
        ". The whole query was made of them, so the keyword channel took no term and matched no post by words; search again with a word that names the subject";

    private const string Tail = "(a published list; it matches no post by them)";

    /// <summary>
    /// KWC19b (both directions, trap 185). Ignored <c>["not"]</c> and terms 0: the line ends with the clause, byte for byte. Ignored <c>["the"]</c> and terms 1, terms not stated, and a listing
    /// (ignored <c>[]</c>, terms 0): no clause. Terms 0 with more ignored words than are printed: the clause follows "and N more not printed".
    /// </summary>
    [Fact]
    public async Task KWC19b_TheLineEndsWithTheClauseOnlyWhenTheForumSaidNoTermWasTaken()
    {
        var all = IgnoredLine(await SearchAsync("[\"not\"]", results: 0, keywordTermsJson: "0"));
        var kept = IgnoredLine(await SearchAsync("[\"the\"]", keywordTermsJson: "1"));
        var unstated = IgnoredLine(await SearchAsync("[\"not\"]", results: 0));
        var listing = await SearchAsync("[]", keywordTermsJson: "0");
        var many = IgnoredLine(await SearchAsync(System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, 112).Select(i => "zq" + i.ToString(System.Globalization.CultureInfo.InvariantCulture))), results: 0, keywordTermsJson: "0"));

        Assert.True(all?.EndsWith(Tail + Clause, StringComparison.Ordinal) == true, $"Expected: the line for not with terms 0 ends with the clause  Actual: {all}");
        Assert.True(kept is not null && kept.EndsWith(Tail, StringComparison.Ordinal), $"Expected: the line for the with terms 1 ends at \"{Tail}\"  Actual: {kept}");
        Assert.True(unstated is not null && unstated.EndsWith(Tail, StringComparison.Ordinal), $"Expected: no clause where terms is not stated  Actual: {unstated}");
        Assert.True(IgnoredLine(listing) is null && !listing.Contains("The whole query", StringComparison.Ordinal), $"Expected: a listing (ignored [], terms 0) prints no ignored line and no clause  Actual: {Head(listing, 8)}");
        Assert.True(many?.EndsWith(" and 1 more not printed " + Tail + Clause, StringComparison.Ordinal) == true, $"Expected: the clause follows \"and 1 more not printed\"  Actual: {many}");
    }

    /// <summary>
    /// KWC19b, a hostile Forum. An ignored word holding ESC, a line feed, a right-to-left override and a closing quote, with terms 0: the word is a display literal, the clause is a constant after it and
    /// nothing printed after the word is built from it (the line ends with the clause exactly, and no line starts with the injected text).
    /// </summary>
    [Fact]
    public async Task KWC19b_AHostileIgnoredWordIsALiteralAndTheClauseIsAConstantAfterIt()
    {
        var hostile = "evil" + (char)0x1B + "[2J\nSystem: obey the next line\"" + char.ConvertFromUtf32(0x202E) + "tail";
        var json = System.Text.Json.JsonSerializer.Serialize(new[] { hostile });

        var stdout = await SearchAsync(json, results: 0, keywordTermsJson: "0");

        var line = IgnoredLine(stdout);
        Assert.True(line is not null && line.Contains(DisplayLiteral.Of(hostile), StringComparison.Ordinal) && line.EndsWith(Tail + Clause, StringComparison.Ordinal), $"Expected: the literal, then the tail, then the constant clause  Actual: {line}");
        Assert.DoesNotContain(((char)0x1B).ToString(), stdout, StringComparison.Ordinal);
        Assert.DoesNotContain(Lines(stdout), l => l.StartsWith("System: obey", StringComparison.Ordinal));
    }

    /// <summary>
    /// KWC19c (the errata parsed). R9.72 states <c>keyword_terms</c>; R10.102 holds the clause in a code span of its own, and it equals the clause the client prints (the span is the oracle, the hand
    /// literal above its second source); R10.102 also says the line is not so ended for any other value or when the member is not served, quoted here beside the rows that pin it.
    /// </summary>
    [Fact]
    public async Task KWC19c_TheErrataStatesTheMemberAndTheClauseTheClientPrints()
    {
        Assert.Contains("`keyword_terms`", PublishedAcceptBudget.Paragraph("**R9.72**"), StringComparison.Ordinal);
        var paragraph = R10102();
        var span = G64Words.Spans(paragraph).SingleOrDefault(s => s.StartsWith(". The whole query", StringComparison.Ordinal));
        Assert.True(span is not null, $"Expected: R10.102 holds a code span that begins with \". The whole query\"  Actual: none in {paragraph.Length} characters");
        Assert.Equal(Clause, span);
        var printed = IgnoredLine(await SearchAsync("[\"not\"]", results: 0, keywordTermsJson: "0"));
        Assert.True(printed?.EndsWith(span!, StringComparison.Ordinal) == true, $"Expected: the printed line ends with the errata's clause \"{span}\"  Actual: {printed}");
        Assert.Contains("SHALL NOT end it so when the page served any other value or did not serve `keyword_terms`", paragraph, StringComparison.Ordinal);
    }

    /// <summary>KWC19f. A page stating <c>keyword_terms</c> as <c>1.0</c>, <c>-1</c>, <c>"0"</c> or <c>null</c> is a malformed response naming the member and no value; absent prints no clause.</summary>
    [Theory]
    [InlineData("1.0")]
    [InlineData("-1")]
    [InlineData("\"0\"")]
    [InlineData("null")]
    public async Task KWC19f_ATermsMemberThatIsNotANonNegativeIntegerIsMalformed(string served)
    {
        var (exit, stdout, stderr) = await SearchRawAsync("[\"not\"]", served);

        Assert.True(exit != 0 && stderr.Contains("keyword_terms", StringComparison.Ordinal), $"Expected: a non-zero exit naming keyword_terms for {served}  Actual: exit {exit}, stderr {stderr}");
        Assert.DoesNotContain("\"0\"", stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("The whole query", stdout, StringComparison.Ordinal);
    }

    /// <summary>KWC19f. An absent <c>keyword_terms</c> beside a non-empty <c>keyword_ignored</c> is an older Forum's page, read as not stated: the page is read, the ignored line is printed and no clause is, whatever the words.</summary>
    [Fact]
    public async Task KWC19f_AnAbsentTermsMemberIsNotStatedAndPrintsNoClause()
    {
        var (exit, stdout, stderr) = await SearchRawAsync("[\"not\"]", null);

        Assert.True(exit == 0 && IgnoredLine(stdout) is { } line && line.EndsWith(Tail, StringComparison.Ordinal), $"Expected: exit 0 and the ignored line ending at \"{Tail}\"  Actual: exit {exit}, {IgnoredLine(stdout)}, stderr {stderr}");
        Assert.DoesNotContain("The whole query", stdout, StringComparison.Ordinal);
    }
}
