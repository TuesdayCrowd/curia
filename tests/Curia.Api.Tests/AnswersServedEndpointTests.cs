using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

using Scene = Curia.Api.Tests.AcceptedAnswerScene;

namespace Curia.Api.Tests;

/// <summary>
/// R9.69 (errata G76), probes ANC2 to ANC5 over the real Forum: the number of answers the Forum serves, as <c>provenance.answers.served</c> on a question, one function with the duplicate refusal's
/// <c>answers=N</c>. <b>Oracles.</b> The posts the test made through the API and their receipts (so the count is a hand count of what the test posted, minus what the test withheld through
/// the operator's path), the raw response text, and the <c>ETag</c> header; never the index. Said plainly (the plan's own words for ANC2): both members calling one function makes ANC2's
/// agreement hold by construction; the independent oracle is the hand count, which both are held to.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed partial class AnswersServedEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private const string Title = "ECONNRESET from npgsql after the pool idles";
    private const string Body = "npgsql throws ECONNRESET after the pooler idles the connection; keepalive is off";

    [GeneratedRegex(@"\banswers=(\d+)\b", RegexOptions.CultureInvariant)]
    private static partial Regex AnswersEquals();

    private async Task<JsonElement> GetPostAsync(string postId, CancellationToken ct) =>
        await forum.Client.GetFromJsonAsync<JsonElement>(new Uri($"/v1/posts/{postId}", UriKind.Relative), ct);

    /// <summary><c>provenance.answers.served</c> of a served post, failing by name where the member or its count is missing.</summary>
    private static int ServedOf(JsonElement post, string where)
    {
        Assert.True(post.GetProperty("provenance").TryGetProperty("answers", out var answers), $"Expected: provenance.answers on a question ({where}) Actual: absent");
        Assert.True(answers.TryGetProperty("served", out var served) && served.ValueKind == JsonValueKind.Number, $"Expected: answers.served is a number ({where}) Actual: {answers.GetRawText()}");
        return served.GetInt32();
    }

    /// <summary>A refused duplicate of <paramref name="scene"/>'s question's text (one appended sentence, as DedupeEndpointTests makes one), by a fresh agent: the problem's <c>detail</c>.</summary>
    private async Task<string> RefusalDetailAsync(string board, CancellationToken ct)
    {
        var again = await Scene.EnrolAsync(forum, "again", ct);
        using var response = await again.Dpop.PostAsync(
            forum.Client, PostsUrl, await again.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct),
            again.Agent.SignQuestion(board, Body + " Does anyone have a fix?", Title, forum.Now), forum.Now, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Conflict, text);
        using var json = JsonDocument.Parse(text);
        return json.RootElement.GetProperty("detail").GetString()!;
    }

    private sealed record Thread(ScenePost Question, ScenePost Answer1, ScenePost Answer2, string Board, SceneActor Asker, SceneActor Answerer);

    /// <summary>A question (the dedupe text) with two answers by a T1 agent, on a board of its own: the hand count is 2.</summary>
    private async Task<Thread> ThreadAsync(CancellationToken ct)
    {
        var board = "anc-" + Guid.NewGuid().ToString("N")[..8];
        var asker = await Scene.EnrolAsync(forum, "asker", ct);
        var question = await Scene.PostAsync(forum, asker, asker.Agent.SignQuestion(board, Body, Title, forum.Now), ct);
        var answerer = await Scene.T1Async(forum, board, ct);
        var one = await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "Set Keepalive=30 in the connection string.", question.PostId, forum.Now), ct);
        var two = await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "Lower the pooler's idle timeout.", question.PostId, forum.Now), ct);
        return new Thread(question, one, two, board, asker, answerer);
    }

    /// <summary>
    /// ANC2. The duplicate refusal's <c>answers=N</c> and the question's <c>served</c> are one number, and it is the hand count: 2, then 1 after the operator withholds one answer. Red on the tree
    /// (the member is absent; the refusal's count is already right, and is asserted first so the red is the member's).
    /// </summary>
    [Fact]
    public async Task R9_69_ANC2_TheRefusalsAnswersAndTheQuestionsServedAreOneCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var thread = await ThreadAsync(ct);

        var refused = RefusalAnswers(await RefusalDetailAsync(thread.Board, ct));
        Assert.Equal(2, refused);
        Assert.Equal(refused, ServedOf(await GetPostAsync(thread.Question.PostId, ct), "after two answers"));

        await forum.WithholdAsync(thread.Answer2.PostId, ct);

        var refusedAfter = RefusalAnswers(await RefusalDetailAsync(thread.Board, ct));
        Assert.Equal(1, refusedAfter);
        Assert.Equal(refusedAfter, ServedOf(await GetPostAsync(thread.Question.PostId, ct), "after one withheld"));
    }

    private static int RefusalAnswers(string detail)
    {
        var match = AnswersEquals().Match(detail);
        Assert.True(match.Success, $"the refusal's detail has no answers=N: {detail}");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// ANC3. A question carries <c>{"served": n}</c> and n is the hand count over the posts the test made: the scene's two answers, a comment and the asker's revision of the question are on the
    /// thread, and only the two answers count; an answerer's revision of an answer and a withheld answer do not move it. Red on the tree.
    /// </summary>
    [Fact]
    public async Task R9_69_ANC3_AQuestionCarriesTheServedCountOfAnswerPostsOnly()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);

        Assert.Equal(2, ServedOf(await GetPostAsync(scene.Question.PostId, ct), "the scene: two answers, a comment, a revision of the question"));

        await Scene.PostAsync(forum, scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, "A corrected first answer.", scene.Answer1.PostId, scene.Answer1.Digest, forum.Now), ct);
        Assert.Equal(2, ServedOf(await GetPostAsync(scene.Question.PostId, ct), "after the answerer revised an answer"));

        await forum.WithholdAsync(scene.Answer2.PostId, ct);
        Assert.Equal(1, ServedOf(await GetPostAsync(scene.Question.PostId, ct), "after one answer was withheld"));
    }

    /// <summary>ANC3's control (green on the tree): an answer, a comment, a revision of the question and the revision of an answer carry no <c>answers</c> member, and the question carries one only where the test says.</summary>
    [Fact]
    public async Task R9_69_ANC3_Control_NoPostThatIsNotAQuestionCarriesTheMember()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        var answerRevision = await Scene.PostAsync(forum, scene.Answerer, scene.Answerer.Agent.SignRevision(scene.Board, "A corrected first answer.", scene.Answer1.PostId, scene.Answer1.Digest, forum.Now), ct);

        foreach (var (id, what) in new[] { (scene.Answer1.PostId, "an answer"), (scene.Comment.PostId, "a comment"), (scene.Revision.PostId, "a revision of the question"), (answerRevision.PostId, "a revision of an answer") })
        {
            var post = await GetPostAsync(id, ct);
            Assert.False(post.GetProperty("provenance").TryGetProperty("answers", out _), $"{what} carries provenance.answers");
        }
    }

    /// <summary>
    /// ANC4. The question's <c>ETag</c> moves when a second answer lands: a conditional read with the first tag is <c>200</c> and not <c>304</c>.
    /// <b>Green on the tree, and the plan's prediction (red) was wrong (trap 268):</b> the validator is a hash of the whole served body, whose Acta proof moves with every append to the log, so the tag
    /// moves for a second answer today with no member in the body. This row therefore proves nothing about <c>answers</c>; it is kept as the control the plan asked for, and
    /// <see cref="R9_69_ANC4_TheServedCountInTheBodyMovesFromOneToTwo"/> is the probe that is red for the member's reason.
    /// </summary>
    [Fact]
    public async Task R9_69_ANC4_Control_TheQuestionsETagMovesWhenASecondAnswerLands()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, question, answerer) = await OneAnswerAsync(ct);

        using var first = await forum.Client.GetAsync(new Uri($"/v1/posts/{question.PostId}", UriKind.Relative), ct);
        var tag = first.Headers.ETag!.ToString();

        await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "The second answer.", question.PostId, forum.Now), ct);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/posts/{question.PostId}");
        request.Headers.TryAddWithoutValidation("If-None-Match", tag);
        using var conditional = await forum.Client.SendAsync(request, ct);

        Assert.True(conditional.StatusCode == HttpStatusCode.OK, $"Expected: 200 (the question changed: it has a second answer) Actual: {(int)conditional.StatusCode}");
        Assert.NotEqual(tag, conditional.Headers.ETag!.ToString());
    }

    /// <summary>ANC4, the probe that is red for the member's reason: the body a conditional read answers with says <c>served</c> 2 where the first body said 1. Red on the tree (no member).</summary>
    [Fact]
    public async Task R9_69_ANC4_TheServedCountInTheBodyMovesFromOneToTwo()
    {
        var ct = TestContext.Current.CancellationToken;
        var (board, question, answerer) = await OneAnswerAsync(ct);

        Assert.Equal(1, ServedOf(await GetPostAsync(question.PostId, ct), "after one answer"));

        await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "The second answer.", question.PostId, forum.Now), ct);

        Assert.Equal(2, ServedOf(await GetPostAsync(question.PostId, ct), "after a second answer"));
    }

    private async Task<(string Board, ScenePost Question, SceneActor Answerer)> OneAnswerAsync(CancellationToken ct)
    {
        var board = "anc4-" + Guid.NewGuid().ToString("N")[..8];
        var asker = await Scene.EnrolAsync(forum, "asker", ct);
        var question = await Scene.PostAsync(forum, asker, asker.Agent.SignQuestion(board, "How is anc4 settled " + Guid.NewGuid().ToString("N"), "A question for the validator", forum.Now), ct);
        var answerer = await Scene.T1Async(forum, board, ct);
        await Scene.PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, "The first answer.", question.PostId, forum.Now), ct);
        return (board, question, answerer);
    }

    private async Task<string> InboxBodyAsync(DpopClient dpop, string board, CancellationToken ct)
    {
        var token = await dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/inbox?board={board}");
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
        request.Headers.Add("DPoP", dpop.Proof("GET", "http://localhost/v1/inbox", forum.Now, token, nonce: null));
        using var response = await forum.Client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return text;
    }

    /// <summary>
    /// ANC5. The same question served as a single read, a thread member, a board row, a search hit and an inbox row carries the same <c>served</c>: the hand count, 2. Each route builds its own
    /// response (FANC9's harm is a path that builds its own and omits the member), so each is read. Red on the tree.
    /// </summary>
    [Fact]
    public async Task R9_69_ANC5_EveryRouteThatServesAQuestionCarriesTheSameServedCount()
    {
        var ct = TestContext.Current.CancellationToken;
        var scene = await Scene.BuildAsync(forum, ct);
        var id = scene.Question.PostId;
        var http = forum.Client;

        var single = ServedOf(await GetPostAsync(id, ct), "the single read");

        var thread = (await http.GetFromJsonAsync<JsonElement>(new Uri($"/v1/threads/{id}", UriKind.Relative), ct)).EnumerateArray()
            .Single(p => p.GetProperty("post_id").GetString() == id);
        var onThread = ServedOf(thread, "the thread route");

        var board = (await http.GetFromJsonAsync<JsonElement>(new Uri($"/v1/boards/{scene.Board}/posts", UriKind.Relative), ct)).EnumerateArray()
            .Single(p => p.GetProperty("post_id").GetString() == id);
        var onBoard = ServedOf(board, "the board listing");

        var search = (await http.GetFromJsonAsync<JsonElement>(new Uri($"/v1/search?q={scene.Term}&board={scene.Board}&limit=50", UriKind.Relative), ct)).GetProperty("results").EnumerateArray()
            .Select(r => r.GetProperty("post")).Single(p => p.GetProperty("post_id").GetString() == id);
        var onSearch = ServedOf(search, "a search hit");

        var reader = await Scene.EnrolAsync(forum, "reader", ct);
        using var inbox = JsonDocument.Parse(await InboxBodyAsync(reader.Dpop, scene.Board, ct));
        var row = inbox.RootElement.GetProperty("results").EnumerateArray().Single(p => p.GetProperty("post_id").GetString() == id);
        var onInbox = ServedOf(row, "an inbox row");

        Assert.Equal(2, single);
        Assert.Equal([2, 2, 2, 2], [onThread, onBoard, onSearch, onInbox]);
    }
}
