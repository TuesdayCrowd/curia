using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Mcp;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// R11.103 and R10.99 (errata G64) at the adapter: PR 8's probes MBC6 and DVW6 (the Mcp half). <c>curia_search</c>'s first block states what <c>curia search</c> prints, from the same
/// definitions, the count line before the served model's limits and those limits whole, and the tool's description says that <c>limit</c> moves membership. The Forum is
/// <see cref="StubLog"/>'s, whose page carries one hit; a handler in front of it writes the page a fact needs (three hits, each with the mark it names, and <c>by_channel</c> and
/// <c>diversified</c> as raw JSON). The oracle is what the fact set on the page, the words R10.99 and R11.103 give (read at test time from the errata), and
/// <see cref="ModelWording.Sentence(string?)"/>, which MBC6 holds byte for byte because R11.103 says the MCP's words are the CLI's.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class SearchChannelToolTests : IDisposable
{
    private const string Bge = "bge-small-en-v1.5@1";

    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-channel-").FullName;

    public void Dispose()
    {
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The count line of a page of three results, one of each kind: the same literal <c>SearchWordingTests.CountLine(3, 1, 1, 1)</c> writes for the CLI (the one literal pairing the two).</summary>
    private const string CountLineOfThree = "3 post(s) on this page: 1 by words, 1 by meaning only, 1 by words and meaning";

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

    private async Task<string> FirstBlockAsync(string? modelJson, string?[] marks, string? byChannelJson, string? diversifiedJson)
    {
        using var log = new StubLog { SearchModelJson = modelJson };
        using var rewriter = new PageRewriter(log.Handler, marks, byChannelJson, diversifiedJson);
        using var http = new HttpClient(rewriter, disposeHandler: false) { BaseAddress = StubLog.Forum };
        var tools = new ForumTools(new ForumClient(http, StubLog.Forum), MarkingMode.None, new HeadStore(_home));

        var result = await tools.SearchAsync(new SearchCriteria(), TestContext.Current.CancellationToken);

        return Assert.IsType<TextContentBlock>(result.Content[0]).Text;
    }

    private static string Paragraph(string anchor) => PublishedAcceptBudget.Paragraph(anchor);

    private static string Phrase(string paragraph, string phrase) =>
        paragraph.Contains(phrase, StringComparison.Ordinal) ? phrase : throw new InvalidOperationException($"the paragraph does not hold \"{phrase}\": {paragraph}");

    private static string Head(string text) => text.Length > 160 ? text[..160] + "..." : text;

    private static string Shown(string block, params string[] needles) =>
        string.Join(" ", needles.Select(n => $"[{Head(n)}]@{block.IndexOf(n, StringComparison.Ordinal).ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>
    /// MBC6. R11.103: the first block holds <c>ModelWording.Sentence(model)</c> byte for byte, the count line (the literal the CLI's test holds for the same page), the diversified line, the
    /// gloss of a `words` mark and the sentence that the score is not a cosine, and (Amendment 1, ruling 7(a)) in this order: the adapter's own <c>key=value</c> line, the count line, the
    /// diversified line, the gloss, the score sentence, then the model's sentence last and whole, so a reader that truncates a long block loses the sentence that says what the floor does
    /// not do and not the one that says which results the vector channel chose.
    /// </summary>
    [Fact]
    public async Task MBC6_TheFirstBlockStatesTheCountsTheMarksTheGlossAndTheModelInThatOrder()
    {
        var block = await FirstBlockAsync("\"" + Bge + "\"", ["words", "meaning", "words_and_meaning"], "{\"words\":1,\"meaning\":1,\"words_and_meaning\":1}", "2");

        var model = ModelWording.Sentence(Bge);
        var gloss = Phrase(Paragraph("**R10.99**"), "a common word such as");
        var cosine = Phrase(Paragraph("**R10.99**"), "not a cosine");
        var diversified = "diversified 2 post(s)";

        // Every piece is present: absent is -1 and fails here with the piece named.
        string[] pieces = ["results=3", CountLineOfThree, diversified, gloss, cosine, model];
        var positions = pieces.Select(p => block.IndexOf(p, StringComparison.Ordinal)).ToArray();
        Assert.True(positions.All(p => p >= 0), $"Expected: the first block holds, in order, results=3, the count line, the diversified line, the gloss, the score sentence and the model sentence  Actual: {Shown(block, pieces)}");
        Assert.True(positions.SequenceEqual(positions.Order()), $"Expected: those pieces in ascending order, the model sentence last  Actual: {Shown(block, pieces)}");
    }

    /// <summary>MBC6: a Forum that served no model id reads `limits NOT STATED`, and the count line still comes before it (the page's own words are not lost with the model's).</summary>
    [Fact]
    public async Task MBC6_AForumThatServedNoModelReadsLimitsNotStatedAfterTheCountLine()
    {
        var block = await FirstBlockAsync("", ["words", "meaning", "words_and_meaning"], "{\"words\":1,\"meaning\":1,\"words_and_meaning\":1}", "0");

        var sentence = ModelWording.Sentence(null);
        Assert.Contains("limits NOT STATED", sentence, StringComparison.Ordinal);
        var counts = block.IndexOf(CountLineOfThree, StringComparison.Ordinal);
        var limits = block.IndexOf(sentence, StringComparison.Ordinal);
        Assert.True(counts >= 0 && limits > counts, $"Expected: the count line before \"{Head(sentence)}\"  Actual: {Shown(block, CountLineOfThree, sentence)}");
    }

    /// <summary>
    /// MBC6 (description). R11.103: the descriptions of `curia search` and `curia_search` say that `limit` moves membership. The sentence's words are R11.103's own
    /// ("`limit` moves membership"); the tool's description is read from the registered catalogue, which composes it with no Forum call.
    /// </summary>
    [Fact]
    public void MBC6_TheDescriptionSaysLimitMovesMembership()
    {
        using var http = new HttpClient();
        var client = new ForumClient(http, new Uri("https://forum.invalid/", UriKind.Absolute));
        var tools = ToolCatalogue.Build(new ForumTools(client, MarkingMode.Datamark, new HeadStore(Path.Combine(_home, "never-written"))));
        var description = tools.Single(t => t.ProtocolTool.Name == "curia_search").ProtocolTool.Description ?? string.Empty;

        var said = Phrase(Paragraph("**R11.103**"), "`limit` moves membership").Replace("`", string.Empty, StringComparison.Ordinal);
        Assert.True(
            description.Contains("membership", StringComparison.OrdinalIgnoreCase),
            $"Expected: curia_search's description says \"{said}\"  Actual: no \"membership\" in a description of {description.Length} characters");
    }

    /// <summary>
    /// MBC6 (version). R11.103 names the version: <c>`curia-mcp` is X.Y.Z</c>. Read through the one parser, <c>PublishedReadersLastWords.Version()</c> (item 344);
    /// the catalogue's constant is what <c>initialize</c> serves as <c>serverInfo.version</c>.
    /// </summary>
    [Fact]
    public void MBC6_TheCatalogueIsBumpedToWhatR11103Names()
    {
        var named = PublishedReadersLastWords.Version();

        Assert.True(named == ToolCatalogue.Version, $"Expected: {named}  Actual: {ToolCatalogue.Version}");
    }

    /// <summary>DVW6 (Mcp half): the diversified line at 0 as at N, and `not stated` for an older Forum, never 0.</summary>
    [Theory]
    [InlineData("0", "diversified 0 post(s)")]
    [InlineData("4", "diversified 4 post(s)")]
    public async Task DVW6_TheFirstBlockStatesDiversifiedAtZeroAsAtN(string served, string expected)
    {
        var block = await FirstBlockAsync("\"" + Bge + "\"", ["words"], "{\"words\":1,\"meaning\":0,\"words_and_meaning\":0}", served);

        Assert.True(block.Contains(expected, StringComparison.Ordinal), $"Expected: the first block holds \"{expected}\"  Actual: {Head(block)}");
    }

    [Fact]
    public async Task DVW6_AnOlderForumsDiversifiedReadsNotStated()
    {
        var block = await FirstBlockAsync("\"" + Bge + "\"", ["words"], "{\"words\":1,\"meaning\":0,\"words_and_meaning\":0}", null);

        var at = block.IndexOf("diversified", StringComparison.Ordinal);
        var line = at < 0 ? null : block[at..].Split('\n')[0];
        Assert.True(
            line is not null && line.Contains("not stated", StringComparison.OrdinalIgnoreCase) && !line.Contains("diversified 0", StringComparison.Ordinal),
            $"Expected: a diversified line reading not stated  Actual: {line ?? "no diversified line in " + Head(block)}");
    }
}
