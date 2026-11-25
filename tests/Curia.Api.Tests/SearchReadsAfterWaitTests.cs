using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Application.Ports;
using Curia.Domain.Search;
using Curia.OperatorTool;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// What a probe of "a search reads the log after it waits" is made of, shared by <see cref="SearchReadsAfterWaitTests"/> and <see cref="DedupeReadsAfterWaitTests"/>: seeding, the observer, and the
/// operator acts. The park itself lives in <see cref="ParkingEmbedderForumFixture"/> and nowhere else.
///
/// <para><b>The observer</b> reads a declared (or withheld) post's id and <c>floor.excluded_disputed</c> together. The other seeded post is on every page, so an observer pointed at it counts every
/// search as stale whatever the Forum does (Task 0's pre-declared control: 300 of 300); the controls here are a search sent before the act and a search sent after the release.</para>
/// </summary>
internal sealed class ParkScene(ParkingEmbedderForumFixture forum)
{
    internal const string PostsUrl = "http://localhost/v1/posts";

    internal ParkingEmbedderForumFixture Forum => forum;

    internal static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    internal sealed record Seeded(ForumAgent Agent, string PostId, long LogIndex, string Digest);

    /// <summary>An agent at T0 and one question by it, on <paramref name="board"/>.</summary>
    internal async Task<Seeded> AskAsync(string board, string title, string body, CancellationToken ct, string[]? tags = null)
    {
        var agent = ForumAgent.Create($"https://agents.example/park-{Suffix()}", "park-" + Suffix());
        var (dpop, token) = await agent.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        using var response = await dpop.PostAsync(forum.Client, PostsUrl, token, agent.SignQuestion(board, body, title, forum.Now, tags), forum.Now, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        using var accepted = JsonDocument.Parse(text);
        var postId = accepted.RootElement.GetProperty("post_id").GetString()!;
        var digest = accepted.RootElement.GetProperty("digest").GetString()!;
        var served = await forum.Client.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct);
        return new Seeded(agent, postId, served.GetProperty("log_index").GetInt64(), digest);
    }

    /// <summary>One GET of the search route, as a status and a parsed body (a problem document parses too).</summary>
    internal async Task<(HttpStatusCode Status, JsonDocument Body)> GetAsync(string pathAndQuery, CancellationToken ct)
    {
        using var response = await forum.Client.GetAsync(new Uri(pathAndQuery, UriKind.Relative), ct);
        return (response.StatusCode, JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)));
    }

    internal static string[] Ids(JsonDocument page) =>
        [.. page.RootElement.GetProperty("results").EnumerateArray().Select(r => r.GetProperty("post").GetProperty("post_id").GetString()!)];

    internal static int ExcludedDisputed(JsonDocument page) => page.RootElement.GetProperty("floor").GetProperty("excluded_disputed").GetInt32();

    internal static string? NextCursor(JsonDocument page) =>
        page.RootElement.TryGetProperty("next_cursor", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

    /// <summary>What the observer reads of a page: whether each labelled post is on it, and (where the surface has one) the dispute counter; the withhold has none.</summary>
    internal static string Observe((HttpStatusCode Status, JsonDocument Body) page, bool counter, params (string Label, string Id)[] posts)
    {
        if (page.Status != HttpStatusCode.OK) return $"status {(int)page.Status}";
        var ids = Ids(page.Body);
        var said = posts.Select(p => $"{p.Label} {(ids.Contains(p.Id, StringComparer.Ordinal) ? "present" : "absent")}").ToList();
        if (counter) said.Add($"excluded_disputed {Number(ExcludedDisputed(page.Body))}");
        return string.Join("; ", said);
    }

    // ---- operator acts. DisputedContentTests.OperatorAsync and DeclareAsync are private to that class, so their lines are copied here (the brief's step 2(b)) and not edited there.

    private async Task<int> OperatorAsync(string pem, string declarationPem, CancellationToken ct, params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var declares = args[0] == "compromise";
        return await OperatorCommands.RunAsync(
            args, forum.ConnectionString, forum.Clock, forum.PermissiveGate(), OperatorCommands.LoadDeclarationSigner, stdout, stderr, ct,
            readLogSigningKeyPem: () => declares ? null : pem,
            readDeclarationSigningKeyPem: () => declares ? declarationPem : null);
    }

    /// <summary>Publishes the log key, then declares <paramref name="kid"/> compromised from <paramref name="fromIndex"/>; returns the declaration's log index.</summary>
    internal async Task<long> DeclareAsync(string agent, string kid, long fromIndex, CancellationToken ct)
    {
        var pem = LogSigningKey.GeneratePem();
        var declarationPem = await DeclarationKeyHost.PemAsync(forum, ct);
        Assert.Equal(0, await OperatorAsync(pem, declarationPem, ct, "sign-head", "--by", "ops"));

        // The declaration is the next entry of the log, so its log index is the count of entries before it.
        var declarationIndex = await CompromiseDeclarationTests.EventCountAsync(forum.ConnectionString, ct);
        Assert.Equal(0, await OperatorAsync(pem, declarationPem, ct, "compromise", "--agent", agent, "--kid", kid, "--from-index", Number(fromIndex),
            "--reason", "owner-reported", "--operator", "ops"));
        return declarationIndex;
    }

    /// <summary>The log's own answer for a post (the oracle, a different artifact from the search): the status and, when served, the provenance's key status and dispute index.</summary>
    internal async Task<string> ReadSaysAsync(string postId, CancellationToken ct)
    {
        var (status, body) = await GetAsync($"/v1/posts/{postId}", ct);
        using (body)
        {
            if (status != HttpStatusCode.OK) return $"{(int)status} {body.RootElement.GetProperty("type").GetString()}";
            var provenance = body.RootElement.GetProperty("provenance");
            return $"200 {provenance.GetProperty("key_status").GetString()}" +
                (provenance.TryGetProperty("disputed_by_log_index", out var by) ? $" by {Number(by.GetInt64())}" : "");
        }
    }

    internal async Task<long> EventCountAsync(CancellationToken ct) => await CompromiseDeclarationTests.EventCountAsync(forum.ConnectionString, ct);
}

/// <summary>
/// Register item 286 (errata G71, R9.65), SQW1 to SQW4 and SQW8 on a real Forum over a real Postgres: a search reads the log after the embedding it waits for, so a compromise declaration or a
/// withholding the log honours while the search is parked is on the page. The park is <see cref="ParkingEmbedderForumFixture"/>'s; each probe asserts first that the park was reached
/// (<c>NOT REACHED</c> otherwise), and each carries a control read by the same observer in the same test.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class SearchReadsAfterWaitTests(ParkingEmbedderForumFixture forum, ParkingEmbedderConfiguredFloorForumFixture configured)
    : IClassFixture<ParkingEmbedderForumFixture>, IClassFixture<ParkingEmbedderConfiguredFloorForumFixture>
{
    private ParkScene Scene => new(forum);

    private static bool Has(string term, string text) => text.Contains(term, StringComparison.Ordinal);

    /// <summary>Two questions on one board by two authors, each mentioning <paramref name="term"/>: the declared one and the one on every page.</summary>
    private static async Task<(string Board, string Term, ParkScene.Seeded P, ParkScene.Seeded Q)> SeedPairAsync(ParkScene scene, string stem, CancellationToken ct)
    {
        var board = "board-" + ParkScene.Suffix();
        var term = "zpark" + ParkScene.Suffix();
        var p = await scene.AskAsync(board, $"Signed by the first author {term}", $"this post mentions {term} once", ct);
        var q = await scene.AskAsync(board, $"Signed by the second author {term}", $"another, different post mentions {term} twice {stem}", ct);
        return (board, term, p, q);
    }

    /// <summary>SQW1. A compromise declaration appended while a search is parked at the embedding is on the page: the declared post is omitted and counted.</summary>
    [Fact]
    public async Task R9_65_SQW1_ADeclarationAppendedWhileASearchWaitsIsOnThePage()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = Scene;
        var (board, term, p, q) = await SeedPairAsync(scene, "sqw1", ct);
        var url = $"/v1/search?q={term}&board={board}";

        // Control, before the act: the observer can read "present" for the declared post, with the counter at 0.
        Assert.Equal("P present; Q present; excluded_disputed 0", ParkScene.Observe(await scene.GetAsync(url, ct), true, ("P", p.PostId), ("Q", q.PostId)));

        var park = forum.Embedder.Arm(EmbeddingRole.Query, t => Has(term, t));
        try
        {
            var parked = scene.GetAsync(url, ct);
            await park.EnteredAsync("the search's query");

            var declaredAt = await scene.DeclareAsync(p.Agent.AgentId, p.Agent.Kid, p.LogIndex, ct);
            // The oracle is the log's own answer, not the order the code ran in: the post is read as compromised, by this declaration, at the same instant.
            Assert.Equal($"200 compromised by {ParkScene.Number(declaredAt)}", await scene.ReadSaysAsync(p.PostId, ct));
            park.Release();

            var duringPage = await parked;
            // Control, after the release: the observer can read "absent" for the same post, with the counter at 1.
            Assert.Equal("P absent; Q present; excluded_disputed 1", ParkScene.Observe(await scene.GetAsync(url, ct), true, ("P", p.PostId), ("Q", q.PostId)));

            Assert.Equal("P absent; Q present; excluded_disputed 1", ParkScene.Observe(duringPage, true, ("P", p.PostId), ("Q", q.PostId)));
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }

    /// <summary>SQW2, the genuine leg. A genuine signed withholding appended while a search is parked is on the page; the read route has it at the same instant.</summary>
    [Fact]
    public async Task R9_65_SQW2_AWithholdingAppendedWhileASearchWaitsIsOnThePage()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = Scene;
        var (board, term, p, q) = await SeedPairAsync(scene, "sqw2", ct);
        var url = $"/v1/search?q={term}&board={board}";

        Assert.Equal("P present; Q present", ParkScene.Observe(await scene.GetAsync(url, ct), false, ("P", p.PostId), ("Q", q.PostId)));
        Assert.Equal("200 active", await scene.ReadSaysAsync(p.PostId, ct));

        var park = forum.Embedder.Arm(EmbeddingRole.Query, t => Has(term, t));
        try
        {
            var parked = scene.GetAsync(url, ct);
            await park.EnteredAsync("the search's query");

            await forum.WithholdAsync(p.PostId, ct);
            Assert.Equal("404 curia/posts/withheld", await scene.ReadSaysAsync(p.PostId, ct));
            park.Release();

            var duringPage = await parked;
            Assert.Equal("P absent; Q present", ParkScene.Observe(await scene.GetAsync(url, ct), false, ("P", p.PostId), ("Q", q.PostId)));

            Assert.Equal("P absent; Q present", ParkScene.Observe(duringPage, false, ("P", p.PostId), ("Q", q.PostId)));
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }

    /// <summary>
    /// SQW2, the forged leg. A <c>moderation.applied</c> row inserted as the application role, with no statement, while a search is parked, is not honoured (errata G70): the post stays on the page and is
    /// served by id. Green on the tree and after the fix; the control is a genuine withholding of another post in the same test, which the same observer reads as absent.
    /// </summary>
    [Fact]
    public async Task R9_65_SQW2_AForgedWithholdingRowAppendedWhileASearchWaitsLeavesThePostOnThePage()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = Scene;
        var (board, term, p, q) = await SeedPairAsync(scene, "sqw2f", ct);
        var url = $"/v1/search?q={term}&board={board}";
        var forger = new OperatorActsApi(forum);

        Assert.Equal("P present; Q present", ParkScene.Observe(await scene.GetAsync(url, ct), false, ("P", p.PostId), ("Q", q.PostId)));

        var park = forum.Embedder.Arm(EmbeddingRole.Query, t => Has(term, t));
        try
        {
            var parked = scene.GetAsync(url, ct);
            await park.EnteredAsync("the search's query");

            var before = await scene.EventCountAsync(ct);
            await forger.ForgeModerationAsync(p.PostId, p.Digest, "withhold", ct);
            Assert.Equal(before + 1, await scene.EventCountAsync(ct));   // the row is in the log
            Assert.Equal("200 active", await scene.ReadSaysAsync(p.PostId, ct));   // and the log does not honour it
            park.Release();

            var duringPage = await parked;
            Assert.Equal("P present; Q present", ParkScene.Observe(duringPage, false, ("P", p.PostId), ("Q", q.PostId)));
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }

        // Control, same observer: a genuine withholding of the other post is read as absent, so the observer above could have said so.
        await forum.WithholdAsync(q.PostId, ct);
        Assert.Equal("P present; Q absent", ParkScene.Observe(await scene.GetAsync(url, ct), false, ("P", p.PostId), ("Q", q.PostId)));
    }

    /// <summary>SQW3. A continuation parked at the embedding reads the log after it: a declaration appended while page 2 waits is on page 2.</summary>
    [Fact]
    public async Task R9_65_SQW3_ADeclarationAppendedWhileAContinuationWaitsIsOnThatPage()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = Scene;
        var board = "board-" + ParkScene.Suffix();
        var term = "zpark" + ParkScene.Suffix();
        var seeded = new List<ParkScene.Seeded>
        {
            await scene.AskAsync(board, $"First of three {term}", $"the first post mentions {term}", ct),
            await scene.AskAsync(board, $"Second of three, distinct {term}", $"the second, different post mentions {term} differently", ct),
            await scene.AskAsync(board, $"Third of three, again distinct {term}", $"a third and unlike post mentions {term} at length", ct),
        };
        var first = $"/v1/search?q={term}&board={board}&limit=1";

        string cursor;
        string secondId;
        using (var page1 = (await scene.GetAsync(first, ct)).Body)
        {
            Assert.Single(ParkScene.Ids(page1));
            cursor = ParkScene.NextCursor(page1)!;
            Assert.NotNull(cursor);
        }

        var second = first + "&cursor=" + Uri.EscapeDataString(cursor);
        using (var control = (await scene.GetAsync(second, ct)).Body)
        {
            // Control, before the act: page 2 serves one post, and it is the one the declaration will name.
            secondId = Assert.Single(ParkScene.Ids(control));
            Assert.Equal(0, ParkScene.ExcludedDisputed(control));
        }

        var declared = seeded.Single(s => s.PostId == secondId);
        var park = forum.Embedder.Arm(EmbeddingRole.Query, t => Has(term, t));
        try
        {
            var parked = scene.GetAsync(second, ct);
            await park.EnteredAsync("page 2's query");

            var declaredAt = await scene.DeclareAsync(declared.Agent.AgentId, declared.Agent.Kid, declared.LogIndex, ct);
            Assert.Equal($"200 compromised by {ParkScene.Number(declaredAt)}", await scene.ReadSaysAsync(declared.PostId, ct));
            park.Release();

            var duringPage = await parked;
            // Control, after the release: the same page 2 omits the declared post and counts it.
            Assert.Equal("D absent; excluded_disputed 1", ParkScene.Observe(await scene.GetAsync(second, ct), true, ("D", declared.PostId)));

            Assert.Equal("D absent; excluded_disputed 1", ParkScene.Observe(duringPage, true, ("D", declared.PostId)));
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }

    /// <summary>
    /// The members of a search response that depend on the process and the clock and not on the log: the vector index's backlog and the check's two instants (R9.51, R9.53). Read from
    /// <c>SearchResponse</c> and from one real response; <see cref="R9_65_SQW4_TheListOfMembersExcludedIsTheOneReadAndTheRestIsNotTheLogsToChange"/> asserts the response's member names, so a member
    /// added without a decision here fails that test and not the equality.
    /// </summary>
    private static readonly string[] StateDependent = ["vector_pending", "vector_checked_at", "vector_check_due_at"];

    private static readonly string[] ResponseMembers =
        ["results", "next_cursor", "floor", "model", "corpus_bound", "k", "candidate_depth", "min_cosine_bp", "vector_pending", "vector_checked_at", "vector_check_due_at", "unmatched_filters",
         // G64 (R9.67, R9.68): counts over the served page, so the log's and not the process's.
         "by_channel", "diversified",
         // G81 (R9.72): the query's ignored words, a function of the query's text alone.
         "keyword_ignored",
         // G81 (R9.72, Amendment 3): the distinct terms the keyword channel took, a function of the query's text alone.
         "keyword_terms"];

    private static string Normalised(JsonDocument page)
    {
        var node = JsonNode.Parse(page.RootElement.GetRawText())!.AsObject();
        foreach (var member in StateDependent) node.Remove(member);
        return node.ToJsonString();
    }

    private static async Task SeedForSqw4Async(ParkScene scene, string board, string term, CancellationToken ct)
    {
        await scene.AskAsync(board, $"First {term} question", $"the first post mentions {term} once", ct, ["tls"]);
        await scene.AskAsync(board, $"Second, quite distinct {term} question", $"the second, different post mentions {term} again and again", ct, ["tls", "pool"]);
        await scene.AskAsync(board, $"Third and unlike {term}", $"a third post mentions {term} at greater length than either", ct);
        // A post that holds only a term the vector channel finds by its characters and the lexical channel cannot find by any word of the query.
        await scene.AskAsync(board, $"{term}", $"{term} {term}", ct);
    }

    /// <summary>
    /// SQW4. A search that waits and nothing appended answers what the same search answers unarmed, member for member, for seven requests (a first page, no board, a continuation, a kind, a tag, a
    /// query only the vector channel can answer, and a request below the configured floor), except the three members that are the process's and not the log's. Green on the tree and after the fix:
    /// it holds the fix to "the page does not change".
    /// </summary>
    [Fact]
    public async Task R9_65_SQW4_AParkedSearchWithNothingAppendedAnswersWhatTheSameSearchAnswersUnarmed()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = Scene;
        var board = "board-" + ParkScene.Suffix();
        var term = "zpark" + ParkScene.Suffix();
        await SeedForSqw4Async(scene, board, term, ct);

        string? cursor;
        using (var firstPage = (await scene.GetAsync($"/v1/search?q={term}&limit=1", ct)).Body)
            cursor = ParkScene.NextCursor(firstPage);
        Assert.NotNull(cursor);   // control: request 3 has a page to continue

        var vectorOnly = $"{term}s";
        var requests = new (string Label, string Url, string Term)[]
        {
            ("first page, board", $"/v1/search?q={term}&board={board}&limit=10", term),
            ("first page, no board, limit 1", $"/v1/search?q={term}&limit=1", term),
            ("continuation", $"/v1/search?q={term}&limit=1&cursor={Uri.EscapeDataString(cursor!)}", term),
            ("kind", $"/v1/search?q={term}&board={board}&kind=question", term),
            ("tag", $"/v1/search?q={term}&board={board}&tags=tls", term),
            ("vector only", $"/v1/search?q={vectorOnly}&board={board}&why=true", vectorOnly),
        };

        foreach (var (label, url, marker) in requests)
        {
            var (unarmedStatus, unarmed) = await scene.GetAsync(url, ct);
            using (unarmed)
            {
                Assert.Equal(HttpStatusCode.OK, unarmedStatus);
                Assert.NotEmpty(ParkScene.Ids(unarmed));   // control: the request finds something, so equality is not equality of two empty pages

                var park = forum.Embedder.Arm(EmbeddingRole.Query, t => Has(marker, t));
                try
                {
                    var parked = scene.GetAsync(url, ct);
                    await park.EnteredAsync($"the query of '{label}'");
                    park.Release();
                    var (parkedStatus, parkedPage) = await parked;
                    using (parkedPage)
                    {
                        Assert.Equal(HttpStatusCode.OK, parkedStatus);
                        Assert.Equal(Normalised(unarmed), Normalised(parkedPage));
                    }
                }
                finally
                {
                    park.Release();
                    forum.Embedder.Disarm();
                }

                if (label == "vector only")
                {
                    // The request is vector-only: its top hit came through the vector channel and not the lexical one, or the request above says nothing about the vector channel.
                    var why = unarmed.RootElement.GetProperty("results")[0].GetProperty("why_ranked");
                    Assert.True(why.TryGetProperty("vector", out _) && !why.TryGetProperty("lexical", out _), $"not vector-only: {why.GetRawText()}");
                }
            }
        }

        // Request 7, on the Forum whose search surface is configured at V1: a request for V0 is below the floor and is served at the floor, and the page says so.
        var cscene = new ParkScene(configured);
        var cboard = "board-" + ParkScene.Suffix();
        var cterm = "zpark" + ParkScene.Suffix();
        await SeedForSqw4Async(cscene, cboard, cterm, ct);
        var below = $"/v1/search?q={cterm}&board={cboard}&min_verification=V0";
        var (cStatus, cUnarmed) = await cscene.GetAsync(below, ct);
        using (cUnarmed)
        {
            Assert.Equal(HttpStatusCode.OK, cStatus);
            Assert.True(cUnarmed.RootElement.GetProperty("floor").GetProperty("clamped").GetBoolean(), "the request is not below the floor, so the clamp is not exercised");
            Assert.NotEmpty(ParkScene.Ids(cUnarmed));

            var park = configured.Embedder.Arm(EmbeddingRole.Query, t => Has(cterm, t));
            try
            {
                var parked = cscene.GetAsync(below, ct);
                await park.EnteredAsync("the query of 'below the floor'");
                park.Release();
                var (cParkedStatus, cParked) = await parked;
                using (cParked)
                {
                    Assert.Equal(HttpStatusCode.OK, cParkedStatus);
                    Assert.Equal(Normalised(cUnarmed), Normalised(cParked));
                }
            }
            finally
            {
                park.Release();
                configured.Embedder.Disarm();
            }
        }
    }

    /// <summary>The member names of a real response are the ones <see cref="StateDependent"/> was decided against; a member added to the response fails here, naming itself.</summary>
    [Fact]
    public async Task R9_65_SQW4_TheListOfMembersExcludedIsTheOneReadAndTheRestIsNotTheLogsToChange()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = Scene;
        var board = "board-" + ParkScene.Suffix();
        var term = "zpark" + ParkScene.Suffix();
        await scene.AskAsync(board, $"One {term}", $"mentions {term}", ct);

        var (_, page) = await scene.GetAsync($"/v1/search?q={term}&board={board}", ct);
        using (page)
        {
            var named = page.RootElement.EnumerateObject().Select(m => m.Name).Where(n => n != "next_cursor").Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(ResponseMembers.Where(n => n != "next_cursor").Order(StringComparer.Ordinal), named);
        }

        Assert.Equal(["vector_check_due_at", "vector_checked_at", "vector_pending"], StateDependent.Order(StringComparer.Ordinal));
    }

    /// <summary>SQW8. The marking is decided before the cursor is compared, and each alone answers its own refusal: green on the tree and after the fix (FSQW6 moves the marking below the wait).</summary>
    [Fact]
    public async Task R9_65_SQW8_AnInvalidMarkingWithACursorOfAnotherQueryAnswersTheMarkingsRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = Scene;
        var (board, term, _, _) = await SeedPairAsync(scene, "sqw8", ct);

        string cursor;
        using (var page1 = (await scene.GetAsync($"/v1/search?q={term}&board={board}&limit=1", ct)).Body)
            cursor = ParkScene.NextCursor(page1)!;
        Assert.NotNull(cursor);
        var other = $"/v1/search?q={term}+other&board={board}&limit=1&cursor={Uri.EscapeDataString(cursor)}";

        // Two sources for each type: the code's constant where there is one, and a literal copied from the existing assertions (SearchEndpointTests:263 for the marking, NameMatchesNothingEndToEndTests:200 for the cursor).
        const string MarkingLiteral = "curia/serving/unknown-marking";
        const string CursorLiteral = "curia/search/cursor-query-mismatch";
        Assert.Equal(CursorLiteral, RetrievalCursor.QueryMismatch().Type);

        async Task<string> TypeAsync(string url)
        {
            var (status, body) = await scene.GetAsync(url, ct);
            using (body) return $"{(int)status} {(body.RootElement.TryGetProperty("type", out var t) ? t.GetString() : "(a page)")}";
        }

        // Each alone answers its own.
        Assert.Equal("400 " + CursorLiteral, await TypeAsync(other));
        Assert.Equal("400 " + MarkingLiteral, await TypeAsync($"/v1/search?q={term}&board={board}&marking=datamarking"));

        // Both: the marking's.
        Assert.Equal("400 " + MarkingLiteral, await TypeAsync(other + "&marking=datamarking"));
    }
}
