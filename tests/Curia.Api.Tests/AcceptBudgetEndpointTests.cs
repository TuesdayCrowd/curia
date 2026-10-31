using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Domain.Content;
using Curia.Tests.Shared;
using Npgsql;
using Xunit;

using Scene = Curia.Api.Tests.AcceptedAnswerScene;

namespace Curia.Api.Tests;

/// <summary>
/// R7.26 (errata G46), TRW13 to TRW21 against a real Forum on real Postgres: an acceptance spends no posting budget and has a budget of its own that a
/// replay never spends, decided before the post is looked up. (TRW12, which replaces PK40, is in <see cref="AcceptRevisionEndpointTests"/>.)
///
/// <para><b>Oracles.</b> The <c>answer.accepted</c> events in the events table with the asker as <c>accepted_by</c>, read by SQL and never from a response;
/// the reason, the three numbers and the cause list are the errata's, parsed at test time (<see cref="PublishedAcceptBudget"/>). Each refusal has its
/// control. TRW15, TRW17, TRW20 and TRW21 are green on the unmodified tree by design: there is no budget there to refuse them. The scene's asker is T0 with
/// two posts in the window (its question is 49 hours old), so a T0 asker reaches the budget of ten with the scene's two answers alternating.</para>
/// </summary>
[Collection("forum")]
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AcceptBudgetEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string NoSuchPost = "01M6FFFF000000000000000000";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record Reply(HttpStatusCode Status, string Raw, JsonElement Body);

    private async Task<Reply> AcceptRawAsync(SceneActor actor, string postId)
    {
        using var response = await actor.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{postId}/accept",
            await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
            Encoding.UTF8.GetBytes("{}"), forum.Now, Ct, contentType: "application/json");
        var raw = await response.Content.ReadAsStringAsync(Ct);
        return new Reply(response.StatusCode, raw, raw.StartsWith('{') ? JsonDocument.Parse(raw).RootElement.Clone() : default);
    }

    private async Task<Reply> AcceptedAsync(SceneActor actor, string postId, HttpStatusCode expected = HttpStatusCode.Created)
    {
        var reply = await AcceptRawAsync(actor, postId);
        Assert.True(reply.Status == expected, $"Expected: {(int)expected} {expected} Actual: {(int)reply.Status} {reply.Status} {reply.Raw}");
        return reply;
    }

    /// <summary>The answers the agent's <c>answer.accepted</c> events name, in log order: the events table, read by SQL.</summary>
    private async Task<string[]> AcceptedByAsync(string agentId)
    {
        await using var connection = new NpgsqlConnection(forum.ConnectionString);
        await connection.OpenAsync(Ct);
        await using var query = new NpgsqlCommand(
            "SELECT payload->>'answer_id' FROM events WHERE event_type = 'answer.accepted' AND payload->>'accepted_by' = @who ORDER BY seq;", connection);
        query.Parameters.AddWithValue("who", agentId);
        var answers = new List<string>();
        await using var reader = await query.ExecuteReaderAsync(Ct);
        while (await reader.ReadAsync(Ct)) answers.Add(reader.GetString(0));
        return [.. answers];
    }

    /// <summary>The first <c>count</c> acceptances of the scene's asker, alternating its two answers, each a new acceptance. Returns the last receipt.</summary>
    private async Task<Reply> SpendAsync(Scene scene, int count, int start = 0)
    {
        Reply? last = null;
        for (var i = start; i < start + count; i++)
            last = await AcceptedAsync(scene.Asker, (i % 2 == 0 ? scene.Answer1 : scene.Answer2).PostId);

        return last!;
    }

    private static int Budget(int tier) => PublishedAcceptBudget.Numbers()[tier];

    /// <summary>Reads the refusal's type, title and the reason the detail begins with.</summary>
    /// <summary>
    /// The detail an acceptance refusal at T0 carries (errata G49, R7.29): the published reason, the tier and ` next_slot_at=` the oldest acceptance still
    /// counted plus the window, the instant read from the events table (never from a response). The three whole-detail pins here asserted `reason tier=T0`
    /// alone; each now asserts the three parts, the instant by SQL.
    /// </summary>
    private async Task<string> RefusedDetailAsync(string agentId)
    {
        var oldest = (await BudgetReadouts.InstantsAsync(forum, "acceptance", agentId)).Where(i => i > forum.Now - BudgetReadouts.Window).Order().First();
        return $"{PublishedAcceptBudget.Reason()} tier=T0 {PublishedBudgetReadout.RefusalToken()}{BudgetReadouts.Form(oldest + BudgetReadouts.Window)}";
    }

    private static (string Type, string Title, string Detail) Refusal(Reply reply) =>
        reply.Body.ValueKind is JsonValueKind.Object && reply.Body.TryGetProperty("type", out var type)
            ? (type.GetString()!, reply.Body.GetProperty("title").GetString()!, reply.Body.TryGetProperty("detail", out var d) ? d.GetString()! : string.Empty)
            : (reply.Status.ToString(), string.Empty, reply.Raw);

    /// <summary>
    /// TRW13 (M9: a T1 asker is five requests, so this is required at the Api). A T1 asker with 25 posts in the window resolves: 24 comments are seeded
    /// through the host's store (the shape of a log this code did not write), the question is the 25th.
    /// </summary>
    [Fact]
    public async Task R7_26_TRW13_AT1AskerAtTwentyFivePostsResolves()
    {
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        // The answerer first: each T1Async moves the clock 49 hours, and the asker's own posts must all fall in the window that ends now.
        var answerer = await Scene.T1Async(forum, board, Ct);
        var asker = await Scene.T1Async(forum, board, Ct);
        var question = await Scene.PostAsync(forum, asker, asker.Agent.SignQuestion(board, "How is the budget spent?", "About the budget", forum.Now), Ct);
        var answer = await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "By counting.", question.PostId, forum.Now), Ct);

        // The asker's own posts in the window: the question, and 24 stored comments. The warm-up questions are older than the window.
        for (var i = 0; i < Curia.Domain.Authorization.TierPolicy.PostsPerDay(Curia.Domain.Authorization.PrincipalTier.T1) - 1; i++)
            await forum.AppendStoredPostAsync(asker.Agent.Sign(PostKind.Comment, board, $"Stored comment {i}.", null, question.PostId, forum.Now), Ct);

        var reply = await AcceptedAsync(asker, answer.PostId);

        Assert.Equal([answer.PostId], await AcceptedByAsync(asker.Agent.AgentId));
        Assert.Equal(HttpStatusCode.Created, reply.Status);
    }

    /// <summary>
    /// TRW14. The budget bites at the eleventh new acceptance: ten alternating acceptances are all <c>201</c>, the eleventh is <c>403</c> with the cause's
    /// title and the published reason and <c>tier=T0</c> in the detail, the asker's ten events and the standing acceptance are unchanged, and the asker's
    /// posting budget is untouched (a following question is stored).
    /// </summary>
    [Fact]
    public async Task R7_26_TRW14_TheBudgetBitesAtTheEleventhNewAcceptance()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var tenth = await SpendAsync(scene, Budget(0));

        var eleventh = await AcceptRawAsync(scene.Asker, scene.Answer1.PostId);

        var events = await AcceptedByAsync(scene.Asker.Agent.AgentId);
        Assert.True(
            eleventh.Status == HttpStatusCode.Forbidden,
            $"Expected: Forbidden ({PublishedAcceptBudget.Reason()}) at the eleventh. Actual: {(int)eleventh.Status} {eleventh.Status}; {events.Length} answer.accepted events");
        var (type, title, detail) = Refusal(eleventh);
        Assert.Equal(
            ("curia/authz/denied", await RefusedDetailAsync(scene.Asker.Agent.AgentId), Budget(0), scene.Answer2.PostId),
            (type, detail, events.Length, events[^1]));
        Assert.Contains("acceptance budget", title, StringComparison.Ordinal);
        Assert.Equal(scene.Answer2.PostId, tenth.Body.GetProperty("post_id").GetString());

        var question = await Scene.PostAsync(
            forum, scene.Asker, scene.Asker.Agent.SignQuestion(scene.Board, "Is the posting budget untouched?", "After the budget", forum.Now), Ct);
        Assert.False(string.IsNullOrEmpty(question.PostId));
    }

    /// <summary>
    /// TRW15 (green first, held by FTRW12 and FTRW13 through TRW5 and this). A replay at the limit is <c>200</c> and records nothing: after ten, accepting the
    /// answer that now stands answers <c>200</c>, <c>already_accepted</c>, the first <c>accepted_at</c>, and the events are still ten.
    /// </summary>
    [Fact]
    public async Task R7_26_TRW15_AReplayAtTheLimitIsFreeAndRecordsNothing()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var tenth = await SpendAsync(scene, Budget(0));

        var replay = await AcceptedAsync(scene.Asker, scene.Answer2.PostId, HttpStatusCode.OK);

        Assert.Equal(
            (true, tenth.Body.GetProperty("accepted_at").GetString(), Budget(0)),
            (replay.Body.GetProperty("already_accepted").GetBoolean(), replay.Body.GetProperty("accepted_at").GetString(), (await AcceptedByAsync(scene.Asker.Agent.AgentId)).Length));
    }

    /// <summary>
    /// TRW16. A switch is a new acceptance, whichever answer it names: at ten, the answer accepted earlier and not standing, and a third answer never
    /// accepted, are both <c>403</c> with the reason, and the events stay ten.
    /// </summary>
    [Fact]
    public async Task R7_26_TRW16_ASwitchIsANewAcceptanceWhicheverAnswerItNames()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        var third = await Scene.PostAsync(forum, scene.Answerer, scene.Answerer.Agent.SignAnswer(scene.Board, $"A third way to settle {scene.Term}.", scene.Question.PostId, forum.Now), Ct);
        await SpendAsync(scene, Budget(0));

        var earlier = await AcceptRawAsync(scene.Asker, scene.Answer1.PostId);
        var never = await AcceptRawAsync(scene.Asker, third.PostId);

        Assert.True(
            (earlier.Status, never.Status) == (HttpStatusCode.Forbidden, HttpStatusCode.Forbidden),
            $"Expected: Forbidden for a switch to an earlier answer and to a third. Actual: {(int)earlier.Status} and {(int)never.Status}");
        Assert.Equal(
            (await RefusedDetailAsync(scene.Asker.Agent.AgentId), await RefusedDetailAsync(scene.Asker.Agent.AgentId), Budget(0)),
            (Refusal(earlier).Detail, Refusal(never).Detail, (await AcceptedByAsync(scene.Asker.Agent.AgentId)).Length));
    }

    /// <summary>
    /// TRW17 (green first, held by FTRW9). Acceptances older than the window do not count: ten, the clock past 24 hours, and the eleventh is <c>201</c>. The
    /// count's window is the posting budget's trailing day (an acceptance and a post at one instant fall in or out together).
    /// </summary>
    [Fact]
    public async Task R7_26_TRW17_AcceptancesOlderThanTheWindowDoNotCount()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        await SpendAsync(scene, Budget(0));
        forum.Clock.Advance(TimeSpan.FromHours(25));

        var eleventh = await AcceptedAsync(scene.Asker, scene.Answer1.PostId);

        Assert.Equal(Budget(0) + 1, (await AcceptedByAsync(scene.Asker.Agent.AgentId)).Length);
        Assert.Equal(scene.Answer1.PostId, eleventh.Body.GetProperty("post_id").GetString());
    }

    /// <summary>
    /// TRW54 (green first, held by FTRW10). The window is the trailing 24 hours at the route, not a shorter one: an asker whose ten acceptances are half 13
    /// hours old and half new is refused the eleventh; with all ten 25 hours old the eleventh is stored (the contrast, so the refusal is the window's).
    /// </summary>
    [Fact]
    public async Task R7_26_TRW54_TheRouteCountsAcceptancesAcrossTheWholeDay()
    {
        var half = Budget(0) / 2;
        var inside = await Scene.BuildAsync(forum, Ct);
        await SpendAsync(inside, half);
        forum.Clock.Advance(TimeSpan.FromHours(13));
        await SpendAsync(inside, Budget(0) - half, start: half);

        var refused = await AcceptRawAsync(inside.Asker, inside.Answer1.PostId);
        var refusedDetail = await RefusedDetailAsync(inside.Asker.Agent.AgentId);   // read at the instant of the refusal, before the clock moves for the contrast

        var outside = await Scene.BuildAsync(forum, Ct);
        await SpendAsync(outside, Budget(0));
        forum.Clock.Advance(TimeSpan.FromHours(25));
        var allowed = await AcceptRawAsync(outside.Asker, outside.Answer1.PostId);

        Assert.True(
            (refused.Status, allowed.Status) == (HttpStatusCode.Forbidden, HttpStatusCode.Created),
            $"Expected: 403 for ten acceptances 13 and 0 hours old, 201 for ten 25 hours old. Actual: {(int)refused.Status} and {(int)allowed.Status}; {refused.Raw}");
        Assert.Equal(refusedDetail, Refusal(refused).Detail);
    }

    /// <summary>
    /// TRW18. A refusal reveals nothing of the target: an asker at the budget naming a real answer that is not the standing one, and naming an id no post
    /// bears, get the same status, type, title and detail (the budget is decided before the post is looked up, as an under-tier caller is told the tier).
    /// </summary>
    [Fact]
    public async Task R7_26_TRW18_ARefusalRevealsNothingOfTheTarget()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        await SpendAsync(scene, Budget(0));

        var real = await AcceptRawAsync(scene.Asker, scene.Answer1.PostId);
        var invented = await AcceptRawAsync(scene.Asker, NoSuchPost);

        Assert.True(
            real.Status == HttpStatusCode.Forbidden && invented.Status == HttpStatusCode.Forbidden,
            $"Expected: both 403, identical. Actual: {(int)real.Status} (real), {(int)invented.Status} {Refusal(invented).Type} (invented)");
        Assert.Equal(Refusal(real), Refusal(invented));
    }

    /// <summary>TRW19. The count is the asker's: a first asker at ten and a second asker at zero on their own thread, whose acceptance is <c>201</c>.</summary>
    [Fact]
    public async Task R7_26_TRW19_TheCountIsTheAskers()
    {
        var first = await Scene.BuildAsync(forum, Ct);
        var second = await Scene.BuildAsync(forum, Ct);
        await SpendAsync(first, Budget(0));

        var firstEleventh = await AcceptRawAsync(first.Asker, first.Answer1.PostId);
        Assert.True(firstEleventh.Status == HttpStatusCode.Forbidden, $"Expected: the first asker's eleventh 403. Actual: {(int)firstEleventh.Status} {firstEleventh.Status}");

        var secondFirst = await AcceptedAsync(second.Asker, second.Answer1.PostId);
        Assert.Equal(second.Answer1.PostId, secondFirst.Body.GetProperty("post_id").GetString());
    }

    /// <summary>
    /// TRW20 (green first, held by FTRW57). <c>accepted_by</c> is the token's subject, and a refusal writes nothing: after an acceptance by the asker the
    /// event names the asker, and a stranger's refused attempt adds no <c>answer.accepted</c> event.
    /// </summary>
    [Fact]
    public async Task R7_26_TRW20_AcceptedByIsTheTokensSubjectAndARefusalWritesNothing()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        await AcceptedAsync(scene.Asker, scene.Answer1.PostId);

        var stranger = await AcceptRawAsync(scene.Answerer, scene.Answer2.PostId);

        Assert.Equal(HttpStatusCode.Forbidden, stranger.Status);
        Assert.Contains("table-10/own-thread-only", stranger.Raw, StringComparison.Ordinal);
        Assert.Equal([scene.Answer1.PostId], await AcceptedByAsync(scene.Asker.Agent.AgentId));
        Assert.Empty(await AcceptedByAsync(scene.Answerer.Agent.AgentId));
    }

    /// <summary>TRW21 (green first, held by FTRW56). An acceptance draws nothing from the posting budget: after ten the asker, with two posts, asks a question.</summary>
    [Fact]
    public async Task R7_26_TRW21_AnAcceptanceDrawsNothingFromThePostingBudget()
    {
        var scene = await Scene.BuildAsync(forum, Ct);
        await SpendAsync(scene, Budget(0));

        using var response = await scene.Asker.Dpop.PostAsync(
            forum.Client,
            "http://localhost/v1/posts",
            await scene.Asker.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, Ct),
            scene.Asker.Agent.SignQuestion(scene.Board, "Is the posting budget intact?", "After ten acceptances", forum.Now),
            forum.Now,
            Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
