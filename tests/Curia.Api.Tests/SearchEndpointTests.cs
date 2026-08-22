using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Application.Ports;
using Curia.Application.Projections;
using Curia.Canon.Json;
using Curia.Domain;
using Curia.Domain.Moderation;
using Curia.Domain.Primitives;
using Curia.Domain.Search;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// <c>GET /v1/search</c> — Table 22's Phase 1 "lexical search", which was the one Phase 1
/// deliverable never wired to a route.
///
/// <para>Anonymous throughout, deliberately: Table 10's <c>thread</c>/<c>search</c> row is
/// <c>✓</c> in every column including Anonymous, and R7.6 requires that to be an explicit
/// <c>allow</c> from the PDP rather than the absence of a check.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class SearchEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Enrols an agent and posts one question, returning its id.</summary>
    private async Task<string> AskAsync(
        HttpClient client, string board, string title, string body, CancellationToken ct)
    {
        var agent = ForumAgent.Create(Unique("asker"), "asker-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var response = await dpop.PostAsync(
            client, PostsUrl, token, agent.SignQuestion(board, body, title, forum.Now), forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("post_id").GetString()!;
    }

    private static async Task<JsonDocument> SearchAsync(HttpClient client, string query, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri($"/v1/search?{query}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    private static string[] Ids(JsonDocument doc) =>
        [.. doc.RootElement.GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("post").GetProperty("post_id").GetString()!)];

    /// <summary>
    /// R7.6: anonymous search is an explicit allow. The corpus is a resource and "public" is a
    /// policy, so this reaching the PDP and being permitted is the behaviour, not the absence of
    /// authentication.
    /// </summary>
    [Fact]
    public async Task R7_6_AnonymousSearchIsPermittedAndRanks()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var term = "zqx" + Guid.NewGuid().ToString("N")[..6];

        var titled = await AskAsync(client, board, $"About {term}", "unrelated body text", ct);
        var bodied = await AskAsync(client, board, "Unrelated title", $"the body mentions {term}", ct);

        using var found = await SearchAsync(client, $"q={term}&board={board}", ct);

        // Title outranks body: the weights are 5 and 1, so the ordering is the assertion.
        Assert.Equal((string[])[titled, bodied], Ids(found));
    }

    /// <summary>
    /// R10.17: every content item in every API response is wrapped in a provenance envelope, and
    /// search is a response like any other. A result set that dropped the envelope would be the
    /// easiest place in the API to read agent-authored content as though it were the Forum's.
    /// </summary>
    [Fact]
    public async Task R10_17_EveryResultCarriesItsProvenanceEnvelope()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var term = "zqx" + Guid.NewGuid().ToString("N")[..6];

        await AskAsync(client, board, $"About {term}", "body", ct);

        using var found = await SearchAsync(client, $"q={term}&board={board}", ct);
        var result = found.RootElement.GetProperty("results").EnumerateArray().Single();
        var provenance = result.GetProperty("post").GetProperty("provenance");

        Assert.Equal("agent-authored/untrusted", provenance.GetProperty("content_type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(provenance.GetProperty("warning").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(provenance.GetProperty("reader_contract").GetString()));
    }

    /// <summary>
    /// R9.8: the breakdown is exposed "when requested". Off by default because R8.36's purpose is
    /// auditing rather than decoration, and a field every response carries is one every client
    /// learns to ignore.
    /// </summary>
    [Fact]
    public async Task R9_8_TheRankingBreakdownIsReturnedOnlyWhenRequested()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var term = "zqx" + Guid.NewGuid().ToString("N")[..6];

        await AskAsync(client, board, $"About {term}", $"and the body says {term} twice: {term}", ct);

        using var without = await SearchAsync(client, $"q={term}&board={board}", ct);
        Assert.False(without.RootElement.GetProperty("results").EnumerateArray().Single()
            .TryGetProperty("why_ranked", out _));

        using var with = await SearchAsync(client, $"q={term}&board={board}&why=true", ct);
        var why = with.RootElement.GetProperty("results").EnumerateArray().Single()
            .GetProperty("why_ranked");

        Assert.Equal(1, why.GetProperty("title_matches").GetInt32());
        Assert.Equal(2, why.GetProperty("body_matches").GetInt32());
        Assert.Equal(0, why.GetProperty("tag_matches").GetInt32());
        Assert.Equal((1 * 5) + (2 * 1), why.GetProperty("score").GetInt32());
    }

    /// <summary>
    /// R10.36: a withheld post is not searchable. Search is, if anything, the likelier way someone
    /// finds a post than a direct fetch by id, so a withholding that covered only
    /// <c>GET /v1/posts/{id}</c> would be a withholding in name.
    /// </summary>
    [Fact]
    public async Task R10_36_AWithheldPostDoesNotAppearInResults()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var term = "zqx" + Guid.NewGuid().ToString("N")[..6];

        var kept = await AskAsync(client, board, $"About {term}", "body", ct);
        var withheld = await AskAsync(client, board, $"Also about {term}", "body", ct);

        using (var before = await SearchAsync(client, $"q={term}&board={board}", ct))
            Assert.Equal(2, Ids(before).Length);

        await WithholdAsync(withheld, ct);

        using var after = await SearchAsync(client, $"q={term}&board={board}", ct);
        Assert.Equal((string[])[kept], Ids(after));
    }

    /// <summary>
    /// R9.6 names <c>verification &gt;= V2</c> as a structured filter, and §8's verification events
    /// do not exist — so the filter cannot work.
    ///
    /// <para><b>Refused rather than ignored.</b> A parameter accepted and silently dropped returns
    /// the unfiltered corpus to an agent that believes it asked for verified answers only, which is
    /// worse than the filter being absent: the agent cannot tell, and the whole point of R9.6 is
    /// that "an agent looking for a verified answer for a specific runtime version should be able to
    /// say so".</para>
    /// </summary>
    [Fact]
    public async Task R9_6_AFilterThatCannotBeHonouredIsRefusedRatherThanIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        using var response = await client.GetAsync(
            new Uri("/v1/search?q=jcs&min_verification=V2", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "curia/search/unsupported-filter",
            await response.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }

    /// <summary>An out-of-range page size is refused, so a client is told rather than served fewer.</summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000")]
    [InlineData("not-a-number")]
    public async Task AnOutOfRangePageSizeIsRefused(string limit)
    {
        var ct = TestContext.Current.CancellationToken;

        using var response = await forum.Client.GetAsync(
            new Uri($"/v1/search?q=jcs&limit={limit}", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "curia/search/invalid-limit",
            await response.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// R9.7 end to end: paging the whole result set returns each post exactly once, in the same
    /// order one large page would have produced.
    ///
    /// <para>The posts are arranged so that score <i>rises</i> with <c>seq</c> — the arrangement
    /// under which a cursor keyed on <c>seq</c> rather than on the ranking silently skips and
    /// repeats. It is the arrangement the domain-level defect hid under, so it is the one asserted
    /// over the wire.</para>
    /// </summary>
    [Fact]
    public async Task R9_7_PagingTheResultSetReturnsEachPostExactlyOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var term = "zqx" + Guid.NewGuid().ToString("N")[..6];

        for (var i = 1; i <= 5; i++)
            await AskAsync(client, board, "Question " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join(" ", Enumerable.Repeat(term, i)), ct);

        var seen = new List<string>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var q = $"q={term}&board={board}&limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");
            using var found = await SearchAsync(client, q, ct);

            var ids = Ids(found);
            if (ids.Length == 0) break;

            seen.AddRange(ids);
            cursor = found.RootElement.TryGetProperty("next_cursor", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString()
                : null;

            if (cursor is null) break;
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());

        using var single = await SearchAsync(client, $"q={term}&board={board}&limit=25", ct);
        Assert.Equal(Ids(single), seen.ToArray());
    }

    /// <summary>A term nothing matches is an empty result set, not an error.</summary>
    [Fact]
    public async Task ATermNothingMatchesReturnsNoResults()
    {
        var ct = TestContext.Current.CancellationToken;

        using var found = await SearchAsync(forum.Client, "q=" + Guid.NewGuid().ToString("N"), ct);

        Assert.Empty(found.RootElement.GetProperty("results").EnumerateArray());
        Assert.False(
            found.RootElement.TryGetProperty("next_cursor", out var c) && c.ValueKind == JsonValueKind.String);
    }

    /// <summary>Appends a withholding action through the host's own store; no route creates one.</summary>
    private async Task WithholdAsync(string postId, CancellationToken ct)
    {
        using var scope = forum.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IEventStore>();

        static T Require<T>(Result<T> result) =>
            result.Match(v => v, e => throw new InvalidOperationException($"{e.Type}: {e.Title}"));

        var aggregate = Require(AggregateId.Create(postId));
        var history = Require(await store.ReadByAggregateAsync(aggregate, ct));

        Require(await store.AppendAsync(
            aggregate,
            Require(AggregateVersion.From(history.Count)),
            [new DomainEvent(
                Require(EventId.Create($"{postId}-mod")),
                Require(EventType.Create(FlagProjector.ModerationAppliedType)),
                Require(ActorId.Create("https://agents.example/moderator")),
                new JsonValue.Object(
                [
                    new(FlagProjector.PostIdField, new JsonValue.String(postId)),
                    new(FlagProjector.ModeratorField, new JsonValue.String(ModeratorKinds.Wire(ModeratorKind.Human))),
                    new(FlagProjector.ActorIdField, new JsonValue.String("https://agents.example/moderator")),
                    new(FlagProjector.EffectField, new JsonValue.String(ModerationEffects.Wire(ModerationEffect.Withhold))),
                    new(FlagProjector.CategoryField, new JsonValue.String(FlagKinds.Wire(FlagKind.Injection))),
                    new(FlagProjector.RationaleField, new JsonValue.String("reviewed")),
                ]))],
            ct));
    }
}
