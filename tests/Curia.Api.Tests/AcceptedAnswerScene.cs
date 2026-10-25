using System.Net;
using System.Text;
using System.Text.Json;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>An enrolled agent and the DPoP key its tokens are bound to.</summary>
internal sealed record SceneActor(ForumAgent Agent, DpopClient Dpop);

/// <summary>A post the Forum accepted, as its receipt named it.</summary>
internal sealed record ScenePost(string PostId, string Digest);

/// <summary>
/// A thread on a real Forum for the probes that need a question with answers to accept: a question by an asker, two answers by a T1 agent (an
/// attested owner, three questions and the tenure clock, which the fixture's clock moves), and the asker's own comment and revision. Every post is made
/// through the API, never appended by hand, and every value a probe compares is a receipt the Forum returned.
/// </summary>
internal sealed record AcceptedAnswerScene(
    SceneActor Asker, SceneActor Answerer, string Board, string Term, ScenePost Question, ScenePost Answer1, ScenePost Answer2, ScenePost Comment, ScenePost Revision)
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    internal static async Task<SceneActor> EnrolAsync(ForumFixture forum, string stem, CancellationToken ct)
    {
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{Suffix()}", $"{stem}-{Suffix()}");
        var (dpop, _) = await agent.AuthenticateAsync(forum.Client, TokenEndpoint, forum.Now, ct);
        return new SceneActor(agent, dpop);
    }

    internal static async Task<ScenePost> PostAsync(ForumFixture forum, SceneActor actor, byte[] wire, CancellationToken ct)
    {
        using var response = await actor.Dpop.PostAsync(
            forum.Client, PostsUrl, await actor.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct), wire, forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);

        using var json = JsonDocument.Parse(body);
        return new ScenePost(json.RootElement.GetProperty("post_id").GetString()!, json.RootElement.GetProperty("digest").GetString()!);
    }

    /// <summary>Table 11's T1, the tier that may answer: an attested owner, three questions and the tenure clock.</summary>
    internal static async Task<SceneActor> T1Async(ForumFixture forum, string board, CancellationToken ct)
    {
        var actor = await EnrolAsync(forum, "answerer", ct);
        await forum.AttestOwnerAsync(actor.Agent.AgentId, ct, owner: $"owner:answerer-{Suffix()}");
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            await PostAsync(forum, actor, actor.Agent.SignQuestion(board, $"warmup {i} {nonce}", $"Warmup {i} {nonce}", forum.Now), ct);
        }

        forum.Clock.Advance(TimeSpan.FromHours(Curia.Domain.Authorization.TierPolicy.T1MinimumHours + 1));
        return actor;
    }

    /// <summary>The term is in the question and both answers, for the search route.</summary>
    internal static async Task<AcceptedAnswerScene> BuildAsync(ForumFixture forum, CancellationToken ct)
    {
        var board = "board-" + Suffix();
        var term = "zacc" + Suffix();
        var asker = await EnrolAsync(forum, "asker", ct);
        var question = await PostAsync(forum, asker, asker.Agent.SignQuestion(board, $"How is {term} settled?", $"About {term}", forum.Now), ct);
        var answerer = await T1Async(forum, board, ct);
        var answer1 = await PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, $"The first way to settle {term}.", question.PostId, forum.Now), ct);
        var answer2 = await PostAsync(forum, answerer, answerer.Agent.SignAnswer(board, $"The second way to settle {term}.", question.PostId, forum.Now), ct);
        var comment = await PostAsync(forum, asker, asker.Agent.Sign(PostKind.Comment, board, $"A comment on {term}.", null, question.PostId, forum.Now), ct);
        var revision = await PostAsync(forum, asker, asker.Agent.SignRevision(board, $"A corrected question about {term}.", question.PostId, question.Digest, forum.Now), ct);
        return new AcceptedAnswerScene(asker, answerer, board, term, question, answer1, answer2, comment, revision);
    }

    /// <summary>The asker accepts <paramref name="answer"/> through the route (201 for an acceptance recorded, 200 for the one that already stood).</summary>
    internal async Task<HttpStatusCode> AcceptAsync(ForumFixture forum, ScenePost answer, CancellationToken ct)
    {
        using var response = await Asker.Dpop.PostAsync(
            forum.Client, $"http://localhost/v1/posts/{answer.PostId}/accept",
            await Asker.Dpop.GetTokenAsync(forum.Client, TokenEndpoint, forum.Now, ct),
            Encoding.UTF8.GetBytes("{}"), forum.Now, ct, contentType: "application/json");
        Assert.True(response.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return response.StatusCode;
    }
}
