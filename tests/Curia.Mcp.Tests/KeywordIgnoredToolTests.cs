using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// Errata G81 (R11.108) at the adapter, plan KWC13: <c>curia_search</c>'s first block carries R10.102's line and gloss from the one definition <c>curia search</c> prints them from, for a page that
/// served <c>keyword_ignored</c> and not otherwise; its description says the keyword channel ignores a published list of common English words; <c>curia-mcp</c> is 0.12.8.
///
/// <para>The Forum is <see cref="StubLog"/>'s; a handler writes the page a fact needs. The oracle is the words the errata gives R10.102 (read at test time) and what the fact set on the page.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeywordIgnoredToolTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-kwc-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string TodaysGloss = "a common word such as 'the' or 'of' included";

    private static readonly string[] GlossPhrases =
    [
        "other than the common English words the keyword channel ignores",
        "can still match",
        "does not show that the Forum holds the topic",
    ];

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

    private async Task<string> FirstBlockAsync(string? keywordIgnoredJson, int results)
    {
        using var log = new StubLog();
        using var rewriter = new PageRewriter(log.Handler, results, keywordIgnoredJson);
        using var http = new HttpClient(rewriter, disposeHandler: false) { BaseAddress = StubLog.Forum };
        var tools = new ForumTools(new ForumClient(http, StubLog.Forum), MarkingMode.None, new HeadStore(_home));

        var result = await tools.SearchAsync(new SearchCriteria(), TestContext.Current.CancellationToken);

        return Assert.IsType<TextContentBlock>(result.Content[0]).Text;
    }

    private static (string Before, string After) LineParts()
    {
        var span = PublishedAcceptBudget.Paragraph("**R10.102**");
        var code = System.Text.RegularExpressions.Regex.Matches(span, "`([^`]+)`").Select(m => m.Groups[1].Value).Single(s => s.Contains("<words>", StringComparison.Ordinal));
        var at = code.IndexOf("<words>", StringComparison.Ordinal);
        return (code[..at].Trim(), code[(at + "<words>".Length)..].Trim());
    }

    private static string Head(string text) => text.Length > 200 ? text[..200] + "..." : text;

    /// <summary>
    /// KWC13. A page that served <c>["how","the"]</c>: the first block holds R10.102's line (its opening words, each word as a display literal, its closing words) before the count line, then the gloss with
    /// the errata's three claims and not today's sentence. The line and the gloss are asked of <c>SearchWording</c>, so <c>curia</c> prints the same by construction; the CLI's own fact is KWC11.
    /// </summary>
    [Fact]
    public async Task KWC13_TheFirstBlockCarriesTheIgnoredLineBeforeTheCountLineAndTheNewGloss()
    {
        var block = await FirstBlockAsync("[\"how\",\"the\"]", results: 1);

        var (before, after) = LineParts();
        var line = block.IndexOf(before, StringComparison.Ordinal);
        var count = block.IndexOf("post(s) on this page", StringComparison.Ordinal);
        Assert.True(line >= 0, $"Expected: the first block holds \"{before}\"  Actual: {Head(block)}");
        Assert.True(line < count, $"Expected: the ignored line before the count line  Actual: line at {line}, count line at {count}");
        Assert.Contains(DisplayLiteral.Of("how"), block[line..], StringComparison.Ordinal);
        Assert.Contains(after, block[line..], StringComparison.Ordinal);
        foreach (var phrase in GlossPhrases)
            Assert.True(block.Contains(phrase, StringComparison.Ordinal), $"Expected: the first block holds \"{phrase}\"  Actual: {Head(block)}");
        Assert.DoesNotContain(TodaysGloss, block, StringComparison.Ordinal);
    }

    /// <summary>KWC13. A page that did not serve the member keeps today's gloss and prints no ignored line: an older Forum is not made to claim an ignoring it does not do.</summary>
    [Fact]
    public async Task KWC13_APageWithoutTheMemberKeepsTodaysGlossAndNoLine()
    {
        var block = await FirstBlockAsync(null, results: 1);

        Assert.Contains(TodaysGloss, block, StringComparison.Ordinal);
        Assert.DoesNotContain(LineParts().Before, block, StringComparison.Ordinal);
    }

    /// <summary>KWC13. An all-ignored query's empty page: the first block still says what was ignored (the empty page is where the only useful sentence is).</summary>
    [Fact]
    public async Task KWC13_AnEmptyPageAfterAnAllIgnoredQueryStatesTheIgnoredWords()
    {
        var block = await FirstBlockAsync("[\"the\"]", results: 0);

        Assert.True(block.Contains(LineParts().Before, StringComparison.Ordinal), $"Expected: the first block holds \"{LineParts().Before}\"  Actual: {Head(block)}");
    }

    /// <summary>KWC13. The tool's description says that the keyword channel ignores a published list of common English words (R11.108), read from the registered catalogue, which composes it with no Forum call.</summary>
    [Fact]
    public void KWC13_TheDescriptionSaysTheKeywordChannelIgnoresAPublishedList()
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        var tools = ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, new HeadStore(Path.Combine(_home, "never-written"))));
        var description = tools.Single(t => t.ProtocolTool.Name == "curia_search").ProtocolTool.Description ?? string.Empty;

        Assert.True(
            description.Contains("common English words", StringComparison.Ordinal) && description.Contains("published list", StringComparison.Ordinal),
            $"Expected: curia_search's description says the keyword channel ignores a published list of common English words  Actual: neither \"common English words\" nor \"published list\" in a description of {description.Length} characters");
    }

    /// <summary>KWC13. <c>curia-mcp</c> is 0.12.8 (R11.108), the hand-written twin of the errata's sentence, which moves only when someone means the version to move.</summary>
    [Fact]
    public void KWC13_TheVersionIs0128() => Assert.Equal("0.12.8", ToolCatalogue.Version);
}
