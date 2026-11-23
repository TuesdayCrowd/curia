#pragma warning disable CA1305, CA1308, CA1707, CA1822, CA1031, CA2007, CA1861, CA1859, CA1062, CA1307, CA1310, CA1002, CA1819
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Curia.Canon.Jws;
using Curia.Domain.Search;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// OAS31 (PR N2, errata G70, Amendment 1's consumers table, rows C1 to C20): every consumer Task 0 found reads honoured acts. Each consumer is observed before and after a forged act row is inserted as the
/// application role, by the same observer, and the test fails by the consumer's name when its output moved. The rows covered here are C1, C2, C3, C4, C5, C7, C11, C13 and C19; C6, C9, C10, C12, C14 to C18 are
/// the rows of OAS36, OAS6, OAS15, OAS37, OAS27, OAS22 and OAS21 (each named in its own probe). Task 3b adds C8 (the inbox) and C12 (a vote on a post a forged row withheld) here, and C20 (a forged flag) is
/// OAS40's characterization.
/// </summary>
[Collection("forum")]
public sealed class OperatorActsConsumersTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> BatchStatusAsync(OperatorActsApi api, string digest)
    {
        using var r = await api.Http().PostAsJsonAsync("/v1/posts/batch", new { digests = new[] { digest } }, Ct);
        var body = await r.Content.ReadAsStringAsync(Ct);
        var item = JsonDocument.Parse(body).RootElement.GetProperty("items")[0];
        return $"{(int)r.StatusCode} {item.GetProperty("state").GetString()}";
    }

    private static async Task<string> BoardListsAsync(OperatorActsApi api, string board, string postId)
    {
        var body = await api.GetBodyAsync($"/v1/boards/{board}/posts", Ct);
        return body.Contains(postId, StringComparison.Ordinal) ? "lists the post" : "omits the post";
    }

    /// <summary>
    /// Every consumer of the table, observed before and after one act of one shape. <paramref name="genuine"/> true: the act is the genuine one (the fixture's signing writers); false: it is a forged row inserted as the application
    /// role, and <paramref name="forged"/> is called once per row forged, so the test knows how many rows the Forum was to read and skip. Each call builds its own subjects, so the genuine and the forged run share nothing.
    /// </summary>
    private async Task<List<(string Consumer, string Before, string After)>> ConsumerRowsAsync(OperatorActsApi api, bool genuine, Action forged, List<string> refusals)
    {
        var rows = new List<(string Consumer, string Before, string After)>();

        // A genuine writer whose row the Forum then skips says so (R6.65: curia/acts/not-honoured). The row is appended all the same, so the leg goes on and observes: a Forum that skips every genuine act then
        // fails by naming the consumers that did not move, with the writer's refusal beside them, and not by an exception of the fixture.
        async Task GenuineAsync(Func<Task> write)
        {
            try { await write(); }
            catch (InvalidOperationException e) when (e.Message.StartsWith("curia/acts/not-honoured", StringComparison.Ordinal)) { refusals.Add(e.Message); }
        }

        async Task WithholdAsync(string postId, string digest)
        {
            if (genuine) { await GenuineAsync(() => api.GenuineWithhold(postId, Ct)); return; }
            await api.ForgeModerationAsync(postId, digest, "withhold", Ct);
            forged();
        }

        async Task AttestAsync(string agentId, string owner)
        {
            if (genuine) { await GenuineAsync(() => api.GenuineAttest(agentId, Ct, owner)); return; }
            await api.ForgeAttestationNowAsync(agentId, owner, Ct);
            forged();
        }

        // A withhold of a served post: C2 (GetPost), C3 (thread), C4 (board), C5 (batch), C7 (search), C11 (the serving filter).
        var victim = await api.QuestionAsync("victim", Ct);
        async Task<string[]> PostConsumersAsync() =>
        [
            $"{await api.GetStatusAsync($"/v1/posts/{victim.Id}", Ct)}",
            $"{await api.GetStatusAsync($"/v1/threads/{victim.Id}", Ct)}",
            await BoardListsAsync(api, victim.Board, victim.Id),
            await BatchStatusAsync(api, victim.Digest),
            $"{await api.SearchCountAsync(victim.Words, victim.Board, Ct)} result(s)",
        ];
        string[] names = ["C2 GetPost", "C3 GetThread", "C4 ListBoard", "C5 Batch", "C7/C11 Search and the serving filter"];
        var postBefore = await PostConsumersAsync();
        await WithholdAsync(victim.Id, victim.Digest);
        var postAfter = await PostConsumersAsync();
        for (var i = 0; i < names.Length; i++) rows.Add((names[i], postBefore[i], postAfter[i]));

        // An attestation for an aged agent: C1 (the token's tier claim), C2 (provenance), C13 (re-announcement), C19 (the write gate).
        var board = OperatorActsApi.Board();
        var asker = await api.GenuineAsync("asker", Ct);
        var target = await api.QuestionByAsync(asker, board, Ct);
        var x = await api.GenuineAsync("aged", Ct);
        var first = await api.AskAsync(x, board, Ct);
        for (var i = 0; i < 2; i++) await api.AskAsync(x, board, Ct);
        forum.Clock.Advance(TimeSpan.FromHours(49));
        async Task<string[]> AgentConsumersAsync() =>
        [
            (await api.TokenAsync(x, Ct)).Tier ?? "no token",
            (await api.ProvenanceAsync(first.PostId, Ct)).OwnerVerified?.ToString().ToLowerInvariant() ?? "no provenance",
            await api.EnrollBodyMemberAsync(x.Agent, "owner_verified", Ct),
            OperatorActsApi.Said(await api.AnswerAsync(x, board, target, Ct)),
        ];
        string[] agentNames = ["C1 token tier", "C2 provenance owner_verified", "C13 re-announcement owner_verified", "C19 write gate (answer)"];
        var agentBefore = await AgentConsumersAsync();
        await AttestAsync(x.Agent.AgentId, "owner:aged-" + OperatorActsApi.Suffix());
        var agentAfter = await AgentConsumersAsync();
        for (var i = 0; i < agentNames.Length; i++) rows.Add((agentNames[i], agentBefore[i], agentAfter[i]));

        // Search diversification: six authors on one board, a same-owner attestation for each, and a seventh author nobody attests, posted last. R9.68 defers by position: six posts that
        // all share one owner only top their own window up and move nowhere, so the farm shows in `diversified` only where an independent post moves into the first window ahead of them.
        var term = "zdiv" + OperatorActsApi.Suffix();
        var divBoard = OperatorActsApi.Board();
        var posters = new List<OperatorActsApi.Actor>();
        for (var i = 0; i < 6; i++)
        {
            var a = await api.GenuineAsync("div" + i, Ct);
            posters.Add(a);
            Assert.Equal(201, (await api.PostRawAsync(a, a.Agent.SignQuestion(divBoard, $"{term} behaviour number {i} {RevisionProbe.Unrelated()}", $"{term} {i}", forum.Now), Ct)).Status);
        }

        var independent = await api.GenuineAsync("div-independent", Ct);
        Assert.Equal(201, (await api.PostRawAsync(independent, independent.Agent.SignQuestion(divBoard, $"{term} behaviour number independent {RevisionProbe.Unrelated()}", $"{term} independent", forum.Now), Ct)).Status);

        async Task<string> DeferredAsync() =>
            $"diversified x{Regex.Match(await api.GetBodyAsync($"/v1/search?q={term}&board={divBoard}&limit=3", Ct), "\"diversified\":(\\d+)").Groups[1].Value}";
        var divBefore = await DeferredAsync();
        var oneFarm = "owner:one-farm-" + OperatorActsApi.Suffix();
        foreach (var p in posters) await AttestAsync(p.Agent.AgentId, oneFarm);
        rows.Add(("C7 search diversification", divBefore, await DeferredAsync()));

        // C8: the inbox lists an open question before and after a withhold of it (Task 3b; observed on the unmodified consumers, where the forged row removed it).
        var inboxQuestion = await api.QuestionAsync("c8asker", Ct);
        var inboxReader = await api.GenuineAsync("c8reader", Ct);
        var inboxBefore = await api.InboxListsAsync(inboxReader, inboxQuestion.Board, inboxQuestion.Id, Ct);
        await WithholdAsync(inboxQuestion.Id, inboxQuestion.Digest);
        rows.Add(("C8 inbox", inboxBefore, await api.InboxListsAsync(inboxReader, inboxQuestion.Board, inboxQuestion.Id, Ct)));

        // C12: a vote on a served answer after a withhold of it says what a vote on an answer with no such row says (the control, in the same test). The delta path is OAS15's.
        // Its two actors are genuine T1 agents, made by the genuine writer: on a Forum that skips genuine acts the genuine leg cannot make them, and says so as a row that did not move.
        async Task C12Async()
        {
            var c12Board = OperatorActsApi.Board();
            var c12Asker = await api.GenuineAsync("c12asker", Ct);
            var c12Target = await api.QuestionByAsync(c12Asker, c12Board, Ct);
            var c12Answerer = await api.GenuineT1Async("c12answerer", c12Board, Ct);
            var control = await api.AnswerAsync(c12Answerer, c12Board, c12Target, Ct);
            var withheldAnswer = await api.AnswerAsync(c12Answerer, c12Board, c12Target, Ct);
            Assert.Equal(201, control.Status);
            Assert.Equal(201, withheldAnswer.Status);
            var c12Voter = await api.GenuineT1Async("c12voter", c12Board, Ct);
            var voteControl = OperatorActsApi.Said(await api.PostRawAsync(c12Voter, c12Voter.Agent.SignVote(c12Board, control.Digest, forum.Now), Ct));
            await WithholdAsync(withheldAnswer.PostId, withheldAnswer.Digest);
            var voteAfter = OperatorActsApi.Said(await api.PostRawAsync(c12Voter, c12Voter.Agent.SignVote(c12Board, withheldAnswer.Digest, forum.Now), Ct));
            Assert.Equal("201 (accepted)", voteControl);
            rows.Add(("C12 vote on an answer a row withheld", voteControl, voteAfter));
        }

        if (!genuine) await C12Async();
        else
        {
            try { await C12Async(); }
            catch (InvalidOperationException e) when (e.Message.StartsWith("curia/acts/not-honoured", StringComparison.Ordinal))
            {
                refusals.Add(e.Message);
                rows.Add(("C12 vote on an answer a row withheld", "(setup refused)", "(setup refused)"));
            }
        }

        return rows;
    }

    /// <summary>
    /// OAS31 (Task 6b, ruling 99): the table of consumers is run twice. First with the genuine act of each shape, and every consumer must move (<c>genuine != before</c>: a consumer that cannot move, an observer on the wrong
    /// route or a Forum that skipped every row and every genuine one too, would pass a before-equals-after test); then with the forged row of the same shape, and every consumer must not (<c>forged == before</c>, the
    /// probe). The forged rows are shown read: the audit's skipped total rises by exactly the number of rows the test forged.
    /// </summary>
    [Fact]
    public async Task OAS31_EveryConsumerReadsHonouredActs()
    {
        var api = new OperatorActsApi(forum);

        var refusals = new List<string>();
        var genuine = await ConsumerRowsAsync(api, genuine: true, () => { }, refusals);
        var unmoved = genuine.Where(r => r.After == r.Before).Select(r => $"{r.Consumer} ({r.Before})").ToArray();
        Assert.True(unmoved.Length == 0, $"Expected: a genuine act of the same shape moves every consumer  Actual: {unmoved.Length} did not move: {string.Join("; ", unmoved)}; the genuine writers refused {refusals.Count} time(s){(refusals.Count > 0 ? ": " + refusals[0] : "")}");

        var skippedBefore = await api.SkippedRowsAsync(Ct);
        var forgedRows = 0;
        var rows = await ConsumerRowsAsync(api, genuine: false, () => forgedRows++, []);
        var skippedAfter = await api.SkippedRowsAsync(Ct);

        Assert.Equal(genuine.Select(r => r.Consumer), rows.Select(r => r.Consumer));
        Assert.True(forgedRows > 0, "the test forged no row");
        Assert.Equal(forgedRows, skippedAfter - skippedBefore);

        WholeText.Same(
            string.Join("; ", rows.Select(r => $"{r.Consumer}: {r.Before}")),
            string.Join("; ", rows.Select(r => $"{r.Consumer}: {r.After}")));
        _ = HttpStatusCode.OK;
    }

    /// <summary>
    /// OAS40, a characterization (green by design, as OAS39 is; Amendment 6, ruling 78; register item 304, filed under 298): C20, a forged <c>flag.committed</c> row with its <c>flag_details</c> row, inserted as the
    /// application role. Observed on the unmodified consumers: the operator's flag listing shows it as an open flag and nothing else moves. Asserted: the listing holds the flag and, beside the control (a flag raised
    /// through the route), says what the genuine one says; and a tier (the author's, the raiser's), an owner, the post's serving state and the search result are what they were before the row. A flag has no
    /// standing of its own: an upheld flag demotes only through an honoured moderation record naming it. The fix of item 298 must edit this probe.
    /// </summary>
    [Fact]
    public async Task OAS40_AForgedFlagIsListedAsOpenAndMovesNoTierOwnerOrWithholding()
    {
        var api = new OperatorActsApi(forum);
        var post = await api.QuestionAsync("c20author", Ct);
        var raiser = await api.GenuineAsync("c20raiser", Ct);

        async Task<string> ObserveAsync()
        {
            var provenance = await api.ProvenanceAsync(post.Id, Ct);
            return string.Join("; ",
                $"author tier {(await api.TokenAsync(post.Who, Ct)).Tier}",
                $"raiser tier {(await api.TokenAsync(raiser, Ct)).Tier}",
                $"owner {provenance.Owner ?? "none"} verified {provenance.OwnerVerified.ToString()?.ToLowerInvariant()}",
                $"post {await api.GetStatusAsync($"/v1/posts/{post.Id}", Ct)}",
                $"search {await api.SearchCountAsync(post.Words, post.Board, Ct)}");
        }

        // The control, the genuine path: a flag raised through the route is listed open on another post, by the same observer.
        var other = await api.QuestionAsync("c20other", Ct);
        var flagger = await api.GenuineAsync("c20flagger", Ct);
        _ = await api.RaiseFlagAsync(flagger, other.Id, Ct);
        var genuineState = await api.FlagStateAsync(other.Id, Ct);
        WholeText.Same("state open", genuineState);

        var listedBefore = await api.OperatorAsync(Ct, "flags", "--post", post.Id);
        var before = await ObserveAsync();
        var flagId = await api.ForgeFlagAsync(post.Id, raiser.Agent.AgentId, Ct);
        var listedAfter = await api.OperatorAsync(Ct, "flags", "--post", post.Id);
        var after = await ObserveAsync();

        Assert.Contains("0 flag(s)", listedBefore.Out, StringComparison.Ordinal);
        Assert.Contains("1 flag(s)", listedAfter.Out, StringComparison.Ordinal);
        Assert.Contains(flagId, listedAfter.Out, StringComparison.Ordinal);
        WholeText.Same(genuineState, await api.FlagStateAsync(post.Id, Ct));
        WholeText.Same(before, after);
    }
}

/// <summary>
/// OAS41 (PR N2, errata G70, Amendment 6, ruling 76 (c)): one <c>VerifiedLog</c> per request. A counting <see cref="IContentVerifier"/> is registered on a second host over the fixture's database
/// (<c>WithWebHostBuilder</c>), in place of the verifier dictionary every route's <c>LifecycleVerifier</c> is composed from. For each of five routes the verifications the request makes are counted over the log
/// as it stands, then again after three more genuine operator acts have been recorded (each reaches R6.67's step 7, where the signature is verified); the difference is exactly three, never a multiple. The request's
/// own verifications (the DPoP proof, the client assertion, the post's signature) are the same both times and cancel: the comparison is of two counts over logs that differ by the three rows, so the baseline
/// needs no log with no act rows, which a database shared by the class's other legs cannot give.
/// </summary>
[Collection("forum")]
public sealed class OperatorActsRequestCostTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Counter
    {
        private long _calls;

        internal long Calls => Interlocked.Read(ref _calls);

        internal void Add() => Interlocked.Increment(ref _calls);
    }

    private sealed class CountingVerifier(IContentVerifier inner, Counter counter) : IContentVerifier
    {
        public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
        {
            counter.Add();
            return inner.Verify(input, sig, key);
        }
    }

    [Theory]
    [InlineData("GET /v1/posts/{id}")]
    [InlineData("GET /v1/search")]
    [InlineData("POST an ask")]
    [InlineData("POST /oauth/token")]
    [InlineData("POST /v1/agents (re-announcement)")]
    public async Task OAS41_ARequestVerifiesEachActRowOnceAndNeverTwice(string route)
    {
        ArgumentNullException.ThrowIfNull(route);
        var counter = new Counter();
        var real = forum.Services.GetRequiredService<IReadOnlyDictionary<string, IContentVerifier>>();
        var counting = real.ToDictionary(p => p.Key, p => (IContentVerifier)new CountingVerifier(p.Value, counter), StringComparer.Ordinal);
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IReadOnlyDictionary<string, IContentVerifier>>();
            services.AddSingleton<IReadOnlyDictionary<string, IContentVerifier>>(counting);
        }));
        using var client = host.CreateClient();
        var api = new OperatorActsApi(forum, client);

        var board = OperatorActsApi.Board();
        var subject = await api.QuestionAsync("c41subject", Ct);
        var actor = await api.GenuineAsync("c41actor", Ct);

        // The request under measurement is one HTTP request. An ask is a token request and a post: the token is acquired first, outside the count, so that what is counted is the post's own.
        async Task<Func<Task<string>>> PrepareAsync()
        {
            if (route == "POST an ask")
            {
                var (_, tokenBody) = await actor.Dpop.RequestTokenAsync(api.Http(), OperatorActsApi.Token, forum.Now, actor.Agent.AgentId, Ct);
                var token = JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString()!;
                var wire = actor.Agent.SignQuestion(board, RevisionProbe.Unrelated(), "Q " + RevisionProbe.Unrelated(), forum.Now);
                return async () =>
                {
                    using var response = await actor.Dpop.PostAsync(api.Http(), OperatorActsApi.Posts, token, wire, forum.Now, Ct);
                    return $"{(int)response.StatusCode}";
                };
            }

            return () => RequestAsync();
        }

        async Task<string> RequestAsync() => route switch
        {
            "GET /v1/posts/{id}" => $"{await api.GetStatusAsync($"/v1/posts/{subject.Id}", Ct)}",
            "GET /v1/search" => $"{await api.SearchCountAsync(subject.Words, subject.Board, Ct)} result(s)",
            "POST /oauth/token" => $"{(await api.TokenAsync(actor, Ct)).Reply.Status}",
            _ => await api.EnrollBodyMemberAsync(actor.Agent, "enrolled_at", Ct) is { } said ? said[..3] : "-",
        };

        async Task<(long Calls, string Said)> MeasureAsync()
        {
            var request = await PrepareAsync();
            var before = counter.Calls;
            var said = await request();
            return (counter.Calls - before, said);
        }

        // Warm the host (its first request builds things the second does not), then count over the log as it stands.
        _ = await MeasureAsync();
        var (baseline, baselineSaid) = await MeasureAsync();

        // Three more genuine acts, each over an agent of its own so that each is honoured and reaches step 7.
        for (var i = 0; i < 3; i++)
        {
            var attested = await api.GenuineAsync("c41attested", Ct);
            await api.GenuineAttest(attested.Agent.AgentId, Ct, "owner:c41-" + OperatorActsApi.Suffix());
        }

        var (grown, grownSaid) = await MeasureAsync();
        Assert.Equal(baselineSaid, grownSaid);
        WholeText.Same("3 more verifications", $"{grown - baseline} more verifications");
    }
}

/// <summary>
/// OAS41c (register item 286, errata G71, Amendment 3 rulings 28 and 31): OAS41's ask with the log growing while the ask waits. OAS41's row measures a log that does not grow during the wait, so it passes for
/// a seam that merely returned the first <c>VerifiedLog</c> again; this holds the delta. An operator act is appended <b>while the ask's embedding is parked</b> (the park is
/// <see cref="ParkingEmbedderForumFixture"/>'s, on the ask's own text), under a counting verifier as OAS41's. The ask that waited is counted against the same ask, unparked, over the log as it stands once the act
/// is in it: the request verifies each act row once, the one appended during its wait included, so the two counts are equal; and it costs exactly one more than the same ask over the log before the act.
/// The vacuity guards: the park is shown reached (<c>NOT REACHED</c> otherwise), and the appended act is shown honoured by reading back the attested agent's post, which says <c>owner_verified</c> false before and true after.
/// </summary>
[Collection("forum")]
public sealed class OperatorActsRequestCostWhileWaitingTests(ParkingEmbedderForumFixture forum) : IClassFixture<ParkingEmbedderForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Counter
    {
        private long _calls;

        internal long Calls => Interlocked.Read(ref _calls);

        internal void Add() => Interlocked.Increment(ref _calls);
    }

    private sealed class CountingVerifier(IContentVerifier inner, Counter counter) : IContentVerifier
    {
        public bool Verify(ReadOnlySpan<byte> input, ReadOnlySpan<byte> sig, PublicKeyMaterial key)
        {
            counter.Add();
            return inner.Verify(input, sig, key);
        }
    }

    [Fact]
    public async Task OAS41c_AnAskWhoseLogGrowsWhileItWaitsVerifiesEachActRowOnce()
    {
        var counter = new Counter();
        var real = forum.Services.GetRequiredService<IReadOnlyDictionary<string, IContentVerifier>>();
        var counting = real.ToDictionary(p => p.Key, p => (IContentVerifier)new CountingVerifier(p.Value, counter), StringComparer.Ordinal);
        await using var host = forum.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IReadOnlyDictionary<string, IContentVerifier>>();
            services.AddSingleton<IReadOnlyDictionary<string, IContentVerifier>>(counting);
        }));
        using var client = host.CreateClient();
        var api = new OperatorActsApi(forum, client);

        var board = OperatorActsApi.Board();
        var attested = await api.GenuineAsync("c41cattested", Ct);
        var attestedPost = await api.QuestionByAsync(attested, board, Ct);
        for (var i = 0; i < 2; i++)
        {
            var prior = await api.GenuineAsync("c41cprior", Ct);
            await api.GenuineAttest(prior.Agent.AgentId, Ct, "owner:c41c-" + OperatorActsApi.Suffix());
        }

        // One ask, as OAS41 makes it: the token is acquired first, outside the count, so that what is counted is the post's own. The marker makes the ask's own text the one the park matches.
        async Task<(long Calls, string Said)> AskAsync(string marker, Func<Task>? whileWaiting)
        {
            // A fresh agent for each ask: a T0 agent's question budget is spent by a few.
            var actor = await api.GenuineAsync("c41cask", Ct);
            var (_, tokenBody) = await actor.Dpop.RequestTokenAsync(api.Http(), OperatorActsApi.Token, forum.Now, actor.Agent.AgentId, Ct);
            var token = JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString()!;
            var wire = actor.Agent.SignQuestion(board, $"{marker} pelican failure modes under load", $"About {marker}", forum.Now);

            var before = counter.Calls;
            string said;
            if (whileWaiting is null)
            {
                using var response = await actor.Dpop.PostAsync(api.Http(), OperatorActsApi.Posts, token, wire, forum.Now, Ct);
                said = $"{(int)response.StatusCode}";
            }
            else
            {
                var park = forum.Embedder.Arm(EmbeddingRole.Passage, text => text.Contains(marker, StringComparison.Ordinal));
                try
                {
                    var posting = actor.Dpop.PostAsync(api.Http(), OperatorActsApi.Posts, token, wire, forum.Now, Ct);
                    await park.EnteredAsync("the ask's passage");
                    await whileWaiting();
                    park.Release();
                    using var response = await posting;
                    said = $"{(int)response.StatusCode}";
                }
                finally
                {
                    park.Release();
                    forum.Embedder.Disarm();
                }
            }

            return (counter.Calls - before, said);
        }

        async Task<string> ReadBackAsync() => (await api.ProvenanceAsync(attestedPost, Ct)).OwnerVerified switch
        {
            true => "owner_verified true",
            false => "owner_verified false",
            _ => "owner_verified (none)",
        };

        _ = await AskAsync("zwarm" + OperatorActsApi.Suffix(), null);
        var (beforeAct, beforeSaid) = await AskAsync("zbase" + OperatorActsApi.Suffix(), null);
        WholeText.Same("owner_verified false", await ReadBackAsync());

        var (waited, waitedSaid) = await AskAsync("zwait" + OperatorActsApi.Suffix(), () => api.GenuineAttest(attested.Agent.AgentId, Ct, "owner:c41c-" + OperatorActsApi.Suffix()));
        WholeText.Same("owner_verified true", await ReadBackAsync());

        var (grown, grownSaid) = await AskAsync("zgrown" + OperatorActsApi.Suffix(), null);

        Assert.Equal(beforeSaid, waitedSaid);
        Assert.Equal(beforeSaid, grownSaid);
        WholeText.Same($"{beforeAct + 1} verifications by the ask that waited", $"{waited} verifications by the ask that waited");
        WholeText.Same($"{grown} verifications by the ask that waited", $"{waited} verifications by the ask that waited");
    }
}
