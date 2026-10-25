using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Tests.Shared;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R9.35 (errata G36) on a real Forum: a search cursor belongs to the query that minted it. Presented under another query, or a cursor the Forum minted
/// before it bound, it is refused <c>400</c>, and the refusal echoes neither a value of the query nor the cursor.
///
/// <para><b>Oracles.</b> The cursor is the one the Forum's own first page returned for the query this fact sent; each row changes exactly one member of
/// that query (the members are the errata's, <see cref="PublishedSearchPage"/>); the controls (<c>why</c>, <c>marking</c>, the identical query) are the
/// requests that must still continue, and the corpus bound they answer with is page one's. The refusal type is parsed from the errata. A negative case
/// always has its control.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class SearchCursorBindingEndpointTests(ForumFixture forum, ConfiguredFloorForumFixture configured)
    : IClassFixture<ForumFixture>, IClassFixture<ConfiguredFloorForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private sealed record Seeded(string Board, string Term, string Author, string Cursor, long CorpusBound);

    /// <summary>Three questions by three agents sharing one term on one board, and page one at <c>limit=1</c>: its cursor and corpus bound.</summary>
    private static async Task<Seeded> SeedAsync(ForumFixture fixture, CancellationToken ct)
    {
        var http = fixture.Client;
        var board = "board-" + Suffix();
        var term = "zbind" + Suffix();
        string? firstAuthor = null;
        for (var i = 0; i < 3; i++)
        {
            var agent = ForumAgent.Create($"https://agents.example/binder{i}-{Suffix()}", $"binder{i}-{Suffix()}");
            firstAuthor ??= agent.AgentId;
            var (dpop, token) = await agent.AuthenticateAsync(http, TokenEndpoint, fixture.Now, ct);
            var noise = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
            using var asked = await dpop.PostAsync(http, PostsUrl, token, agent.SignQuestion(board, $"{noise} about {term} {Guid.NewGuid():N}", $"{noise} {term}", fixture.Now), fixture.Now, ct);
            Assert.True(asked.StatusCode == HttpStatusCode.Created, await asked.Content.ReadAsStringAsync(ct));
        }

        using var page = await GetJsonAsync(fixture, $"q={term}&board={board}&limit=1", ct);
        var cursor = page.RootElement.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor), "the first page minted no cursor, so every row below would test nothing");
        return new Seeded(board, term, firstAuthor!, cursor!, page.RootElement.GetProperty("corpus_bound").GetInt64());
    }

    private static async Task<JsonDocument> GetJsonAsync(ForumFixture fixture, string query, CancellationToken ct)
    {
        using var response = await fixture.Client.GetAsync(new Uri($"/v1/search?{query}", UriKind.Relative), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static async Task<(HttpStatusCode Status, string Body)> GetAsync(ForumFixture fixture, string query, CancellationToken ct)
    {
        using var response = await fixture.Client.GetAsync(new Uri($"/v1/search?{query}", UriKind.Relative), ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static readonly string[] MemberNames = ["q", "board", "tags", "min_verification", "author", "kind", "limit"];

    public static TheoryData<string> Members() => [.. MemberNames];

    /// <summary>The base query (page one's) with exactly one member changed.</summary>
    private static string Changed(Seeded s, string member) => member switch
    {
        "q" => $"q={s.Term}x&board={s.Board}&limit=1",
        "board" => $"q={s.Term}&board={s.Board}-other&limit=1",
        "tags" => $"q={s.Term}&board={s.Board}&tags=nomatch&limit=1",
        "min_verification" => $"q={s.Term}&board={s.Board}&min_verification=V2&limit=1",
        "author" => $"q={s.Term}&board={s.Board}&author={Uri.EscapeDataString(s.Author)}&limit=1",
        "kind" => $"q={s.Term}&board={s.Board}&kind=answer&limit=1",
        "limit" => $"q={s.Term}&board={s.Board}&limit=2",
        _ => throw new ArgumentOutOfRangeException(nameof(member), member, "not a member this fact varies"),
    };

    [Fact]
    public void R9_35_TheMembersThisFactVariesAreTheOnesTheErratumBinds()
    {
        // The row names are the HTTP parameters; the errata's list is the same set (kind and q included).
        Assert.Equal(PublishedSearchPage.BoundMembers().Order(StringComparer.Ordinal), MemberNames.Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Members))]
    public async Task R9_35_PageOnesCursorIsRefusedUnderEachOtherQuery(string member)
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(forum, ct);

        var (status, body) = await GetAsync(forum, $"{Changed(seeded, member)}&cursor={Uri.EscapeDataString(seeded.Cursor)}", ct);

        Assert.True(status == HttpStatusCode.BadRequest, $"expected 400 for page one's cursor under a changed {member}, got {(int)status}");
        Assert.Contains(PublishedSearchPage.MismatchType(), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_35_TheMembersThatAreNotBoundAndTheIdenticalQueryContinue()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(forum, ct);
        var baseQuery = $"q={seeded.Term}&board={seeded.Board}&limit=1";

        foreach (var extra in new[] { string.Empty, "&why=true", "&marking=datamark" })
        {
            using var page = await GetJsonAsync(forum, $"{baseQuery}{extra}&cursor={Uri.EscapeDataString(seeded.Cursor)}", ct);
            Assert.Equal(seeded.CorpusBound, page.RootElement.GetProperty("corpus_bound").GetInt64());
            Assert.Single(page.RootElement.GetProperty("results").EnumerateArray());
        }
    }

    [Fact]
    public async Task R9_35_AFilterThatMatchesNothingRefusesACursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(forum, ct);

        // %00 is a board name BoardName.Parse refuses (no U+0000), so the filter names no board; control: that query alone serves no cursor.
        using (var alone = await GetJsonAsync(forum, $"q={seeded.Term}&board=%00&limit=1", ct))
            Assert.False(alone.RootElement.TryGetProperty("next_cursor", out var next) && next.ValueKind == JsonValueKind.String, "a filter that matches nothing minted a cursor");

        var (status, body) = await GetAsync(forum, $"q={seeded.Term}&board=%00&limit=1&cursor={Uri.EscapeDataString(seeded.Cursor)}", ct);

        Assert.True(status == HttpStatusCode.BadRequest, $"expected 400 for a cursor presented under a board that names none, got {(int)status}");
        Assert.Contains(PublishedSearchPage.MismatchType(), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_35_AFilterThatMatchesNothingRefusesACursorThatTheBindingAloneWouldAccept()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(forum, ct);

        // A page one with no board at all: the board that names none (%00) is read as no board, so the binding of the query below equals this cursor's.
        using var unfiltered = await GetJsonAsync(forum, $"q={seeded.Term}&limit=1", ct);
        var cursor = unfiltered.RootElement.GetProperty("next_cursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor), "the unfiltered first page minted no cursor, so the row below would test nothing");

        // Control: the identical unfiltered query continues, so the binding accepts this cursor and only the matches-nothing refusal can turn it away.
        using (var same = await GetJsonAsync(forum, $"q={seeded.Term}&limit=1&cursor={Uri.EscapeDataString(cursor!)}", ct))
            Assert.Equal(unfiltered.RootElement.GetProperty("corpus_bound").GetInt64(), same.RootElement.GetProperty("corpus_bound").GetInt64());

        var (status, body) = await GetAsync(forum, $"q={seeded.Term}&board=%00&limit=1&cursor={Uri.EscapeDataString(cursor!)}", ct);

        Assert.True(status == HttpStatusCode.BadRequest, $"expected 400 for a cursor presented under a board that names none, got {(int)status}");
        Assert.Contains(PublishedSearchPage.MismatchType(), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_35_AnOldTwoFieldCursorIsMalformed()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(forum, ct);
        var old = Convert.ToBase64String(Encoding.ASCII.GetBytes($"c{seeded.CorpusBound}:1"));

        var (status, body) = await GetAsync(forum, $"q={seeded.Term}&board={seeded.Board}&limit=1&cursor={Uri.EscapeDataString(old)}", ct);

        Assert.True(status == HttpStatusCode.BadRequest, $"expected 400 for a cursor without a binding, got {(int)status}");
        Assert.Contains("curia/search/cursor-malformed", body, StringComparison.Ordinal);
        Assert.DoesNotContain(old, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_35_TheRefusalBodyHoldsNoQueryValueAndNoCursor()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(forum, ct);
        const string Marker = "zzmarkervalue";

        var (status, body) = await GetAsync(forum, $"q={seeded.Term}{Marker}&board={seeded.Board}&limit=1&cursor={Uri.EscapeDataString(seeded.Cursor)}", ct);

        Assert.True(status == HttpStatusCode.BadRequest, $"expected 400 under a changed q, got {(int)status}");
        Assert.DoesNotContain(Marker, body, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.Cursor, body, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.Term, body, StringComparison.Ordinal);
        Assert.DoesNotContain(seeded.Board, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_35_TheFloorInForceIsBound()
    {
        var ct = TestContext.Current.CancellationToken;
        var term = await SeedAsync(configured, ct);

        // A page one that asked for V0 on a Forum configured at V1 is clamped; the cursor it mints records the request and the floor in force.
        using var first = await GetJsonAsync(configured, $"q={term.Term}&board={term.Board}&limit=1&min_verification=V0", ct);
        var cursor = first.RootElement.GetProperty("next_cursor").GetString()!;
        Assert.True(first.RootElement.GetProperty("floor").GetProperty("clamped").GetBoolean(), "page one was not clamped, so the fixture does not show a floor in force that differs from the request");

        // Control: the identical request continues.
        using (var same = await GetJsonAsync(configured, $"q={term.Term}&board={term.Board}&limit=1&min_verification=V0&cursor={Uri.EscapeDataString(cursor)}", ct))
            Assert.Equal(first.RootElement.GetProperty("corpus_bound").GetInt64(), same.RootElement.GetProperty("corpus_bound").GetInt64());

        // Another request that is served at the same floor in force (V1) is another query.
        var (status, body) = await GetAsync(configured, $"q={term.Term}&board={term.Board}&limit=1&min_verification=V1&cursor={Uri.EscapeDataString(cursor)}", ct);
        Assert.True(status == HttpStatusCode.BadRequest, $"expected 400 for a cursor presented under another requested floor served at the same floor in force, got {(int)status}");
        Assert.Contains(PublishedSearchPage.MismatchType(), body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R9_35_AMismatchIsA400AndNotA503()
    {
        var ct = TestContext.Current.CancellationToken;
        var seeded = await SeedAsync(forum, ct);

        var (status, _) = await GetAsync(forum, $"{Changed(seeded, "limit")}&cursor={Uri.EscapeDataString(seeded.Cursor)}", ct);

        Assert.True(status == HttpStatusCode.BadRequest, $"expected 400 for a mismatch, got {(int)status}{(status == HttpStatusCode.ServiceUnavailable ? " (the search-failure status)" : string.Empty)}");
    }
}
