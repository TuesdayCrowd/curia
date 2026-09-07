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

        var lexical = why.GetProperty("lexical");
        Assert.Equal(1, lexical.GetProperty("title_matches").GetInt32());
        Assert.Equal(2, lexical.GetProperty("body_matches").GetInt32());
        Assert.Equal(0, lexical.GetProperty("tag_matches").GetInt32());
        Assert.Equal((1 * 5) + (2 * 1), lexical.GetProperty("score").GetInt32());
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
    /// R9.6's <c>verification &gt;= V</c> filter is honoured now that Stage 3's events exist, and
    /// R10.2's floor is stated on every response (errata G10). A floor evaluated against a level a
    /// question can never hold would hide every question, so it applies to gradable kinds only and
    /// the response says which.
    /// </summary>
    [Fact]
    public async Task R9_6_R10_2_AFloorIsHonouredAppliedToGradableKindsOnlyAndStated()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "floor-" + Guid.NewGuid().ToString("N")[..8];
        var questionId = await AskAsync(client, board, "ECONNRESET from npgsql", "npgsql ECONNRESET after the pool idles", ct);

        using var open = await SearchAsync(client, $"q=npgsql%20ECONNRESET&board={board}", ct);
        var floor = open.RootElement.GetProperty("floor");
        Assert.Equal(("rest-search", "V0", "published"),
            (floor.GetProperty("surface").GetString(), floor.GetProperty("min_verification").GetString(), floor.GetProperty("source").GetString()));
        Assert.Equal(["answer", "finding"], floor.GetProperty("applies_to").EnumerateArray().Select(k => k.GetString()));
        Assert.Contains("question", floor.GetProperty("not_applicable_to").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal("hashed-ngram@1", open.RootElement.GetProperty("model").GetString());
        Assert.Contains(questionId, Ids(open));

        // V2 requested: the question is still served, because the floor does not apply to it.
        using var gated = await SearchAsync(client, $"q=npgsql%20ECONNRESET&board={board}&min_verification=V2", ct);
        Assert.Equal(("V2", "requested"), (gated.RootElement.GetProperty("floor").GetProperty("min_verification").GetString(), gated.RootElement.GetProperty("floor").GetProperty("source").GetString()));
        Assert.Contains(questionId, Ids(gated));

        using var notAFloor = await client.GetAsync(new Uri("/v1/search?q=jcs&min_verification=V-", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.BadRequest, notAFloor.StatusCode);
        Assert.Contains("curia/search/not-a-floor", await notAFloor.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);

        using var unsupported = await client.GetAsync(new Uri("/v1/search?q=jcs&environment_version=4.3", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        Assert.Contains("curia/search/unsupported-filter", await unsupported.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// R9.25's three silently-degrading members. Each was accepted and then quietly not honoured,
    /// which an agent cannot detect: a correctly filtered page and an unfiltered page it believes
    /// was filtered are the same document. `/v1/search` already refused `verification`,
    /// `environment_version` and an out-of-range `limit` on exactly this reasoning; these three were
    /// the members the local convention had not reached.
    /// </summary>
    [Theory]
    [InlineData("cursor=not-a-cursor", "curia/search/cursor-malformed")]
    [InlineData("marking=datamarking", "curia/serving/unknown-marking")]
    [InlineData("why=yes", "curia/search/unknown-why")]
    public async Task R9_25_AMemberTheForumCannotHonourIsRefusedByName(string parameter, string slug)
    {
        var ct = TestContext.Current.CancellationToken;

        using var response = await forum.Client.GetAsync(
            new Uri($"/v1/search?q=jcs&{parameter}", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(slug, await response.Content.ReadAsStringAsync(ct), StringComparison.Ordinal);
    }

    /// <summary>
    /// R9.26 over the wire: `kind` takes a comma-separated set, so an agent can ask for the gradable
    /// kinds — {answer, finding} — in one request. That is the composition R10.2 (revised) tells an
    /// agent to make when it wants a verification floor, and the scalar could express only half of
    /// it. One unknown member in the set refuses the whole request by name rather than silently
    /// dropping that member, which is R9.25 applied inside a member's own value.
    /// </summary>
    [Fact]
    public async Task R9_26_TheKindMemberTakesASetAndRefusesAnUnknownMemberOfIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "kindset-" + Guid.NewGuid().ToString("N")[..8];
        await AskAsync(client, board, "ECONNRESET from npgsql", "npgsql ECONNRESET after the pool idles", ct);

        using var single = await SearchAsync(client, $"q=npgsql&board={board}&kind=question", ct);
        using var set = await SearchAsync(client, $"q=npgsql&board={board}&kind=answer,finding,question", ct);
        Assert.Equal(Ids(single), Ids(set));

        // The same set without `question` excludes it: the member is doing work, not being ignored.
        using var excluded = await SearchAsync(client, $"q=npgsql&board={board}&kind=answer,finding", ct);
        Assert.Empty(Ids(excluded));

        using var unknown = await client.GetAsync(
            new Uri($"/v1/search?q=npgsql&kind=answer,pamphlet", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        var body = await unknown.Content.ReadAsStringAsync(ct);
        Assert.Contains("curia/search/unknown-kind", body, StringComparison.Ordinal);
        Assert.Contains("pamphlet", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The spellings that ARE honoured keep working. A refusal rule that also refused the accepted
    /// forms would pass the test above while breaking every caller, which is the mirror defect.
    /// </summary>
    [Theory]
    [InlineData("marking=datamark")]
    [InlineData("marking=delimiters")]
    [InlineData("why=true")]
    [InlineData("why=1")]
    [InlineData("")]
    public async Task R9_25_TheHonouredSpellingsAreUnaffected(string parameter)
    {
        var ct = TestContext.Current.CancellationToken;

        using var response = await forum.Client.GetAsync(
            new Uri($"/v1/search?q=jcs&{parameter}", UriKind.Relative), ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>R9.8 / R8.36 (errata G10): every computed term, recombining exactly; every absent term named.</summary>
    [Fact]
    public async Task R9_8_WhyRankedRecombinesAndNamesWhatItDoesNotCompute()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "why-" + Guid.NewGuid().ToString("N")[..8];
        await AskAsync(client, board, "ECONNRESET from npgsql", "npgsql ECONNRESET after the pool idles", ct);

        using var doc = await SearchAsync(client, $"q=npgsql%20ECONNRESET&board={board}&why=true", ct);
        var hit = doc.RootElement.GetProperty("results")[0];
        var why = hit.GetProperty("why_ranked");

        Assert.Equal(60, why.GetProperty("k").GetInt32());
        Assert.True(why.GetProperty("lexical").GetProperty("rank").GetInt32() >= 1);
        Assert.True(why.GetProperty("vector").GetProperty("rank").GetInt32() >= 1);
        Assert.Equal("hashed-ngram@1", why.GetProperty("vector").GetProperty("model").GetString());
        // R6.33: integers only -- millionths and basis points -- so the terms recombine within rounding.
        var fused = why.GetProperty("fused_micro").GetInt64();
        Assert.InRange(why.GetProperty("lexical_term_micro").GetInt64() + why.GetProperty("vector_term_micro").GetInt64(), fused - 2, fused + 2);
        var expectedScore = fused * why.GetProperty("verification_weight_bp").GetInt32() / 10_000;
        Assert.InRange(why.GetProperty("score_micro").GetInt64(), expectedScore - 2, expectedScore + 2);
        Assert.Equal(hit.GetProperty("score_micro").GetInt64(), why.GetProperty("score_micro").GetInt64());
        Assert.Equal(10_000, why.GetProperty("verification_weight_bp").GetInt32());
        Assert.Equal("V0", why.GetProperty("verification_level").GetString());
        Assert.Contains("n_eff", why.GetProperty("not_computed").EnumerateObject().Select(p => p.Name));
        Assert.Contains("surprisingly_popular", why.GetProperty("not_computed").EnumerateObject().Select(p => p.Name));
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

        // Each body carries its own nonce word: five questions differing only in a repeat count are
        // exactly what §8.5 refuses, and this test is about paging, not dedupe.
        for (var i = 1; i <= 5; i++)
            await AskAsync(client, board, "Question " + i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join(" ", Enumerable.Repeat(term, i)) + " " + Guid.NewGuid().ToString("N"), ct);

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

    /// <summary>
    /// R9.22's floor, which is what the design actually promises: "a nearest-neighbour query always
    /// answers with <i>something</i>; without a floor, a query that matches nothing would fuse two
    /// hundred posts at cosine 0.05 into a page of noise and call it a result."
    ///
    /// <para><b>This replaces a test that asserted a random term returns nothing, which the design
    /// does not guarantee.</b> The vector channel is feature-hashed character trigrams in 256
    /// dimensions, so a random thirty-five-character term produces ~33 trigrams that collide with
    /// corpus trigrams by construction. Measured against the real embedder over 20,000 such terms:
    /// <b>1.735 % clear the 0.2 floor, reaching cosine 0.3154</b>. It therefore failed
    /// intermittently, and CI had seen it before — the comment this replaces recorded an earlier
    /// occurrence and narrowed the alphabet in response, treating the symptom. Recorded as plan
    /// defect D14, because a provisional constant that does not achieve its stated purpose is a
    /// finding about the constant rather than about the test.</para>
    ///
    /// <para>The query is a phrase this test seeds, not a random one, for a reason the first attempt
    /// at this rewrite got wrong: with a random term the floor usually admits nothing, the loop
    /// below runs zero times, and the test passes <i>vacuously</i> — removing the floor entirely
    /// left it green. That is `Expect.All` over an empty expectation, which G12 named. The guard
    /// asserting at least one vector-ranked result is therefore load-bearing, not decoration.</para>
    ///
    /// <para>No <c>board</c> filter, deliberately: the index is searched globally and only then
    /// intersected with the filtered corpus, so a board filter can empty the vector channel and
    /// restore the vacuity this test exists to avoid.</para>
    /// </summary>
    [Fact]
    public async Task R9_22_NoVectorNeighbourIsAdmittedBelowThePublishedMinimumCosine()
    {
        var ct = TestContext.Current.CancellationToken;
        var phrase = "chandelier ptarmigan quarrying vestibule";
        await AskAsync(
            forum.Client,
            "floor-" + Guid.NewGuid().ToString("N")[..8],
            "The " + phrase,
            "A body about " + phrase + ", sharing no vocabulary with any other fixture here.",
            ct);

        // A decoy on unrelated vocabulary. Without it the index could hold a single neighbour, the
        // floor would have nothing to exclude, and removing the floor would leave this test green --
        // vacuity one level up from the loop guard below.
        await AskAsync(
            forum.Client,
            "floor-" + Guid.NewGuid().ToString("N")[..8],
            "Yesterday's harbour timetable",
            "Ferries, tide tables, and the harbourmaster's revised timetable for yesterday.",
            ct);

        using var found = await SearchAsync(forum.Client, "q=" + phrase.Replace(" ", "%20", StringComparison.Ordinal) + "&why=true", ct);

        // Read the floor from the response rather than restating it: R9.22 requires it be published,
        // and a test carrying its own copy is a second place for it to drift.
        var floorBp = found.RootElement.GetProperty("min_cosine_bp").GetInt32();
        Assert.True(floorBp > 0, "the response does not publish min_cosine_bp");

        var vectorRanked = 0;
        foreach (var hit in found.RootElement.GetProperty("results").EnumerateArray())
        {
            if (!hit.GetProperty("why_ranked").TryGetProperty("vector", out var vector)
                || vector.ValueKind is JsonValueKind.Null)
            {
                continue;   // fused in on the lexical channel alone; the floor has nothing to say about it
            }

            vectorRanked++;
            var cosineBp = vector.GetProperty("cosine_bp").GetInt32();
            Assert.True(
                cosineBp >= floorBp,
                $"a vector neighbour was admitted at cosine {cosineBp} bp, below the published floor "
                + $"of {floorBp} bp. R9.22's floor is what keeps a query that matches nothing from "
                + "fusing a page of noise and calling it a result.");
        }

        Assert.True(
            vectorRanked > 0,
            "no result was vector-ranked, so the assertion above ran zero times and proved nothing. "
            + "That is a defect in this test, not in the floor.");
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
