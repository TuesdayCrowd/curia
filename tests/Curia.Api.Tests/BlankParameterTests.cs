using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Curia.Application.Ports;
using Curia.Domain;
using Curia.Domain.Primitives;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static Curia.Api.Tests.RevisionProbe;

namespace Curia.Api.Tests;

/// <summary>
/// R9.54 (errata G56) at the routes, NAM15 to NAM21: <c>GET /v1/search</c> refuses a present blank <c>q</c>, <c>board</c>, <c>author</c> or
/// <c>min_verification</c> and <c>GET /v1/inbox</c> a present blank <c>board</c>, <c>400</c>, in the published types, naming each blank parameter in the
/// published order and no value, after the names are judged, before any value is parsed and without a read of the log of its own. The six parameters
/// the entry does not extend are pinned as they answer today (NAM20), with their successor row.
///
/// <para><b>Oracles.</b> The types, status, title, order and parameter list are parsed from R9.54 words (<see cref="PublishedBoardWords"/>). The seven
/// spellings are the raw query text (<c>""</c>, <c>%20</c>, <c>+</c>, <c>%09</c>, <c>%0A</c>, <c>%C2%A0</c>, <c>%E3%80%80</c>), sent as typed. "No read of
/// the log of its own" is a counting <see cref="IEventReader"/>: the refusal is compared with the unknown-name refusal of the same route, which is made
/// before it, so what authentication and the policy read is common to both. Each refusal has its control: the same request with a value is <c>200</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed partial class BlankParameterTests(PassThroughGateForumFixture forum) : IClassFixture<PassThroughGateForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The seven spellings of a blank value, as raw query text.</summary>
    internal static readonly string[] Spellings = ["", "%20", "+", "%09", "%0A", "%C2%A0", "%E3%80%80"];

    private static readonly string[] Parameters = ["q", "board", "author", "min_verification"];

    private sealed record Reply(HttpStatusCode Status, string Type, string Title, string Detail, string Body, JsonElement Json)
    {
        internal string Says => Type.Length > 0 ? $"{(int)Status} {Type}" : $"{(int)Status}";

        internal int Results => Json.ValueKind == JsonValueKind.Object && Json.TryGetProperty("results", out var r) ? r.GetArrayLength() : -1;
    }

    private static async Task<Reply> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        JsonElement json = default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            json = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            // Not JSON: read as no members.
        }

        string Member(string name) =>
            json.ValueKind == JsonValueKind.Object && json.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;

        return new Reply(response.StatusCode, Member("type"), Member("title"), Member("detail"), body, json);
    }

    private async Task<Reply> SearchAsync(string query, HttpClient? client = null)
    {
        using var response = await (client ?? forum.Client).GetAsync(new Uri("/v1/search" + (query.Length > 0 ? "?" + query : string.Empty), UriKind.Relative), Ct);
        return await ReadAsync(response);
    }

    private async Task<(SceneActor Actor, string Token)> ActorAsync(HttpClient? client = null)
    {
        var actor = await AcceptedAnswerScene.EnrolAsync(forum, "blank", Ct);
        return (actor, await actor.Dpop.GetTokenAsync(client ?? forum.Client, TokenEndpoint, forum.Now, Ct));
    }

    private async Task<Reply> InboxAsync(SceneActor? actor, string? token, string query, HttpClient? client = null)
    {
        client ??= forum.Client;
        var path = "/v1/inbox" + (query.Length > 0 ? "?" + query : string.Empty);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (actor is not null && token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
            request.Headers.Add("DPoP", actor.Dpop.Proof("GET", "http://localhost/v1/inbox", forum.Now, token, nonce: null));
        }

        using var response = await client.SendAsync(request, Ct);
        return await ReadAsync(response);
    }

    /// <summary>The query that holds <paramref name="parameter"/> at <paramref name="spelling"/> beside one valid other criterion.</summary>
    private static string QueryWith(string parameter, string spelling) => parameter switch
    {
        "q" => $"board=b&q={spelling}",
        "board" => $"q=x&board={spelling}",
        "author" => $"q=x&author={spelling}",
        "min_verification" => $"q=x&min_verification={spelling}",
        _ => throw new ArgumentOutOfRangeException(nameof(parameter)),
    };

    private static string QueryWithValue(string parameter) => parameter switch
    {
        "q" => "board=b&q=x",
        "board" => "q=x&board=b",
        "author" => "q=x&author=https%3A%2F%2Fagents.example%2Fa",
        "min_verification" => "q=x&min_verification=V1",
        _ => throw new ArgumentOutOfRangeException(nameof(parameter)),
    };

    public static TheoryData<string, int> Legs()
    {
        var data = new TheoryData<string, int>();
        foreach (var parameter in Parameters)
            for (var i = 0; i < Spellings.Length; i++)
                data.Add(parameter, i);
        return data;
    }

    // ---- NAM15 ----------------------------------------------------------------------------------

    /// <summary>NAM15's words check: the parameters R9.54 words lists are the four, in the order the legs use; the types, status and title are the published ones.</summary>
    [Fact]
    public void R9_54_NAM15_ThePublishedWordsAreTheOnesTheLegsUse()
    {
        Assert.Equal(Parameters, PublishedBoardWords.SearchBlankParameters());
        Assert.Equal(400, PublishedBoardWords.BlankStatus());
        var (search, inbox) = PublishedBoardWords.BlankTypes();
        Assert.Equal("curia/search/blank-parameter", search);
        Assert.Equal("curia/inbox/blank-parameter", inbox);
        Assert.StartsWith("A parameter is present and blank", PublishedBoardWords.BlankTitle(), StringComparison.Ordinal);
    }

    /// <summary>NAM15: the 28 legs (four parameters by seven spellings): <c>400 curia/search/blank-parameter</c>, <c>parameter=&lt;name&gt;</c>, the published title, and no value in the body.</summary>
    [Theory]
    [MemberData(nameof(Legs))]
    public async Task R9_54_NAM15_ABlankSearchParameterIsRefusedByName(string parameter, int spelling)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        var reply = await SearchAsync(QueryWith(parameter, Spellings[spelling]));

        Said($"{PublishedBoardWords.BlankStatus()} {PublishedBoardWords.BlankTypes().Search}", reply.Says);
        Said($"parameter={parameter}", reply.Detail);
        Said(PublishedBoardWords.BlankTitle(), reply.Title);
        Assert.DoesNotContain("%20", reply.Body, StringComparison.Ordinal);
    }

    /// <summary>NAM15's controls: each parameter with a value is not refused for blankness (200).</summary>
    [Theory]
    [InlineData("q")]
    [InlineData("board")]
    [InlineData("author")]
    [InlineData("min_verification")]
    public async Task R9_54_NAM15_ANonBlankValueIsServed(string parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        var reply = await SearchAsync(QueryWithValue(parameter));

        Said("200", ((int)reply.Status).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    // ---- NAM16 ----------------------------------------------------------------------------------

    /// <summary>NAM16: the pairs that were the defect: <c>q=x&amp;board=</c> names <c>board</c> (never the unfiltered page); <c>q=</c> alone is refused; a filter alone lists (R9.25).</summary>
    [Fact]
    public async Task R9_54_NAM16_ABlankFilterBesideATermIsRefusedAndNeverTheUnfilteredPage()
    {
        var blankBoard = await SearchAsync("q=x&board=");
        var blankQuery = await SearchAsync("q=");
        var filterAlone = await SearchAsync("board=meta");

        Said(
            [$"400 {PublishedBoardWords.BlankTypes().Search}", $"400 {PublishedBoardWords.BlankTypes().Search}", "200"],
            [blankBoard.Says, blankQuery.Says, filterAlone.Says]);
        Said("parameter=board", blankBoard.Detail);
        Said("parameter=q", blankQuery.Detail);
    }

    // ---- NAM17 ----------------------------------------------------------------------------------

    /// <summary>
    /// NAM17: order and spelling. Names are judged first (an unknown name with a blank value is told the name), the refusable names keep their redirect, a blank is told
    /// before any value is parsed (<c>limit=x</c>), two blanks are named in the published order whatever the request's order, and the names are read without regard to
    /// case and spelled as published.
    /// </summary>
    [Fact]
    public async Task R9_54_NAM17_TheBlankRefusalHasItsPlaceInTheOrder()
    {
        var unknown = await SearchAsync("q=&bogus=1");
        var refusable = await SearchAsync("verification=&q=");
        var beforeValues = await SearchAsync("q=&limit=x");
        var two = await SearchAsync("min_verification=&q=");
        var upperQ = await SearchAsync("Q=");
        var upperBoard = await SearchAsync("q=x&BOARD=");

        Said("400 curia/search/unknown-parameter", unknown.Says);
        Said("400 curia/search/unsupported-filter", refusable.Says);
        Said($"400 {PublishedBoardWords.BlankTypes().Search}", beforeValues.Says);
        Said("parameter=q, min_verification", two.Detail);
        Said("parameter=q", upperQ.Detail);
        Said("parameter=board", upperBoard.Detail);
    }

    // ---- NAM18 ----------------------------------------------------------------------------------

    /// <summary>
    /// NAM18: the blank refusal reads the log no more than the unknown-name refusal of the same route does (which is made before it, so what authentication and the policy
    /// read is common to both), and a valid request reads more: on search and on the inbox.
    /// </summary>
    [Fact]
    public async Task R9_54_NAM18_TheBlankRefusalReadsNoLogOfItsOwn()
    {
        var counter = new Counter();
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.CountRequestReads(counter)));
        using var client = host.CreateClient();
        var (actor, token) = await ActorAsync(client);

        async Task<int> ReadsAsync(Func<Task<Reply>> request)
        {
            counter.Count = 0;
            await request();
            return counter.Count;
        }

        var searchUnknown = await ReadsAsync(() => SearchAsync("q=x&bogus=1", client));
        var searchBlank = await ReadsAsync(() => SearchAsync("q=x&board=", client));
        var searchValid = await ReadsAsync(() => SearchAsync("q=x&board=b", client));
        var inboxUnknown = await ReadsAsync(() => InboxAsync(actor, token, "bogus=1", client));
        var inboxBlank = await ReadsAsync(() => InboxAsync(actor, token, "board=", client));
        var inboxValid = await ReadsAsync(() => InboxAsync(actor, token, "board=b", client));

        // The inbox has no control of the second kind: a valid inbox request reads no more of the log than the unknown-name refusal does (the selector works
        // from the projection; the two reads it shows are authentication's and the policy's, measured), so for the inbox the claim "no read of its own" is held
        // by equality with the unknown-name refusal and cannot be shown to bite by a valid request (trap 93; recorded in the report).
        Said(
            [$"search: blank {searchUnknown} (the unknown name's)", "search: valid reads more", $"inbox: blank {inboxUnknown} (the unknown name's)"],
            [$"search: blank {searchBlank} (the unknown name's)", searchValid > searchUnknown ? "search: valid reads more" : $"search: valid reads {searchValid}", $"inbox: blank {inboxBlank} (the unknown name's)"]);
        _ = inboxValid;
    }

    // ---- NAM19 ----------------------------------------------------------------------------------

    /// <summary>NAM19: the inbox refuses a blank <c>board</c> in each spelling; an unauthenticated caller is told what it was before; a blank <c>tags</c> is refused too (R9.56, errata G62) and a named tag and an absent <c>board</c> are served (controls).</summary>
    [Fact]
    public async Task R9_54_NAM19_TheInboxRefusesABlankBoardAfterTheCallerIsKnownAllowed()
    {
        var (actor, token) = await ActorAsync();

        var refused = new List<string>();
        foreach (var spelling in Spellings)
            refused.Add((await InboxAsync(actor, token, $"board={spelling}")).Says);
        var anonymous = await InboxAsync(null, null, "board=");
        var tagsBlank = await InboxAsync(actor, token, "tags=");
        var absent = await InboxAsync(actor, token, string.Empty);
        var tagsNamed = await InboxAsync(actor, token, "tags=a");

        Said(
            Spellings.Select(_ => $"400 {PublishedBoardWords.BlankTypes().Inbox}"),
            refused);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.Status);
        // R9.56 (errata G62), flipped on purpose: a blank `tags` is refused as `board` is; a named tag and an absent board are the controls.
        Said($"400 {PublishedBoardWords.BlankTypes().Inbox}", tagsBlank.Says);
        Assert.Equal(HttpStatusCode.OK, tagsNamed.Status);
        Assert.Equal(HttpStatusCode.OK, absent.Status);
        var blank = await InboxAsync(actor, token, "board=");
        Said("parameter=board", blank.Detail);
    }

    // ---- NAM20 ----------------------------------------------------------------------------------

    /// <summary>
    /// NAM20, flipped on purpose by errata G62 (R9.56, R9.54 (rev.): G56's decision 8 amended): on the search route <c>kind=</c>, <c>tags=</c> and <c>cursor=</c> blank in all seven
    /// spellings are refused <c>400 curia/search/blank-parameter</c> and no longer ignored; what the entry still does not extend stands as it was: <c>why=</c>, <c>marking=</c> and
    /// <c>limit=</c> are ignored when empty and their white-space spellings answer their own types, and a repeated parameter is joined with a comma (<c>q=a&amp;q=</c> and
    /// <c>board=%20&amp;board=meta</c> answer 200 with no result).
    /// </summary>
    [Fact]
    public async Task R9_56_NAM20_TheThreeParametersR956ExtendsAreRefusedAndTheOthersAreAsTheyWere()
    {
        var served = new List<string>();
        var expected = new List<string>();
        foreach (var parameter in new[] { "kind", "tags", "cursor" })
        {
            foreach (var spelling in Spellings)
            {
                served.Add($"{parameter}={spelling}: {(await SearchAsync($"q=x&{parameter}={spelling}")).Says}");
                expected.Add($"{parameter}={spelling}: 400 {PublishedBoardWords.BlankTypes().Search}");
            }
        }

        foreach (var parameter in new[] { "why", "marking", "limit" })
        {
            served.Add($"{parameter}=: {(await SearchAsync($"q=x&{parameter}=")).Says}");
            expected.Add($"{parameter}=: 200");
        }

        var own = new Dictionary<string, string>
        {
            ["why"] = "curia/search/unknown-why",
            ["marking"] = "curia/serving/unknown-marking",
            ["limit"] = "curia/search/invalid-limit",
        };
        foreach (var (parameter, type) in own)
        {
            foreach (var spelling in Spellings.Skip(1))
            {
                served.Add($"{parameter}={spelling}: {(await SearchAsync($"q=x&{parameter}={spelling}")).Says}");
                expected.Add($"{parameter}={spelling}: 400 {type}");
            }
        }

        Said(expected, served);

        var repeatedTerm = await SearchAsync("q=a&q=");
        var repeatedBoard = await SearchAsync("board=%20&board=meta");
        Assert.Equal(HttpStatusCode.OK, repeatedTerm.Status);
        Assert.Equal(0, repeatedTerm.Results);
        Assert.Equal(HttpStatusCode.OK, repeatedBoard.Status);
        Assert.Equal(0, repeatedBoard.Results);
    }

    /// <summary>
    /// NAM20 on the inbox, flipped on purpose by errata G62 (R9.56): <c>tags=</c> and <c>cursor=</c> blank in all seven spellings are refused <c>400 curia/inbox/blank-parameter</c>;
    /// <c>limit=</c> empty is still ignored and its white space is <c>curia/inbox/invalid-limit</c>.
    /// </summary>
    [Fact]
    public async Task R9_56_NAM20_TheInboxRefusesABlankTagsAndCursorAndLimitIsAsItWas()
    {
        var (actor, token) = await ActorAsync();
        var served = new List<string>();
        var expected = new List<string>();
        foreach (var parameter in new[] { "tags", "cursor" })
        {
            foreach (var spelling in Spellings)
            {
                served.Add($"{parameter}={spelling}: {(await InboxAsync(actor, token, $"{parameter}={spelling}")).Says}");
                expected.Add($"{parameter}={spelling}: 400 {PublishedBoardWords.BlankTypes().Inbox}");
            }
        }

        served.Add($"limit=: {(await InboxAsync(actor, token, "limit=")).Says}");
        expected.Add("limit=: 200");
        foreach (var spelling in Spellings.Skip(1))
        {
            served.Add($"limit={spelling}: {(await InboxAsync(actor, token, $"limit={spelling}")).Says}");
            expected.Add($"limit={spelling}: 400 curia/inbox/invalid-limit");
        }

        Said(expected, served);
    }

    // ---- NAM21 ----------------------------------------------------------------------------------

    [GeneratedRegex("""m\.Query\.Add\(new\("(?<name>[a-z_]+)"\s*,""", RegexOptions.CultureInvariant)]
    private static partial Regex QueryAdd();

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "tests")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("no 'tests' above " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// NAM21 (a control, green on the seam; held by FNAM42): the request fuzzer's closed corpus contains the blank variants for the query parts R9.54 names. The parts are derived
    /// from the route's own set (<see cref="SearchQueryParameters.Honoured"/> and <see cref="BlankQueryParameters"/>'s lists): each is an exemplar query member of its route, and
    /// the closed set's <c>empty</c>, <c>space</c> and <c>whitespace</c> variations apply to a query part and render as <c>""</c>, <c>%20</c> and <c>%09%20%0A</c>. A pass that never
    /// sent a blank would be red here, not green.
    /// </summary>
    [Fact]
    public void R9_54_NAM21_TheFuzzerSendsTheBlankVariantsOfTheNamedParts()
    {
        var source = File.ReadAllText(Path.Combine(RepositoryRoot(), "tests", "Curia.Api.Tests", "Fuzz", "Exemplars.cs"));
        var searchBlock = source[source.IndexOf("Anonymous(\"GET\", \"/v1/search\"", StringComparison.Ordinal)..source.IndexOf("Authenticated(\"GET\", \"/v1/inbox\"", StringComparison.Ordinal)];
        var inboxBlock = source[source.IndexOf("Authenticated(\"GET\", \"/v1/inbox\"", StringComparison.Ordinal)..source.IndexOf("Authenticated(\"GET\", \"/v1/flags\"", StringComparison.Ordinal)];
        var searchParts = QueryAdd().Matches(searchBlock).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
        var inboxParts = QueryAdd().Matches(inboxBlock).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);

        foreach (var name in BlankQueryParameters.SearchParameters)
        {
            Assert.Contains(name, SearchQueryParameters.Honoured);
            Assert.Contains(name, searchParts);
        }

        foreach (var name in BlankQueryParameters.InboxParameters) Assert.Contains(name, inboxParts);

        var expected = new Dictionary<string, string> { ["empty"] = string.Empty, ["space"] = "%20", ["whitespace"] = "%09%20%0A" };
        foreach (var name in searchParts.Union(inboxParts))
        {
            var part = new Part("query:" + name, PartKind.Query, PartValueKind.String, false, null);
            foreach (var (id, encoded) in expected)
            {
                var variation = Variations.Closed.SingleOrDefault(v => v.Id == id);
                Assert.NotNull(variation);
                Assert.True(variation!.Applies(part), $"{id} does not apply to query:{name}");
                var made = Assert.IsType<VariedValue.Encoded>(variation.Make(part, "x"));
                Assert.Equal(encoded, made.Text);
            }
        }
    }
}
