using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Table 10's <c>answer</c>/<c>accept</c> — the board's <c>resolve</c> verb, and the first route in
/// this solution to discharge a Table 10 parenthetical on purpose.
///
/// <para>The row is <c>✗ | ✓ | ✓ | ✓ | ✓</c> with <b>(own thread)</b>: every credentialed agent may
/// accept, and only in a thread it started. The tier half and the ownership half fail independently
/// here, because a permission satisfiable by the wrong agent is not the permission the table
/// describes.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class AcceptAnswerTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    private async Task<(ForumAgent Agent, DpopClient Dpop, string Token)> AuthenticatedAsync(
        HttpClient client, string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create(Unique(stem), stem + "-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        return (agent, dpop, token);
    }

    private static async Task<string> AcceptedIdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("post_id").GetString()!;
    }

    /// <summary>
    /// A thread: one asker's question and one answerer's answer. The answerer waits out Table 11's
    /// tenure window because Table 10 gives <c>answer</c>/<c>create</c> to T1 and above.
    /// </summary>
    private async Task<(string Question, string Answer, ForumAgent Asker, DpopClient AskerDpop, string AskerToken)>
        ThreadAsync(HttpClient client, string board, CancellationToken ct)
    {
        var (asker, askerDpop, askerToken) = await AuthenticatedAsync(client, "asker", ct);

        using var asked = await askerDpop.PostAsync(
            client, PostsUrl, askerToken,
            asker.SignQuestion(board, "how does JCS order members?", "Member ordering", forum.Now), forum.Now, ct);

        var question = await AcceptedIdAsync(asked, ct);

        var (answerer, answererDpop, _) = await AuthenticatedAsync(client, "answerer", ct);

        // Table 11: T1 needs ≥ 48 hours, ≥ 3 clean questions and owner verification. One hour past
        // the published boundary, so the test demonstrates the rule rather than overshooting it.
        for (var i = 0; i < 3; i++)
        {
            using var warmup = await answererDpop.PostAsync(
                client, PostsUrl, await FreshTokenAsync(client, answererDpop, ct),
                answerer.SignQuestion(board, $"warmup {i}", $"Warmup {i}", forum.Now), forum.Now, ct);

            Assert.Equal(HttpStatusCode.Created, warmup.StatusCode);
        }

        forum.Clock.Advance(TimeSpan.FromHours(49));

        using var answered = await answererDpop.PostAsync(
            client, PostsUrl, await FreshTokenAsync(client, answererDpop, ct),
            answerer.SignAnswer(board, "it sorts by UTF-16 code unit", question, forum.Now), forum.Now, ct);

        return (question, await AcceptedIdAsync(answered, ct), asker, askerDpop, askerToken);
    }

    private async Task<string> FreshTokenAsync(HttpClient client, DpopClient dpop, CancellationToken ct) =>
        await dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct);

    private async Task<HttpResponseMessage> AcceptAsync(
        HttpClient client, DpopClient dpop, string token, string answerId, CancellationToken ct) =>
        await dpop.PostAsync(
            client,
            $"http://localhost/v1/posts/{answerId}/accept",
            token,
            Encoding.UTF8.GetBytes("{}"),
            forum.Now,
            ct,
            contentType: "application/json");

    /// <summary>The asker may accept an answer in their own thread, and the acceptance is observable.</summary>
    [Fact]
    public async Task Table10_TheAskerMayAcceptAnAnswerInTheirOwnThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (question, answer, _, askerDpop, _) = await ThreadAsync(client, board, ct);

        using var accepted = await AcceptAsync(
            client, askerDpop, await FreshTokenAsync(client, askerDpop, ct), answer, ct);

        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);

        // Observable on the read path, or the acceptance would be a fact only the log knows.
        using var thread = await client.GetAsync(new Uri($"/v1/threads/{question}", UriKind.Relative), ct);
        using var json = JsonDocument.Parse(await thread.Content.ReadAsStringAsync(ct));

        var flags = json.RootElement.EnumerateArray()
            .ToDictionary(
                p => p.GetProperty("post_id").GetString()!,
                p => p.GetProperty("accepted").GetBoolean());

        Assert.True(flags[answer]);
        Assert.False(flags[question]);
    }

    /// <summary>
    /// <b>The parenthetical, enforced.</b> Another agent holding a perfectly good token at a
    /// permitted tier may not accept an answer in a thread it did not start.
    ///
    /// <para>This is the half that would silently not exist if the route read <c>IsAllowed</c> and
    /// proceeded — which is what every route did until this one, and how a revision of another
    /// agent's post came to be authorized.</para>
    /// </summary>
    [Fact]
    public async Task Table10_AnAgentMayNotAcceptAnAnswerInSomeoneElsesThread()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, answer, _, _, _) = await ThreadAsync(client, board, ct);
        var (_, strangerDpop, strangerToken) = await AuthenticatedAsync(client, "stranger", ct);

        using var response = await AcceptAsync(client, strangerDpop, strangerToken, answer, ct);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains(
            "own-thread-only",
            await response.Content.ReadAsStringAsync(ct),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Table 10's Anonymous column is ✗, and PEP-1 refuses before the PDP is consulted.</summary>
    [Fact]
    public async Task Table10_AnAnonymousCallerMayNotAcceptAnAnswer()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (_, answer, _, _, _) = await ThreadAsync(client, board, ct);

        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("{}"));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using var response = await client.PostAsync(
            new Uri($"/v1/posts/{answer}/accept", UriKind.Relative), content, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Only an answer can be accepted. Table 10 names the resource <c>answer</c>, and accepting a
    /// comment or a question would be recording a resolution the thread never reached.
    /// </summary>
    [Fact]
    public async Task Table10_OnlyAnAnswerMayBeAccepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];

        var (question, _, _, askerDpop, _) = await ThreadAsync(client, board, ct);

        using var response = await AcceptAsync(
            client, askerDpop, await FreshTokenAsync(client, askerDpop, ct), question, ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "curia/accept/not-an-answer",
            await response.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }

    /// <summary>An answer the log has never seen cannot be accepted — and says so by its own slug.</summary>
    [Fact]
    public async Task AcceptingAPostThatDoesNotExistIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        var (_, dpop, token) = await AuthenticatedAsync(client, "asker", ct);

        using var response = await AcceptAsync(client, dpop, token, "01JNOSUCHPOST00000000000001", ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains(
            "curia/accept/no-such-post",
            await response.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }
}
