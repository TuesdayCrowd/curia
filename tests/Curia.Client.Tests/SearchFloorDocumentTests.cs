using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using Curia.Client;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// R9.30 from the client's side: <c>excluded_by_floor</c> is kept when the Forum serves it, is told apart
/// from <c>0</c> when the Forum does not, and a value that is no count is a malformed response, not a
/// number the client has chosen. The floor block is scripted, so the oracle is the JSON this fact
/// writes, not the client's reading of it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchFloorDocumentTests
{
    private static string Page(string floorTail) =>
        """{"results":[],"floor":{"surface":"rest-search","min_verification":"V1","source":"requested","applies_to":["answer","finding"],"not_applicable_to":["question","comment","revision"]""" +
        floorTail + """},"model":"hashed-ngram@1","corpus_bound":9,"k":60,"candidate_depth":200,"min_cosine_bp":2000}""";

    private static async Task<ForumResult<SearchPage>> SearchAsync(string body)
    {
        using var handler = new Scripted(body);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://forum.test") };
        var client = new ForumClient(http, new Uri("http://forum.test"));
        return await client.SearchAsync(new SearchRequest("jcs") { MinVerification = "V1" }, MarkingMode.None, CancellationToken.None);
    }

    [Fact]
    public async Task R9_30_AClientKeepsTheCountsTheFloorBlockServes()
    {
        var found = await SearchAsync(Page(""","excluded_by_floor":4,"excluded_disputed":2"""));

        Assert.True(found.TryGetValue(out var page, out var refusal), refusal?.Summary);
        Assert.Equal((4, 2), (page.Floor.ExcludedByFloor, page.Floor.ExcludedDisputed));
    }

    [Theory]
    [InlineData("", null)]
    [InlineData(""","excluded_by_floor":0""", 0)]
    [InlineData(""","excluded_by_floor":4""", 4)]
    public async Task R9_30_AClientDistinguishesANotServedCountFromZero(string tail, int? expected)
    {
        var found = await SearchAsync(Page(tail));

        Assert.True(found.TryGetValue(out var page, out var refusal), refusal?.Summary);
        Assert.Equal(expected, page.Floor.ExcludedByFloor);
    }

    [Theory]
    [InlineData(""","excluded_by_floor":-1""")]
    [InlineData(""","excluded_by_floor":"4" """)]
    [InlineData(""","excluded_by_floor":true""")]
    public async Task R9_30_AValueThatIsNoCountIsAMalformedResponse(string tail)
    {
        var found = await SearchAsync(Page(tail));

        Assert.False(found.TryGetValue(out _, out var refusal), "a floor count that is no count was read as a page");
        Assert.Equal(RefusalKind.Malformed, refusal!.Kind);
        Assert.Contains("R9.30", refusal.Error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// A fraction never reaches the floor reader: the client's ADMIT profile (R6.33) refuses a non-integer
    /// number in any response first, so the refusal names that rule and not R9.30. Green on the unmodified
    /// tree, and kept so that the integer rule is not lost to a reader that coerces.
    /// </summary>
    [Fact]
    public async Task R9_30_AFractionalCountIsMalformedBeforeTheFloorIsRead()
    {
        var found = await SearchAsync(Page(""","excluded_by_floor":4.5"""));

        Assert.False(found.TryGetValue(out _, out var refusal), "a fractional floor count was read as a page");
        Assert.Equal(RefusalKind.Malformed, refusal!.Kind);
    }

    private sealed class Scripted(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
