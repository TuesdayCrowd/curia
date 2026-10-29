using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Curia.Canon.Json;
using Curia.Client;
using Curia.Domain.Serving;
using Curia.Tests.Shared;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Curia.Mcp.Tests;

/// <summary>
/// PR 3 (errata G47, R11.63), probes CHN48 to CHN51 and CHN59 at the adapter: <c>curia_search</c>'s first block states <c>applies_to</c> and
/// <c>not_applicable_to</c> as the Forum served them, each element a display literal, <c>not stated</c> for a member it did not serve, a member that is not
/// an array of strings is a malformed search page and not an empty list, and <c>curia-mcp</c> is the version G47 names. The member names and the word are
/// R11.63's code spans and the version is the errata's index row, read by <see cref="PublishedChainWords"/>; the page is served by the stub, whose
/// <see cref="StubLog.FloorListsJson"/> knob writes each row's lists, so the oracle is the JSON the row wrote and the kind of the result is read from the
/// result the tool presented. The CLI's half is <c>FloorListsClientTests</c> (CHN50, CHN58, CHN59).
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class FloorKindsTests : IDisposable
{
    private readonly StubLog _log = new();
    private readonly string _home = Directory.CreateTempSubdirectory("curia-mcp-g47-floor-").FullName;

    public void Dispose()
    {
        _log.Dispose();
        Directory.Delete(_home, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<CallToolResult> SearchAsync(string? listsJson)
    {
        _log.FloorListsJson = listsJson;
        return await new ForumTools(_log.Client(), MarkingMode.None, new HeadStore(_home)).SearchAsync(new SearchCriteria(), Ct);
    }

    private static string FirstBlock(CallToolResult result) => Assert.IsType<TextContentBlock>(result.Content[0]).Text;

    /// <summary>The kinds of the results the tool presented, read from the passages' own <c>kind</c> lines.</summary>
    private static string[] KindsOfResults(CallToolResult result) =>
        [.. result.Content.OfType<EmbeddedResourceBlock>()
            .Select(b => ((TextResourceContents)b.Resource).Text)
            .Select(t => Regex.Match(t, "^kind\\s+\"([^\"]*)\"", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)).Groups[1].Value)];

    /// <summary>The elements of the list that follows <paramref name="member"/>= in the block, as literals, or the words <c>not stated</c>; null where the member is not in the block.</summary>
    private static string[]? ListOf(string block, string member)
    {
        var at = block.IndexOf(member + "=", StringComparison.Ordinal);
        if (at < 0) return null;
        var tail = block[(at + member.Length + 1)..];
        if (tail.StartsWith("not stated", StringComparison.Ordinal)) return ["not stated"];
        var match = Regex.Match(tail, "^(\"(?:[^\"\\\\]|\\\\.)*\"(?:,\"(?:[^\"\\\\]|\\\\.)*\")*)?", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        return [.. Regex.Matches(match.Value, "\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5)).Select(m => m.Value)];
    }

    /// <summary>
    /// CHN48. The block states both lists as the Forum served them, and each kind of the results is in one of the two and in neither both. The result's
    /// kind is read from the result the tool presented, so a kind added to the stub's page is checked without a change here.
    /// </summary>
    [Fact]
    public async Task R11_63_CHN48_TheBlockStatesBothLists()
    {
        var (applies, notApplicable, _) = PublishedChainWords.FloorLists();
        var result = await SearchAsync("\"applies_to\":[\"answer\",\"finding\"],\"not_applicable_to\":[\"question\",\"comment\",\"revision\"]");
        var block = FirstBlock(result);

        var kinds = KindsOfResults(result);
        Assert.NotEmpty(kinds);
        var inApplies = ListOf(block, applies) ?? [];
        var inNot = ListOf(block, notApplicable);

        Assert.True(inNot is not null, $"Expected: the block states {notApplicable} Actual: {block}");
        Assert.Equal(["\"answer\"", "\"finding\""], inApplies);
        Assert.Equal(["\"question\"", "\"comment\"", "\"revision\""], inNot);
        foreach (var kind in kinds)
        {
            var literal = DisplayLiteral.Of(kind);
            Assert.True(inApplies.Contains(literal) ^ inNot!.Contains(literal), $"the result's kind {literal} is in {(inApplies.Contains(literal) && inNot.Contains(literal) ? "both lists" : "neither list")}");
        }
    }

    /// <summary>CHN49. Each element is a display literal: a kind with a quote and a newline in it is escaped, not printed raw.</summary>
    [Fact]
    public async Task R11_63_CHN49_EachElementIsADisplayLiteral()
    {
        var hostile = "a\"b\nc";
        var json = System.Text.Json.JsonSerializer.Serialize(hostile);
        var block = FirstBlock(await SearchAsync($"\"applies_to\":[{json}],\"not_applicable_to\":[{json}]"));

        var elements = ListOf(block, PublishedChainWords.FloorLists().NotApplicableTo);

        Assert.True(elements is not null, $"Expected: not_applicable_to in the block Actual: {block}");
        Assert.Equal([DisplayLiteral.Of(hostile)], elements);
        Assert.DoesNotContain(hostile, block, StringComparison.Ordinal);
    }

    /// <summary>CHN50, the adapter's half. An absent member is <c>not stated</c>, never an empty list and never silence.</summary>
    [Theory]
    [InlineData("\"applies_to\":[\"answer\"]", "not_applicable_to")]
    [InlineData("\"not_applicable_to\":[\"question\"]", "applies_to")]
    [InlineData("", "applies_to")]
    [InlineData("", "not_applicable_to")]
    public async Task R11_63_CHN50_AnAbsentListIsNotStated(string served, string absent)
    {
        ArgumentNullException.ThrowIfNull(absent);
        var block = FirstBlock(await SearchAsync(served));

        var list = ListOf(block, absent);

        Assert.True(list is not null && list.SequenceEqual([PublishedChainWords.FloorLists().NotStated]), $"Expected: {absent}={PublishedChainWords.FloorLists().NotStated} Actual: {(list is null ? "no " + absent + " in the block" : string.Join(",", list))}");
    }

    /// <summary>CHN59, the adapter's half. A list that is a string, an object, <c>null</c>, or an array holding a non-string is a malformed search page, not an empty list.</summary>
    [Theory]
    [InlineData("\"applies_to\":\"answer\",\"not_applicable_to\":[]")]
    [InlineData("\"applies_to\":{},\"not_applicable_to\":[]")]
    [InlineData("\"applies_to\":null,\"not_applicable_to\":[]")]
    [InlineData("\"applies_to\":[\"answer\",7],\"not_applicable_to\":[]")]
    [InlineData("\"applies_to\":[],\"not_applicable_to\":\"question\"")]
    [InlineData("\"applies_to\":[],\"not_applicable_to\":null")]
    public async Task R11_63_CHN59_AListThatIsNoArrayOfStringsIsAMalformedSearchPage(string lists)
    {
        string outcome;
        try
        {
            var result = await SearchAsync(lists);
            outcome = result.IsError == true ? "error" : "a block: " + FirstBlock(result);
        }
        catch (ModelContextProtocol.McpException)
        {
            outcome = "error";
        }

        Assert.True(outcome == "error", $"Expected: a malformed search page Actual: {outcome}");
    }

    /// <summary>CHN51. <c>curia-mcp</c> is the version G47's index row names.</summary>
    [Fact]
    public void R11_63_CHN51_TheVersionIsTheOneTheErrataNames()
    {
        var published = PublishedChainWords.Version();
        Assert.True(string.Equals(published, ToolCatalogue.Version, StringComparison.Ordinal), $"Expected: \"{published}\" Actual: \"{ToolCatalogue.Version}\"");
    }
}
