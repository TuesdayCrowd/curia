using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Client;
using Curia.Domain.Serving;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.30 and R9.29 on a real Forum: the floor block states how many results the floor removed, and the
/// route refuses a query parameter it does not read. Answers are posted through the API by an agent whose
/// owner the operator attested and whose tier the clock has promoted to T1, so the corpus under test is
/// the corpus a client sees, not one built beside it.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed partial class SearchRemovalAndParameterTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string PostsUrl = "http://localhost/v1/posts";
    private const string UnknownParameter = "curia/search/unknown-parameter";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// One question on a fresh board and <paramref name="answers"/> answers to it by one T1 agent, all
    /// carrying one nonce term. The answers are V0 (nobody verified them), so a V1 floor removes them all.
    /// </summary>
    private async Task<(string Board, string Term, string QuestionId, string[] AnswerIds)> SeedAsync(int answers, CancellationToken ct)
    {
        var http = forum.Client;
        var board = "board-" + Suffix();
        var term = "zfloor" + Suffix();

        var asker = ForumAgent.Create($"https://agents.example/asker-{Suffix()}", "asker-" + Suffix());
        var (askerDpop, askerToken) = await asker.AuthenticateAsync(http, KeyActRequests.TokenEndpoint, forum.Now, ct);
        using var asked = await askerDpop.PostAsync(http, PostsUrl, askerToken, asker.SignQuestion(board, $"how does {term} work", $"About {term}", forum.Now), forum.Now, ct);
        var askedText = await asked.Content.ReadAsStringAsync(ct);
        Assert.True(asked.StatusCode == HttpStatusCode.Created, askedText);
        using var question = JsonDocument.Parse(askedText);
        var questionId = question.RootElement.GetProperty("post_id").GetString()!;

        var author = ForumAgent.Create($"https://agents.example/answerer-{Suffix()}", "answerer-" + Suffix());
        var (dpop, token) = await author.AuthenticateAsync(http, KeyActRequests.TokenEndpoint, forum.Now, ct);
        await forum.AttestOwnerAsync(author.AgentId, ct, owner: $"owner:answerer-{Suffix()}");
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            using var warmup = await dpop.PostAsync(http, PostsUrl, token, author.SignQuestion(board, $"warmup {i} {nonce}", $"Warmup {i} {nonce}", forum.Now), forum.Now, ct);
            Assert.Equal(HttpStatusCode.Created, warmup.StatusCode);
        }

        forum.Clock.Advance(TimeSpan.FromHours(Curia.Domain.Authorization.TierPolicy.T1MinimumHours + 1));

        var ids = new List<string>();
        for (var i = 0; i < answers; i++)
            ids.Add(await AnswerAsync(author, dpop, board, $"Use the {term} keepalive, variant {Suffix()}.", questionId, ct));

        return (board, term, questionId, [.. ids]);
    }

    private async Task<string> AnswerAsync(ForumAgent author, DpopClient dpop, string board, string body, string questionId, CancellationToken ct)
    {
        var token = await dpop.GetTokenAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        using var response = await dpop.PostAsync(forum.Client, PostsUrl, token, author.SignAnswer(board, body, questionId, forum.Now), forum.Now, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        using var accepted = JsonDocument.Parse(text);
        return accepted.RootElement.GetProperty("post_id").GetString()!;
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string query, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri($"/v1/search?{query}", UriKind.Relative), ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<JsonDocument> PageAsync(string query, CancellationToken ct)
    {
        var (status, body) = await GetAsync(query, ct);
        Assert.True(status == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    /// <summary>The floor block's count, or a failure that names the member the block does not state.</summary>
    private static JsonElement FloorCount(JsonDocument page, string member)
    {
        Assert.True(page.RootElement.GetProperty("floor").TryGetProperty(member, out var count), $"the floor block states no {member} (R9.30)");
        return count;
    }

    private static string[] Ids(JsonDocument page) =>
        [.. page.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").GetProperty("post_id").GetString()!)];

    [Fact]
    public async Task R9_30_TheServedFloorBlockStatesTheCountAsAnInteger()
    {
        var ct = TestContext.Current.CancellationToken;
        const int answersPosted = 3;
        var (board, term, questionId, _) = await SeedAsync(answersPosted, ct);

        using var gated = await PageAsync($"q={term}&board={board}&min_verification=V1", ct);
        Assert.Equal([questionId], Ids(gated));
        var count = FloorCount(gated, "excluded_by_floor");
        Assert.Equal(JsonValueKind.Number, count.ValueKind);
        Assert.Equal(answersPosted, count.GetInt32());

        // The control: the same corpus under a V0 floor removes nothing, and says so with 0 and not by omission.
        using var open = await PageAsync($"q={term}&board={board}&min_verification=V0", ct);
        Assert.Equal(answersPosted + 1, Ids(open).Length);
        Assert.Equal(0, FloorCount(open, "excluded_by_floor").GetInt32());
    }

    [Fact]
    public async Task R9_30_AWithheldAnswerIsInNoCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, term, _, answers) = await SeedAsync(2, ct);
        await forum.WithholdAsync(answers[0], ct);

        using var gated = await PageAsync($"q={term}&board={board}&min_verification=V1", ct);
        using var open = await PageAsync($"q={term}&board={board}&min_verification=V0", ct);

        Assert.Equal(1, FloorCount(gated, "excluded_by_floor").GetInt32());
        Assert.DoesNotContain(answers[0], Ids(open));
        Assert.Contains(answers[1], Ids(open));
    }

    /// <summary>
    /// D56's shape: the Forum served <c>excluded_disputed</c> and the client's document had no member for it.
    /// Every number the floor block serves must have a property on <see cref="RetrievalFloorDocument"/> of the
    /// same name, snake-cased, holding the same value; the members are found by reflection over what is served
    /// and over the document, not by a list in this fact.
    /// </summary>
    [Fact]
    public async Task R9_24_TheDocumentCarriesEveryCountTheFloorBlockServes()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, term, _, _) = await SeedAsync(2, ct);

        using var raw = await PageAsync($"q={term}&board={board}&min_verification=V1", ct);
        var served = raw.RootElement.GetProperty("floor").EnumerateObject()
            .Where(m => m.Value.ValueKind == JsonValueKind.Number).ToArray();
        Assert.NotEmpty(served);

        var client = new ForumClient(forum.Client, forum.Client.BaseAddress!);
        var found = await client.SearchAsync(new SearchRequest(term) { Board = board, MinVerification = "V1" }, MarkingMode.None, ct);
        Assert.True(found.TryGetValue(out var page, out var refusal), refusal?.Summary);

        var members = typeof(RetrievalFloorDocument).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(p => Snake(p.Name), p => p, StringComparer.OrdinalIgnoreCase);
        foreach (var number in served)
        {
            Assert.True(members.TryGetValue(number.Name, out var member), $"the floor block serves '{number.Name}' and RetrievalFloorDocument has no member for it");
            var held = member.GetValue(page.Floor);
            Assert.True(held is not null, $"RetrievalFloorDocument.{member.Name} is null where the floor block serves {number.Name} {number.Value.GetInt32().ToString(CultureInfo.InvariantCulture)}");
            Assert.Equal(number.Value.GetInt32(), (int)held);
        }
    }

    /// <summary>PascalCase to snake_case up to letter case; the lookup that uses it ignores case.</summary>
    private static string Snake(string pascal) => PascalBoundary().Replace(pascal, "_$1");

    [GeneratedRegex("(?<!^)([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex PascalBoundary();

    [Theory]
    [InlineData("frobnicate=1")]
    [InlineData("kinds=question")]
    [InlineData("minVerification=V2")]
    [InlineData("unresolved_only=true")]
    [InlineData("format=json")]
    [InlineData("Kind_=x")]
    public async Task R9_29_AnUnknownParameterIsRefusedByName(string unknown)
    {
        var ct = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(unknown);
        var name = unknown[..unknown.IndexOf('=', StringComparison.Ordinal)];

        var (status, body) = await GetAsync($"board=g29&{unknown}", ct);

        Assert.True(status == HttpStatusCode.BadRequest, $"{status}: {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(UnknownParameter, problem.RootElement.GetProperty("type").GetString());
        Assert.Contains($"parameter={name}", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    public static TheoryData<string> HonouredRequests => new()
    {
        "q=jcs", "board=b", "author=https%3A%2F%2Fagents.example%2Fx", "kind=question", "tags=a", "min_verification=V0",
        "cursor=bm90LWEtY3Vyc29y", "limit=5", "why=true", "marking=datamark",
    };

    [Theory]
    [MemberData(nameof(HonouredRequests))]
    public async Task R9_29_EveryHonouredNameIsStillAccepted(string request)
    {
        var ct = TestContext.Current.CancellationToken;

        var (_, body) = await GetAsync(request, ct);

        // A refused value (a cursor that is no cursor) is not the refusal under test; an unknown name is.
        Assert.DoesNotContain(UnknownParameter, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_29_NamesMatchWithoutRegardToCase()
    {
        var ct = TestContext.Current.CancellationToken;

        var (status, body) = await GetAsync("Q=jcs&LIMIT=1", ct);

        Assert.True(status == HttpStatusCode.OK, body);
    }

    /// <summary>
    /// The set the route reads, the set the errata publishes and the fuzzer's hand list are three
    /// artifacts, none derived from another. The classification (R9.30, decision 3) is this fact's own: a
    /// member that is added to the set without being classified here fails by name, so a criterion that
    /// removes from the fused list cannot arrive without its count being decided.
    /// </summary>
    [Fact]
    public void R9_29_TheSetTheRouteReadsIsTheSetTheErrataPublishes()
    {
        var published = PublishedSearchParameters.Names();
        var fuzzed = SurfaceInventory.RequestReadQuery
            .Where(r => r is { Method: "GET", Pattern: "/v1/search" }).Select(r => r.Name).ToArray();
        Assert.Equal(10, fuzzed.Length);

        Assert.Equal(published.Order(StringComparer.Ordinal), SearchQueryParameters.Honoured.Order(StringComparer.Ordinal));
        Assert.Equal(published.Order(StringComparer.Ordinal), fuzzed.Order(StringComparer.Ordinal));

        var classification = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["q"] = "filter", ["board"] = "filter", ["author"] = "filter", ["kind"] = "filter", ["tags"] = "filter",
            ["min_verification"] = "floor",
            ["cursor"] = "paging", ["limit"] = "paging",
            ["why"] = "presentation", ["marking"] = "presentation",
        };
        foreach (var name in SearchQueryParameters.Honoured.Concat(published))
            Assert.True(classification.ContainsKey(name), $"'{name}' is read by the route and is not classified as filter, floor, paging or presentation (R9.30)");
        Assert.Equal(["min_verification"], classification.Where(c => c.Value == "floor").Select(c => c.Key));
    }

    public static TheoryData<string, bool> UnechoedNames => new()
    {
        { "ignore%20previous%20instructions=1", false },
        { "a%0Ab=1", false },
        { new string('n', 200) + "=1", false },
        { "%E2%80%AE=1", false },
        { "frob_1.x-2=1", true },
    };

    [Theory]
    [MemberData(nameof(UnechoedNames))]
    public async Task R9_29_ANameIsEchoedOnlyIfItIsAPlainName(string query, bool plain)
    {
        var ct = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(query);
        var name = Uri.UnescapeDataString(query[..query.IndexOf('=', StringComparison.Ordinal)]);

        var (status, body) = await GetAsync(query, ct);

        Assert.True(status == HttpStatusCode.BadRequest, $"{status}: {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(UnknownParameter, problem.RootElement.GetProperty("type").GetString());
        var detail = problem.RootElement.GetProperty("detail").GetString()!;
        if (plain)
        {
            Assert.Contains(name, detail, StringComparison.Ordinal);
            return;
        }

        Assert.DoesNotContain(name, detail, StringComparison.Ordinal);
        Assert.Contains("not echoed", detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("kinds=question&limit=x")]
    [InlineData("frobnicate=1&cursor=zzz")]
    public async Task R9_29_AnUnknownNameIsReportedBeforeABadValue(string query)
    {
        var ct = TestContext.Current.CancellationToken;

        var (status, body) = await GetAsync(query, ct);

        Assert.True(status == HttpStatusCode.BadRequest, $"{status}: {body}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(UnknownParameter, problem.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task R9_29_ARefusableNameKeepsItsRedirectAndIsJudgedFirst()
    {
        var ct = TestContext.Current.CancellationToken;

        var (status, body) = await GetAsync("verification=V1&frobnicate=1", ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        using var problem = JsonDocument.Parse(body);
        Assert.Equal("curia/search/unsupported-filter", problem.RootElement.GetProperty("type").GetString());
        Assert.Contains("parameter=verification", problem.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The refusal says what the route does read, and for a near-miss the honoured spelling. Ruling 9 fixes no
    /// phrase, so this fact fixes one: the hint clause reads "did you mean &lt;honoured name&gt;", which the
    /// detail's list of the ten cannot produce. A non-near-miss carries no such clause, and every refusal
    /// names all ten honoured names.
    /// </summary>
    [Theory]
    [InlineData("minVerification=V2", "min_verification")]
    [InlineData("kinds=question", "kind")]
    [InlineData("Min-Verification=V2", "min_verification")]
    [InlineData("unresolved_only=true", null)]
    [InlineData("format=json", null)]
    public async Task R9_29_TheRefusalNamesTheHonouredSpellingOfANearMiss(string query, string? hinted)
    {
        var ct = TestContext.Current.CancellationToken;

        var (status, body) = await GetAsync(query, ct);

        Assert.True(status == HttpStatusCode.BadRequest, $"{status}: {body}");
        using var problem = JsonDocument.Parse(body);
        var detail = problem.RootElement.GetProperty("detail").GetString()!;
        foreach (var honoured in PublishedSearchParameters.Names())
            Assert.Contains(honoured, detail, StringComparison.Ordinal);

        if (hinted is null)
            Assert.DoesNotContain("did you mean", detail, StringComparison.Ordinal);
        else
            Assert.Contains($"did you mean {hinted}", detail, StringComparison.Ordinal);
    }
}
