using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
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

    private const string Errata = "curia-whitepaper-ERRATA-AND-ADDENDUM.md";

    private static string Paragraph(string text, string anchor)
    {
        var start = text.IndexOf(anchor, StringComparison.Ordinal);
        Assert.True(start >= 0, $"the errata has no paragraph that begins {anchor}");
        var end = text.IndexOf("\n\n", start, StringComparison.Ordinal);
        return Regex.Replace(text[start..(end < 0 ? text.Length : end)], @"\s+", " ", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// The three member names R9.31 gives ("SHALL carry three members: ..."), read from the errata and not from the code, each
    /// checked to appear in R9.33's paragraph too. Sorted ordinally; the count is asserted before they are returned.
    /// </summary>
    private static string[] PublishedMembers()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, Errata))) dir = dir.Parent;
        Assert.NotNull(dir);

        var text = File.ReadAllText(Path.Combine(dir!.FullName, Errata));
        var sentence = Regex.Match(Paragraph(text, "**R9.31**"), @"three members: (?<list>.+?)\.", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5));
        Assert.True(sentence.Success, "R9.31 no longer says 'three members: ...'");

        var names = Regex.Matches(sentence.Groups["list"].Value, @"`(\w+)`", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(5))
            .Select(m => m.Groups[1].Value).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(3, names.Length);

        var r933 = Paragraph(text, "**R9.33**");
        foreach (var name in names)
            Assert.Contains($"`{name}`", r933, StringComparison.Ordinal);

        return names;
    }

    /// <summary>A value of the member's own type, as JSON, and what the document should hold for it once read.</summary>
    private static (string Json, string Expected) Sample(string member) => member switch
    {
        "published_default" => ("\"V0\"", "V0"),
        "requested_min_verification" => ("\"V1\"", "V1"),
        "clamped" => ("true", "True"),
        _ => throw new InvalidOperationException($"R9.31 names a member this fact has no sample for: {member}"),
    };

    private static string Pascal(string member) =>
        string.Concat(member.Split('_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    [Fact]
    public async Task R9_33_AReaderCarriesTheThreeMembersR931Names()
    {
        var names = PublishedMembers();
        var tail = string.Concat(names.Select(n => $",\"{n}\":{Sample(n).Json}"));

        var found = await SearchAsync(Page(tail));

        Assert.True(found.TryGetValue(out var page, out var refusal), refusal?.Summary);
        var read = names.ToDictionary(
            n => n,
            n => typeof(RetrievalFloorDocument).GetProperty(Pascal(n))?.GetValue(page.Floor)?.ToString());
        var served = names.ToDictionary(n => n, n => (string?)Sample(n).Expected);

        Assert.Equal(served.OrderBy(p => p.Key, StringComparer.Ordinal), read.OrderBy(p => p.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task R9_33_AnAbsentBlockIsNotStated()
    {
        var found = await SearchAsync(Page(""));

        Assert.True(found.TryGetValue(out var page, out var refusal), refusal?.Summary);
        Assert.False(page.Floor.ClampStated);
        Assert.Null(page.Floor.Clamped);
        Assert.Null(page.Floor.PublishedDefault);
        Assert.Null(page.Floor.RequestedMinVerification);
    }

    [Fact]
    public async Task R9_33_ANullRequestIsStatedAndIsNone()
    {
        var found = await SearchAsync(Page(""","published_default":"V0","requested_min_verification":null,"clamped":false"""));

        Assert.True(found.TryGetValue(out var page, out var refusal), refusal?.Summary);
        Assert.True(page.Floor.ClampStated);
        Assert.Null(page.Floor.RequestedMinVerification);
        Assert.False(page.Floor.Clamped);
        Assert.Equal("V0", page.Floor.PublishedDefault);
    }

    [Fact]
    public async Task R9_33_ANullRequestAloneIsStated()
    {
        var found = await SearchAsync(Page(""","requested_min_verification":null"""));

        Assert.True(found.TryGetValue(out var page, out var refusal), refusal?.Summary);
        Assert.True(page.Floor.ClampStated);
        Assert.Null(page.Floor.RequestedMinVerification);
        Assert.Null(page.Floor.Clamped);
        Assert.Null(page.Floor.PublishedDefault);
    }

    [Theory]
    [InlineData(""","published_default":"V0","requested_min_verification":null,"clamped":"no" """)]
    [InlineData(""","published_default":"V0","requested_min_verification":null,"clamped":1""")]
    [InlineData(""","published_default":3,"requested_min_verification":null,"clamped":false""")]
    [InlineData(""","published_default":"V0","requested_min_verification":5,"clamped":false""")]
    public async Task R9_33_AMemberThatIsPresentAndMistypedIsAMalformedResponse(string tail)
    {
        var found = await SearchAsync(Page(tail));

        Assert.False(found.TryGetValue(out _, out var refusal), "a mistyped clamp member was read as a page");
        Assert.Equal(RefusalKind.Malformed, refusal!.Kind);
        Assert.Contains("R9.33", refusal.Error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class Scripted(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
