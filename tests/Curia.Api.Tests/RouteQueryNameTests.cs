using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Api.Tests.Fuzz;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.37 (errata G37): <c>GET /v1/boards/{board}/posts</c> reads exactly <c>marking</c> and <c>GET /v1/inbox</c> exactly
/// <c>board</c>, <c>tags</c>, <c>cursor</c>, <c>limit</c> and <c>marking</c>, case-insensitively; any other name is
/// <c>400</c> as <c>curia/boards/unknown-parameter</c> or <c>curia/inbox/unknown-parameter</c> and never a listing. R9.29's
/// rules for the echo, the near-miss hint and the order of the check carry over, and the search route keeps its own
/// refusal byte for byte (PD17).
///
/// <para><b>Oracles.</b> The sets, the refusal phrase and the count members come from the errata
/// (<see cref="PublishedRouteParameters"/>), never from the routes. The posts a fact expects served are the ones it signed
/// (<see cref="InboxProbe"/>'s ledger). The search refusals are six bodies captured from the unmodified tree at Task 0,
/// pasted as literals, so the extraction of the engine is held to a run of the old tree and not to the new engine. Every
/// negative row has its control: the same request with only an honoured name answers <c>200</c>.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed partial class RouteQueryNameTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string BoardListingType = "curia/boards/unknown-parameter";
    private const string InboxType = "curia/inbox/unknown-parameter";

    [GeneratedRegex(@"\{(\w+):[^}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex RouteConstraint();

    private static string Base64(string text) => Convert.ToBase64String(Encoding.ASCII.GetBytes(text));

    private static (string Type, string Title, string Detail) Problem(string body)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        string Read(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
        return (Read("type"), Read("title"), Read("detail"));
    }

    private async Task<(HttpStatusCode Status, string Body)> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri(pathAndQuery, UriKind.Relative), ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    /// <summary>A board holding what the facts ask the listing to serve: three questions, a T1 answer and a comment. The ids are the ledger.</summary>
    private async Task<(string Board, string[] Ids)> BoardAsync(CancellationToken ct)
    {
        var probe = new InboxProbe(forum);
        var board = "board-" + InboxProbe.Suffix();
        var answerer = await probe.EnrolT1Async("names-answerer", ct);
        var asker = await probe.EnrolAsync("names-asker", ct);
        var commenter = await probe.EnrolAsync("names-commenter", ct);
        var q1 = await probe.AskAsync(asker, board, "First " + InboxProbe.Suffix(), ct);
        var q2 = await probe.AskAsync(asker, board, "Second " + InboxProbe.Suffix(), ct);
        await probe.AskAsync(asker, board, "Third " + InboxProbe.Suffix(), ct);
        var answer = await probe.AnswerAsync(answerer, q1, ct);
        var comment = await probe.CommentAsync(commenter, board, q2.Id, ct);

        // The listing serves discussion: the questions, the answer and the comment (a vote is never served, R8.55).
        string[] served = [.. probe.Questions.Where(q => q.Board == board).Select(q => q.Id), answer, comment];
        return (board, [.. served.Order(StringComparer.Ordinal)]);
    }

    /// <summary>The post ids of the board as the listing serves them: what is compared with the ledger, never what the ledger is made of.</summary>
    private async Task<string[]> ServedIdsAsync(string board, string query, CancellationToken ct)
    {
        var (status, body) = await GetAsync($"/v1/boards/{board}/posts?{query}", ct);
        Assert.True(status == HttpStatusCode.OK, $"{status}: {body}");
        using var json = JsonDocument.Parse(body);
        return [.. json.RootElement.EnumerateArray().Select(p => p.GetProperty("post_id").GetString()!)];
    }

    // ---- PD1 to PD3: the board listing ------------------------------------------------------

    public static TheoryData<string> BoardListingUnknown => new()
    {
        "limit=1", "cursor=@@@", "kind=answer", "tags=nomatch", "author=nobody", "q=x", "board=other", "frobnicate=1", "Limit=1", "markings=datamark",
    };

    /// <summary>PD1: the live run answered every one of these with the whole board.</summary>
    [Theory]
    [MemberData(nameof(BoardListingUnknown))]
    public async Task R9_37_TheBoardListingRefusesEveryNameItDoesNotRead(string unknown)
    {
        var ct = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(unknown);
        var name = unknown[..unknown.IndexOf('=', StringComparison.Ordinal)];
        var (board, ids) = await BoardAsync(ct);

        var control = await GetAsync($"/v1/boards/{board}/posts?marking=datamark", ct);
        Assert.Equal(HttpStatusCode.OK, control.Status);
        Assert.Equal(ids, (await ServedIdsAsync(board, "marking=datamark", ct)).Order(StringComparer.Ordinal));

        var (status, body) = await GetAsync($"/v1/boards/{board}/posts?{unknown}", ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var problem = Problem(body);
        Assert.Equal(BoardListingType, problem.Type);
        Assert.Contains($"parameter={name}", problem.Detail, StringComparison.Ordinal);
        foreach (var id in ids)
            Assert.DoesNotContain(id, body, StringComparison.Ordinal);
    }

    /// <summary>PD2: what the route served it still serves.</summary>
    [Theory]
    [InlineData("?marking=datamark")]
    [InlineData("?MARKING=datamark")]
    [InlineData("")]
    [InlineData("?marking=delimiters")]
    public async Task R9_37_TheBoardListingStillServesWhatItServed(string query)
    {
        var ct = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(query);
        var (board, ids) = await BoardAsync(ct);

        var (status, body) = await GetAsync($"/v1/boards/{board}/posts{query}", ct);

        Assert.Equal(HttpStatusCode.OK, status);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(ids, json.RootElement.EnumerateArray().Select(p => p.GetProperty("post_id").GetString()!).Order(StringComparer.Ordinal));
    }

    /// <summary>PD3: the refusal says what the route is, in the phrase R9.37 gives, and names the route that does what the caller asked.</summary>
    [Fact]
    public async Task R9_37_TheBoardListingsRefusalSaysWhatTheRouteIs()
    {
        var ct = TestContext.Current.CancellationToken;
        var phrase = PublishedRouteParameters.BoardListingRefusalPhrase();
        Assert.Contains("does not page, filter or search", phrase, StringComparison.Ordinal);
        var (board, _) = await BoardAsync(ct);

        var (status, body) = await GetAsync($"/v1/boards/{board}/posts?limit=1", ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var problem = Problem(body);
        Assert.Contains(phrase, problem.Detail, StringComparison.Ordinal);
        Assert.Contains("/v1/search", problem.Detail, StringComparison.Ordinal);
    }

    // ---- PD4, PD5: the inbox ----------------------------------------------------------------

    public static TheoryData<string> InboxUnknown => new()
    {
        "kind=answer", "q=jcs", "author=x", "min_verification=V0", "why=true", "unresolved_only=true", "frobnicate=1", "markings=datamark",
    };

    /// <summary>PD4: an authenticated reader's inbox refuses a name it does not read; each honoured name, in any case, still answers 200.</summary>
    [Theory]
    [MemberData(nameof(InboxUnknown))]
    public async Task R9_37_TheInboxRefusesEveryNameItDoesNotRead(string unknown)
    {
        var ct = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(unknown);
        var name = unknown[..unknown.IndexOf('=', StringComparison.Ordinal)];
        var probe = new InboxProbe(forum);
        var reader = await probe.EnrolAsync("names-reader", ct);
        var cursor = Uri.EscapeDataString(Base64("0:1"));

        foreach (var honoured in new[]
        {
            "board=b", "tags=a", "limit=5", "marking=datamark", $"cursor={cursor}", "BOARD=b", "TAGS=a", "LIMIT=5", "MARKING=delimiters", $"CURSOR={cursor}",
        })
        {
            var control = await probe.InboxRawAsync(reader, "?" + honoured, ct);
            Assert.True(control.Status == HttpStatusCode.OK, $"{honoured}: {control.Status}: {control.Body}");
        }

        var (status, body) = await probe.InboxRawAsync(reader, "?" + unknown, ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var problem = Problem(body);
        Assert.Equal(InboxType, problem.Type);
        Assert.Contains($"parameter={name}", problem.Detail, StringComparison.Ordinal);
    }

    /// <summary>PD5: an anonymous caller is told that it is anonymous, and not which names the route reads (R9.37, decision 4).</summary>
    [Fact]
    public async Task R9_37_AnAnonymousCallerIsToldThatAndNotWhichNamesTheRouteReads()
    {
        var ct = TestContext.Current.CancellationToken;

        var (status, body) = await GetAsync("/v1/inbox?frobnicate=1", ct);

        Assert.Equal(HttpStatusCode.Unauthorized, status);
        Assert.DoesNotContain("unknown-parameter", body, StringComparison.Ordinal);
        Assert.DoesNotContain("frobnicate", body, StringComparison.Ordinal);
        Assert.DoesNotContain("reads exactly", body, StringComparison.Ordinal);
        foreach (var name in PublishedRouteParameters.Inbox())
            Assert.DoesNotContain(name, body, StringComparison.OrdinalIgnoreCase);
    }

    // ---- PD6, PD7: the sets are the published sets ------------------------------------------

    /// <summary>PD6: the errata's sets, the code's sets and the fuzzer inventory's reads agree, route by route.</summary>
    [Fact]
    public void R9_37_TheSetsEachRouteReadsAreTheSetsTheErrataPublishes()
    {
        var published = new (string Pattern, string[] Names, IReadOnlySet<string> Honoured)[]
        {
            ("/v1/boards/{board}/posts", PublishedRouteParameters.BoardListing(), BoardListingQueryParameters.Honoured),
            ("/v1/inbox", PublishedRouteParameters.Inbox(), InboxQueryParameters.Honoured),
        };

        foreach (var (pattern, names, honoured) in published)
        {
            var inventory = SurfaceInventory.RequestReadQuery.Where(r => r.Method == "GET" && r.Pattern == pattern).Select(r => r.Name);

            Assert.Equal(names.Order(StringComparer.Ordinal), inventory.Order(StringComparer.Ordinal));
            Assert.Equal(names.Order(StringComparer.Ordinal), honoured.Order(StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// What every GET route that reads a query name does with a name it does not read: the test's own table. A route is either
    /// one that refuses (search, the board listing, the inbox) or a stated non-property (errata G37, finding 4). Every other
    /// route that reads a name fails this fact by name, so a seventh cannot be added without a decision. <c>POST /v1/posts/batch</c>
    /// reads <c>marking</c> and is a POST: it is outside this fact by method, and has no equality row.
    /// </summary>
    private static readonly Dictionary<string, bool> Classified = new(StringComparer.Ordinal)
    {
        ["/v1/search"] = true,
        ["/v1/boards/{board}/posts"] = true,
        ["/v1/inbox"] = true,
        ["/v1/posts/{postId}"] = false,
        ["/v1/threads/{rootPostId}"] = false,
        ["/v1/jwks"] = false,
        ["/v1/log/consistency"] = false,
        ["/v1/log/proof/{index}"] = false,
    };

    /// <summary>PD7: every GET route that reads a query name is classified; the refusers refuse, the others answer a name they ignore as they answer none.</summary>
    [Fact]
    public async Task R9_37_EveryGetRouteThatReadsAQueryNameIsClassified()
    {
        var ct = TestContext.Current.CancellationToken;
        // A route constraint ("{index:long}") is part of how the host spells a pattern and not of which route it is.
        var reading = SurfaceInventory.BoundQuery(forum).Concat(SurfaceInventory.RequestReadQuery)
            .Where(r => r.Method == "GET").Select(r => RouteConstraint().Replace(r.Pattern, "{$1}")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        foreach (var pattern in reading)
            Assert.True(Classified.ContainsKey(pattern), $"{pattern} reads a query name and is classified neither as refusing it nor as a stated non-property (R9.37)");
        foreach (var pattern in Classified.Keys)
            Assert.True(reading.Contains(pattern), $"{pattern} is classified and reads no query name: the table is stale");

        var probe = new InboxProbe(forum);
        var reader = await probe.EnrolAsync("names-classify", ct);
        var asker = await probe.EnrolAsync("names-classify-asker", ct);
        var board = "board-" + InboxProbe.Suffix();
        var question = await probe.AskAsync(asker, board, "Classify " + InboxProbe.Suffix(), ct);

        var requests = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["/v1/search"] = ["/v1/search?q=x"],
            ["/v1/boards/{board}/posts"] = [$"/v1/boards/{board}/posts"],
            ["/v1/inbox"] = ["/v1/inbox"],
            ["/v1/posts/{postId}"] = [$"/v1/posts/{question.Id}"],
            ["/v1/threads/{rootPostId}"] = [$"/v1/threads/{question.Id}"],

            // /v1/jwks requires `agent`: both the request with it and the request without answer the same with and without the stranger.
            ["/v1/jwks"] = [$"/v1/jwks?agent={Uri.EscapeDataString(asker.Id)}", "/v1/jwks"],
            ["/v1/log/consistency"] = ["/v1/log/consistency?from=0&to=0"],
            ["/v1/log/proof/{index}"] = ["/v1/log/proof/0?tree_size=1"],
        };

        foreach (var (pattern, refuses) in Classified)
        {
            foreach (var path in requests[pattern])
            {
                var withName = path + (path.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "frobnicate=1";
                var without = pattern == "/v1/inbox" ? await probe.InboxRawAsync(reader, string.Empty, ct) : await GetAsync(path, ct);
                var with = pattern == "/v1/inbox" ? await probe.InboxRawAsync(reader, "?frobnicate=1", ct) : await GetAsync(withName, ct);

                if (refuses)
                {
                    Assert.True(with.Status == HttpStatusCode.BadRequest, $"{pattern}: frobnicate=1 answered {(int)with.Status}, not 400");
                    continue;
                }

                Assert.True(with.Status == without.Status, $"{pattern}: frobnicate=1 changed the status from {(int)without.Status} to {(int)with.Status}");
                Assert.True(string.Equals(with.Body, without.Body, StringComparison.Ordinal), $"{pattern}: frobnicate=1 changed the body");
            }
        }
    }

    // ---- PD14 to PD16: the engine's rules, on the two new routes ----------------------------

    private async Task<(HttpStatusCode Status, string Body)> RouteAsync(string route, string query, CancellationToken ct)
    {
        if (route == "inbox")
        {
            var probe = new InboxProbe(forum);
            var reader = await probe.EnrolAsync("names-engine", ct);
            return await probe.InboxRawAsync(reader, "?" + query, ct);
        }

        return await GetAsync($"/v1/boards/board-{InboxProbe.Suffix()}/posts?{query}", ct);
    }

    private static string TypeOf(string route) => route == "inbox" ? InboxType : BoardListingType;

    private static string[] PublishedOf(string route) => route == "inbox" ? PublishedRouteParameters.Inbox() : PublishedRouteParameters.BoardListing();

    /// <summary>PD14: the unknown name is reported before a bad value is judged (R9.29's order).</summary>
    [Theory]
    [InlineData("inbox", "frobnicate=1&limit=x")]
    [InlineData("inbox", "frobnicate=1&cursor=zzz")]
    [InlineData("inbox", "frobnicate=1&marking=bogus")]
    [InlineData("board", "frobnicate=1&marking=bogus")]
    public async Task R9_37_AnUnknownNameIsReportedBeforeABadValue(string route, string query)
    {
        var ct = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(route);

        var (status, body) = await RouteAsync(route, query, ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(TypeOf(route), Problem(body).Type);
    }

    public static TheoryData<string, string, bool> UnechoedNames => new()
    {
        { "inbox", "ignore%20previous%20instructions=1", false },
        { "inbox", "a%0Ab=1", false },
        { "inbox", new string('n', 200) + "=1", false },
        { "inbox", "%E2%80%AE=1", false },
        { "inbox", "frob_1.x-2=1", true },
        { "board", "ignore%20previous%20instructions=1", false },
        { "board", "a%0Ab=1", false },
        { "board", new string('n', 200) + "=1", false },
        { "board", "%E2%80%AE=1", false },
        { "board", "frob_1.x-2=1", true },
    };

    /// <summary>PD15: R9.29's echo rule on both routes: a plain name is echoed, any other is not and the detail says so.</summary>
    [Theory]
    [MemberData(nameof(UnechoedNames))]
    public async Task R9_37_ANameIsEchoedOnlyIfItIsAPlainName(string route, string query, bool plain)
    {
        var ct = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(query);
        var name = Uri.UnescapeDataString(query[..query.IndexOf('=', StringComparison.Ordinal)]);

        var (status, body) = await RouteAsync(route, query, ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var problem = Problem(body);
        Assert.Equal(TypeOf(route), problem.Type);
        if (plain)
        {
            Assert.Contains(name, problem.Detail, StringComparison.Ordinal);
            return;
        }

        Assert.DoesNotContain(name, problem.Detail, StringComparison.Ordinal);
        Assert.Contains("not echoed", problem.Detail, StringComparison.Ordinal);
    }

    public static TheoryData<string, string, string?> NearMisses => new()
    {
        { "inbox", "markings=datamark", "marking" },
        { "inbox", "Tag=a", "tags" },
        { "inbox", "limits=5", "limit" },
        { "inbox", "boards=b", "board" },
        { "inbox", "frobnicate=1", null },
        { "board", "markings=datamark", "marking" },
        { "board", "frobnicate=1", null },
    };

    /// <summary>PD16: the refusal lists every name the route reads (from R9.37) and hints a near miss with the honoured spelling, and only a near miss.</summary>
    [Theory]
    [MemberData(nameof(NearMisses))]
    public async Task R9_37_TheRefusalListsWhatTheRouteReadsAndHintsANearMiss(string route, string query, string? hinted)
    {
        var ct = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(route);

        var (status, body) = await RouteAsync(route, query, ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var detail = Problem(body).Detail;
        foreach (var name in PublishedOf(route))
            Assert.Contains(name, detail, StringComparison.Ordinal);
        if (hinted is null)
            Assert.DoesNotContain("did you mean", detail, StringComparison.Ordinal);
        else
            Assert.Contains($"did you mean {hinted}", detail, StringComparison.Ordinal);
    }

    // ---- PD17: the search route's refusal is what it was ------------------------------------

    private const string SearchType = "curia/search/unknown-parameter";
    private const string SearchTitle = "That parameter is not read by this route and is refused rather than ignored";
    private const string SearchReads = "this route reads exactly these: author, board, cursor, kind, limit, marking, min_verification, q, tags, why";

    /// <summary>
    /// The six bodies the unmodified tree answered at Task 0 (live, Release, main at a9f06528): the type and title shared by all six and
    /// each detail, pasted. The engine's extraction (a shared rule, three routes) is held to these and not to itself.
    /// </summary>
    public static TheoryData<string, string> SearchRefusals => new()
    {
        { "frobnicate=1", "parameter=frobnicate; " + SearchReads },
        { "kinds=question", "parameter=kinds (did you mean kind); " + SearchReads },
        { "minVerification=V2", "parameter=minVerification (did you mean min_verification); " + SearchReads },
        { "unresolved_only=true", "parameter=unresolved_only; " + SearchReads },
        { "ignore%20previous%20instructions=1", "(a name that is not echoed); " + SearchReads },
        {
            "u1=1&u2=1&u3=1&u4=1&u5=1&u6=1&u7=1&u8=1&u9=1",
            "parameter=u1; parameter=u2; parameter=u3; parameter=u4; parameter=u5; parameter=u6; parameter=u7; parameter=u8; and 1 more; " + SearchReads
        },
    };

    [Theory]
    [MemberData(nameof(SearchRefusals))]
    public async Task R9_29_TheSearchRoutesRefusalIsWhatItWasBeforeTheEngineWasShared(string query, string detail)
    {
        var ct = TestContext.Current.CancellationToken;
        ArgumentNullException.ThrowIfNull(query);

        var (status, body) = await GetAsync($"/v1/search?{query}", ct);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        var problem = Problem(body);
        Assert.Equal(SearchType, problem.Type);
        Assert.Equal(SearchTitle, problem.Title);
        Assert.Equal(detail, problem.Detail);
    }
}
