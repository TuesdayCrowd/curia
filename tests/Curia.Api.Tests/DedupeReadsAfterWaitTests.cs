using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Domain.Search;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Register item 286 (errata G71, R9.65), SQW9: the near-duplicate check reads the log after the embedding it waits for, and the thread a refusal carries is built from that read. A question the log
/// withholds while an ask waits is neither pointed at by a refusal nor makes the ask a duplicate; an answer the log withholds while an ask waits is not served in the refusal's thread. The park is
/// <see cref="ParkingEmbedderForumFixture"/>'s, on the ask's own text (the passage role, by a marker in its title), so the accepted post's indexing, the seeds' and any later call never park; each leg
/// asserts first that the park was reached (<c>NOT REACHED</c> otherwise), and the control in each leg is the same ask refused before the act, read by the same observer.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class DedupeReadsAfterWaitTests(ParkingEmbedderForumFixture forum) : IClassFixture<ParkingEmbedderForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";

    private ParkScene Scene => new(forum);

    private static bool Has(string marker, string text) => text.Contains(marker, StringComparison.Ordinal);

    private static string TitleOf(string marker) => $"ECONNRESET from npgsql after the pool idles {marker}";

    private static string BodyOf(string marker) => $"npgsql throws ECONNRESET after the pooler idles the connection; keepalive is off ({marker})";

    /// <summary>A near-duplicate and not a byte-identical one (the thresholds' doing, as <c>DedupeEndpointTests</c> asks it): one appended sentence.</summary>
    private static string AskBodyOf(string marker) => BodyOf(marker) + " Does anyone have a fix?";

    /// <summary>One ask by a fresh T0 agent: the status, the parsed body, and the log's size just before and just after the POST (the agent's enrolment is not in either).</summary>
    private async Task<(HttpStatusCode Status, JsonDocument Body, long Before, long After)> AskNearAsync(string board, string marker, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/dupe-{ParkScene.Suffix()}", "dupe-" + ParkScene.Suffix());
        var (dpop, token) = await agent.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var before = await Scene.EventCountAsync(ct);
        using var response = await dpop.PostAsync(forum.Client, ParkScene.PostsUrl, token, agent.SignQuestion(board, AskBodyOf(marker), TitleOf(marker), forum.Now), forum.Now, ct);
        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return (response.StatusCode, body, before, await Scene.EventCountAsync(ct));
    }

    /// <summary>What the observer reads of an ask: a refusal with the thread it names and how many answers it serves, or an acceptance and its <c>board_new</c>.</summary>
    private static string Observe((HttpStatusCode Status, JsonDocument Body, long Before, long After) ask, string questionId)
    {
        var root = ask.Body.RootElement;
        if (ask.Status == HttpStatusCode.Created)
            return $"201 accepted; board_new {(root.TryGetProperty("board_new", out var bn) ? bn.GetRawText() : "(absent)")}";
        if (ask.Status != HttpStatusCode.Conflict)
            return $"{(int)ask.Status} {(root.TryGetProperty("type", out var other) ? other.GetString() : "?")}";
        var names = root.GetProperty("canonical").GetProperty("post_id").GetString() == questionId;
        return $"409 {root.GetProperty("type").GetString()} {(names ? "naming the question" : "naming another post")}; answers {root.GetProperty("answers").GetArrayLength()}";
    }

    /// <summary>Table 11's T1, the tier that may answer: <c>DedupeEndpointTests.T1Async</c>'s lines, copied because that helper is private (the brief's step 2(c)). The warm-up questions go on their own board.</summary>
    private async Task<(ForumAgent Agent, DpopClient Dpop)> T1Async(string warmupBoard, CancellationToken ct)
    {
        var http = forum.Client;
        var suffix = ParkScene.Suffix();
        var agent = ForumAgent.Create($"https://agents.example/helper-{suffix}", $"helper-{suffix}");
        var (dpop, _) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        await forum.AttestOwnerAsync(agent.AgentId, ct, owner: "owner:helper-" + ParkScene.Suffix());
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            using var warmup = await dpop.PostAsync(http, ParkScene.PostsUrl, await dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct), agent.SignQuestion(warmupBoard, $"warmup {i} {nonce}", $"Warmup {i} {nonce}", forum.Now), forum.Now, ct);
            Assert.True(warmup.StatusCode == HttpStatusCode.Created, await warmup.Content.ReadAsStringAsync(ct));
        }

        forum.Clock.Advance(TimeSpan.FromHours(Curia.Domain.Authorization.TierPolicy.T1MinimumHours + 1));
        return (agent, dpop);
    }

    /// <summary>
    /// SQW9 (a). A question near a canonical question that is withheld while the ask waits is accepted, carries no thread, and its receipt says <c>board_new</c> true (the withheld question was the
    /// board's only served post). Control, same observer: before the withholding the same ask is refused <c>409</c> with the thread.
    /// </summary>
    [Fact]
    public async Task R9_65_SQW9a_AQuestionWithheldWhileAnAskWaitsIsNotTheThreadTheAskIsRefusedWith()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = Scene;
        var board = "dupe-" + ParkScene.Suffix();
        var marker = "zdupe" + ParkScene.Suffix();
        var canonical = await scene.AskAsync(board, TitleOf(marker), BodyOf(marker), ct);

        // Control, before the act: the same ask is refused with the thread, and the log is unchanged by the refusal.
        var control = await AskNearAsync(board, marker, ct);
        using (control.Body)
        {
            Assert.Equal("409 curia/posts/duplicate-question naming the question; answers 0", Observe(control, canonical.PostId));
            Assert.Equal(control.Before, control.After);
        }

        var park = forum.Embedder.Arm(EmbeddingRole.Passage, t => Has(marker, t));
        try
        {
            var parked = AskNearAsync(board, marker, ct);
            await park.EnteredAsync("the ask's passage");

            await forum.WithholdAsync(canonical.PostId, ct);
            Assert.Equal("404 curia/posts/withheld", await scene.ReadSaysAsync(canonical.PostId, ct));   // the log's own answer: the question is not served
            park.Release();

            var asked = await parked;
            using (asked.Body)
            {
                var said = Observe(asked, canonical.PostId);
                Assert.True(said == "201 accepted; board_new true", $"Expected: 201 accepted; board_new true  Actual: {said}");
            }
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }

    /// <summary>
    /// SQW9 (b). An answer withheld while an ask waits is not served in the refusal's thread: the question is still the canonical thread, and the <c>409</c> carries no answers. Control, same observer:
    /// before the withholding the same ask is refused with the answer in the thread.
    /// </summary>
    [Fact]
    public async Task R9_65_SQW9b_AnAnswerWithheldWhileAnAskWaitsIsNotServedInTheRefusalsThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = Scene;
        var board = "dupe-" + ParkScene.Suffix();
        var marker = "zdupe" + ParkScene.Suffix();
        var helper = await T1Async("warm-" + ParkScene.Suffix(), ct);
        var canonical = await scene.AskAsync(board, TitleOf(marker), BodyOf(marker), ct);

        string answerId;
        using (var answered = await helper.Dpop.PostAsync(
            forum.Client, ParkScene.PostsUrl, await helper.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct),
            helper.Agent.SignAnswer(board, "Set Keepalive=30 in the connection string.", canonical.PostId, forum.Now), forum.Now, ct))
        {
            var text = await answered.Content.ReadAsStringAsync(ct);
            Assert.True(answered.StatusCode == HttpStatusCode.Created, text);
            answerId = JsonDocument.Parse(text).RootElement.GetProperty("post_id").GetString()!;
        }

        // Control, before the act: the same ask is refused and the thread carries the answer.
        var control = await AskNearAsync(board, marker, ct);
        using (control.Body)
        {
            Assert.Equal("409 curia/posts/duplicate-question naming the question; answers 1", Observe(control, canonical.PostId));
            Assert.Equal(control.Before, control.After);
        }

        var park = forum.Embedder.Arm(EmbeddingRole.Passage, t => Has(marker, t));
        try
        {
            var parked = AskNearAsync(board, marker, ct);
            await park.EnteredAsync("the ask's passage");

            await forum.WithholdAsync(answerId, ct);
            Assert.Equal("404 curia/posts/withheld", await scene.ReadSaysAsync(answerId, ct));   // the log's own answer: the answer is not served
            park.Release();

            var asked = await parked;
            using (asked.Body)
            {
                var said = Observe(asked, canonical.PostId);
                Assert.True(said == "409 curia/posts/duplicate-question naming the question; answers 0", $"Expected: 409 curia/posts/duplicate-question naming the question; answers 0  Actual: {said}");
            }
        }
        finally
        {
            park.Release();
            forum.Embedder.Disarm();
        }
    }
}
