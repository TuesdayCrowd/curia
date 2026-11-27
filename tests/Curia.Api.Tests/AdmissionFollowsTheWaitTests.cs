using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Domain.Authorization;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Register item 308 (errata G88, R7.35), RC15 and RC19, on a real Forum over a real Postgres: a write is admitted by the decision taken on the read after the last wait it makes, and the decision
/// taken before the wait only refuses early. The park is <see cref="ParkingEmbedderForumFixture"/>'s, on the ask's own text (the passage role, by a marker in its title), so the accepted post's
/// indexing and every other call never park; each leg asserts first that the park was reached (<c>NOT REACHED</c> otherwise) and that the setup put the author where the leg says it is, because a
/// leg whose author never reached the tier proves nothing.
///
/// <para><b>The scenes</b> are Task 0's (<c>task-0-report.md</c> section 7), made through the route. A is the control, no act during the wait. B is an operator's moderation upheld against one of the
/// author's questions while the ask waits: the author falls from T1 to T0 on a stream that is not the author's own, so the author's append condition does not move and nothing but the decision after the
/// wait can refuse the ask. B' is the report's own scene B, a second post by the author, made through the route: the agent's hold (R7.27) serializes it behind the parked ask, so "4 of 3" is not reachable
/// that way. C is a withdrawn attestation, an act on the author's own stream, which the append condition already turned into a new attempt.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class AdmissionFollowsTheWaitTests(ParkingEmbedderForumFixture forum) : IClassFixture<ParkingEmbedderForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private ParkScene Scene => new(forum);

    private static string TitleOf(string marker) => $"Why does the pooler reset idle connections {marker}";

    private static string BodyOf(string marker) => $"A pooler between the app and the database drops idle connections after a while and the driver reports a reset ({marker}); which setting governs this?";

    private sealed record Party(ForumAgent Agent, DpopClient Dpop, string Token);

    private async Task<Party> PartyAsync(ForumAgent agent, CancellationToken ct)
    {
        var (dpop, token) = await agent.AuthenticateAsync(forum.Client, TokenEndpoint, forum.Now, ct);
        return new Party(agent, dpop, token);
    }

    private static ForumAgent NewAgent(string stem)
    {
        var suffix = ParkScene.Suffix();
        return ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
    }

    private async Task<(HttpStatusCode Status, string Body)> PostAsync(Party party, byte[] wire, CancellationToken ct)
    {
        using var response = await party.Dpop.PostAsync(forum.Client, ParkScene.PostsUrl, party.Token, wire, forum.Now, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<string> AskedAsync(Party party, string board, string stem, CancellationToken ct)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var (status, body) = await PostAsync(party, party.Agent.SignQuestion(board, $"{stem} body about canonical form {nonce}", $"{stem} {nonce}", forum.Now), ct);
        Assert.True(status == HttpStatusCode.Created, body);
        return JsonDocument.Parse(body).RootElement.GetProperty("post_id").GetString()!;
    }

    private async Task FlagAsync(Party raiser, string postId, CancellationToken ct)
    {
        using var raised = await raiser.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{postId}/flags", raiser.Token,
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind = "spam", rationale = "Advertising, not a question." })),
            forum.Now, ct, contentType: "application/json");
        Assert.Equal(HttpStatusCode.Created, raised.StatusCode);
    }

    /// <summary>What the observer reads of an answered ask: the status, the problem's type or the receipt's budget, and whether the log grew.</summary>
    private static string Observe(HttpStatusCode status, string body, long before, long after)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var said = status == HttpStatusCode.Created
            ? $"201 admitted; posting used {root.GetProperty("budget").GetProperty("used").GetInt32()} of {root.GetProperty("budget").GetProperty("limit").GetInt32()}"
            : $"{(int)status} {(root.TryGetProperty("type", out var type) ? type.GetString() : "?")} {(root.TryGetProperty("detail", out var detail) ? detail.GetString()!.Split(' ')[0] : "")}".TrimEnd();
        return $"{said}; log {(after > before ? "grew" : "did not grow")}";
    }

    /// <summary>
    /// A T1 author with three questions of its own (all clean) and, inside the trailing day, three answers: at T1 (a budget of 25) it has posted 3, at T0 (a budget of 3) its budget is spent. Answers and not
    /// questions, so that the clean-question count that holds T1 up (three) is the three questions above and an upheld flag on one of them is what takes the author to T0. Returns the author, the id of the question
    /// a reporter has flagged (the operator has not acted on it) and the owner the author is attested to.
    /// </summary>
    private async Task<(Party Author, string FlaggedQuestion, string Owner)> T1AuthorWithASpentT0BudgetAsync(CancellationToken ct)
    {
        var board = "admit-" + ParkScene.Suffix();
        var authorAgent = NewAgent("admit-author");
        var author = await PartyAsync(authorAgent, ct);
        var owner = "owner:admit-" + ParkScene.Suffix();
        await forum.AttestOwnerAsync(authorAgent.AgentId, ct, owner: owner);

        var questions = new List<string>();
        for (var i = 0; i < 3; i++)
            questions.Add(await AskedAsync(author, board, $"The author's question {i}", ct));

        var asker = await PartyAsync(NewAgent("admit-asker"), ct);
        var open = new List<string>();
        for (var i = 0; i < 3; i++)
            open.Add(await AskedAsync(asker, board, $"The asker's question {i}", ct));

        var reporter = await PartyAsync(NewAgent("admit-reporter"), ct);
        await FlagAsync(reporter, questions[0], ct);

        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));
        author = await PartyAsync(authorAgent, ct);
        for (var i = 0; i < 3; i++)
        {
            var (status, body) = await PostAsync(author, authorAgent.SignAnswer(board, $"Answer {i}: see the pooler's idle timeout.", open[i], forum.Now), ct);
            Assert.True(status == HttpStatusCode.Created, "the author did not reach T1 (an answer is a T1 act), so this leg proves nothing: " + body);
        }

        return (author, questions[0], owner);
    }

    /// <summary>The ask the legs park: a question on its own board by <paramref name="author"/>, its passage embedding armed to park.</summary>
    private (Task<(HttpStatusCode Status, string Body)> Parked, Park Park) ArmAsk(Party author, string marker, CancellationToken ct, string? board = null, string? body = null)
    {
        var park = forum.Embedder.Arm(EmbeddingRole.Passage, t => t.Contains(marker, StringComparison.Ordinal));
        var wire = author.Agent.SignQuestion(board ?? "admit-ask-" + ParkScene.Suffix(), body ?? BodyOf(marker), TitleOf(marker), forum.Now);
        return (PostAsync(author, wire, ct), park);
    }

    /// <summary>RC15 scene A, the control: no act during the wait, the ask is admitted and the budget is spent to 4 of 25.</summary>
    [Fact]
    public async Task R7_35_SceneA_AnAskWithNoActDuringItsWaitIsAdmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (author, _, _) = await T1AuthorWithASpentT0BudgetAsync(ct);
        var marker = "zadmita" + ParkScene.Suffix();
        var before = await Scene.EventCountAsync(ct);
        var (parked, park) = ArmAsk(author, marker, ct);
        try
        {
            await park.EnteredAsync("the ask's passage");
            park.Release();
            var (status, body) = await parked;
            Assert.Equal("201 admitted; posting used 4 of 25; log grew", Observe(status, body, before, await Scene.EventCountAsync(ct)));
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }

    /// <summary>
    /// RC15 scene B and RC19. An operator's moderation upheld against one of the author's questions lands while the ask waits: the author falls to T0, whose budget the three answers have spent, so the ask is
    /// refused for the budget by the decision after the wait. Control, same observer, scene A. Red on a build whose pre-wait decision admits (the ask is answered 201 and the author's day shows 4 of 3).
    /// </summary>
    [Fact]
    public async Task R7_35_SceneB_AnUpheldFlagDuringTheWaitRefusesTheAskThatTheEarlierDecisionWouldHaveAdmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var (author, flagged, _) = await T1AuthorWithASpentT0BudgetAsync(ct);
        var marker = "zadmitb" + ParkScene.Suffix();
        var before = await Scene.EventCountAsync(ct);
        var (parked, park) = ArmAsk(author, marker, ct);
        try
        {
            await park.EnteredAsync("the ask's passage");
            await forum.WithholdAsync(flagged, ct);
            var during = await Scene.EventCountAsync(ct);
            Assert.True(during > before, "the moderation record is not in the log, so the posture did not change during the wait");
            park.Release();
            var (status, body) = await parked;
            var said = Observe(status, body, during, await Scene.EventCountAsync(ct));
            Assert.True(said == "403 curia/authz/denied table-11/rate-budget-exhausted; log did not grow", $"Expected: 403 curia/authz/denied table-11/rate-budget-exhausted; log did not grow  Actual: {said}");
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }

    /// <summary>
    /// RC15 scene B'. The report's scene B appended a third post by the author straight into the store. Through the route a second post by the author queues behind the parked ask at the agent's hold
    /// (R7.27) and does not run until the first has committed: it is not answered while the ask is parked, the ask is admitted at 3 of 3, and the second is then refused for the budget. A control
    /// that the report's "used 4 of a limit of 3" was the harness and not the Forum.
    /// </summary>
    [Fact]
    public async Task R7_35_SceneBPrime_AnotherPostByTheAuthorThroughTheRouteWaitsBehindTheParkedAskAtTheHold()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "admit-" + ParkScene.Suffix();
        var authorAgent = NewAgent("admit-t0");
        var author = await PartyAsync(authorAgent, ct);
        for (var i = 0; i < 2; i++)
            await AskedAsync(author, board, $"A T0 author's question {i}", ct);

        var marker = "zadmitbp" + ParkScene.Suffix();
        var (parked, park) = ArmAsk(author, marker, ct);
        try
        {
            await park.EnteredAsync("the ask's passage");
            var second = PostAsync(author, authorAgent.SignQuestion(board, "A second ask made while the first waits, about quiet ledgers " + ParkScene.Suffix(), "Second ask " + ParkScene.Suffix(), forum.Now), ct);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            Assert.False(second.IsCompleted, "the second post by the author was answered while the first was parked: the agent's hold did not serialize it");
            park.Release();

            var (firstStatus, firstBody) = await parked;
            Assert.True(firstStatus == HttpStatusCode.Created, firstBody);
            Assert.Equal(3, JsonDocument.Parse(firstBody).RootElement.GetProperty("budget").GetProperty("used").GetInt32());

            var (secondStatus, secondBody) = await second;
            Assert.True(secondStatus == HttpStatusCode.Forbidden && secondBody.Contains("rate-budget-exhausted", StringComparison.Ordinal), $"{(int)secondStatus} {secondBody}");
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }

    /// <summary>
    /// RC15 scene C. A withdrawn attestation lands while the ask waits: an act on the author's own stream, so the append's condition moves and the attempt is made again. The same refusal as scene B, by a
    /// different road; it holds on a build without R7.35 and must still hold with it.
    /// </summary>
    [Fact]
    public async Task R7_35_SceneC_AWithdrawnAttestationDuringTheWaitRefusesTheAsk()
    {
        var ct = TestContext.Current.CancellationToken;
        var (author, _, owner) = await T1AuthorWithASpentT0BudgetAsync(ct);
        var marker = "zadmitc" + ParkScene.Suffix();
        var before = await Scene.EventCountAsync(ct);
        var (parked, park) = ArmAsk(author, marker, ct);
        try
        {
            await park.EnteredAsync("the ask's passage");
            await forum.AttestOwnerAsync(author.Agent.AgentId, ct, owner: owner, verified: false);
            var during = await Scene.EventCountAsync(ct);
            Assert.True(during > before, "the attestation is not in the log, so the posture did not change during the wait");
            park.Release();
            var (status, body) = await parked;
            var said = Observe(status, body, during, await Scene.EventCountAsync(ct));
            Assert.True(said == "403 curia/authz/denied table-11/rate-budget-exhausted; log did not grow", $"Expected: 403 curia/authz/denied table-11/rate-budget-exhausted; log did not grow  Actual: {said}");
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }

    /// <summary>
    /// RC15 scene D. Time passes during the wait: the three answers leave the trailing day, so the budget the receipt states is counted from the read and the instant after the wait (R7.35 "its budget counted"),
    /// "posting used 1 of 25", and not from the ones before it ("used 4 of 25"). The clock is advanced while the ask is parked, as the other scenes advance the log.
    /// </summary>
    [Fact]
    public async Task R7_35_SceneD_TheReceiptsBudgetIsCountedFromTheReadAfterTheWait()
    {
        var ct = TestContext.Current.CancellationToken;
        var (author, _, _) = await T1AuthorWithASpentT0BudgetAsync(ct);
        var marker = "zadmitd" + ParkScene.Suffix();
        var before = await Scene.EventCountAsync(ct);
        var (parked, park) = ArmAsk(author, marker, ct);
        try
        {
            await park.EnteredAsync("the ask's passage");
            forum.Clock.Advance(TimeSpan.FromHours(25));
            park.Release();
            var (status, body) = await parked;
            var said = Observe(status, body, before, await Scene.EventCountAsync(ct));
            Assert.True(said == "201 admitted; posting used 1 of 25; log grew", $"Expected: 201 admitted; posting used 1 of 25; log grew  Actual: {said}");
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }

    /// <summary>
    /// RC15 scene E, the order. An ask that is a near-duplicate of a question on its board, by an author the operator's moderation demotes while it waits, is refused <c>403</c> for the posture and is not told the
    /// thread of the question it duplicates: a caller denied is never told whether a post exists (R7.30), so the decision after the wait comes before the check's verdict is used. Control, same ask, no act: <c>409</c>
    /// duplicate with the thread.
    /// </summary>
    [Fact]
    public async Task R7_35_SceneE_TheDecisionAfterTheWaitComesBeforeTheNearDuplicatesRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var (author, flagged, _) = await T1AuthorWithASpentT0BudgetAsync(ct);
        var board = "admit-dupe-" + ParkScene.Suffix();
        var marker = "zadmite" + ParkScene.Suffix();
        _ = await Scene.AskAsync(board, TitleOf(marker), BodyOf(marker), ct);
        var nearBody = BodyOf(marker) + " Does anyone have a fix?";

        var control = ArmAsk(author, marker, ct, board, nearBody);
        try
        {
            await control.Park.EnteredAsync("the control ask's passage");
            control.Park.Release();
            var (controlStatus, controlBody) = await control.Parked;
            Assert.True(controlStatus == HttpStatusCode.Conflict && controlBody.Contains("duplicate-question", StringComparison.Ordinal), $"the control is not a duplicate refusal, so this leg proves nothing: {(int)controlStatus} {controlBody}");
        }
        finally
        {
            control.Park.Release();
            forum.Embedder.Disarm();
        }

        var (parked, park) = ArmAsk(author, marker, ct, board, nearBody + " (again)");
        try
        {
            await park.EnteredAsync("the ask's passage");
            await forum.WithholdAsync(flagged, ct);
            park.Release();
            var (status, body) = await parked;
            Assert.True(status == HttpStatusCode.Forbidden && body.Contains("rate-budget-exhausted", StringComparison.Ordinal) && !body.Contains("duplicate", StringComparison.Ordinal), $"{(int)status} {body}");
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }

    /// <summary>
    /// RC15 scene F, the early refusal is kept. A T0 author whose budget is spent is refused before the wait: the embedder is never asked for the ask's passage, so a request refused for its posture does not queue at
    /// R11.92's gate to be told so afterwards (item 308's reason for not moving the embedding above the decision).
    /// </summary>
    [Fact]
    public async Task R7_35_SceneF_AnAskRefusedBeforeTheWaitNeverReachesTheEmbedder()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "admit-" + ParkScene.Suffix();
        var authorAgent = NewAgent("admit-spent");
        var author = await PartyAsync(authorAgent, ct);
        for (var i = 0; i < 3; i++)
            await AskedAsync(author, board, $"A T0 author's question {i}", ct);

        var marker = "zadmitf" + ParkScene.Suffix();
        var (parked, park) = ArmAsk(author, marker, ct);
        try
        {
            var answered = await Task.WhenAny(parked, Task.Delay(TimeSpan.FromSeconds(10), ct));
            Assert.False(park.IsEntered, "the embedder was asked for the passage of a request the posture had already refused: the early refusal was not made");
            Assert.True(answered == parked, "the ask was not answered within 10 s");
            var (status, body) = await parked;
            Assert.True(status == HttpStatusCode.Forbidden && body.Contains("rate-budget-exhausted", StringComparison.Ordinal), $"{(int)status} {body}");
            Assert.False(park.IsEntered, "the embedder was asked for the passage of a request the posture had already refused: the early refusal was not made");
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }
}
