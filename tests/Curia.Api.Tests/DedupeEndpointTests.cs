using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>§8.5 at the wire: R8.17–R8.20 and errata G10's question-only refusal.</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class DedupeEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private const string Title = "ECONNRESET from npgsql after the pool idles";
    private const string Body = "npgsql throws ECONNRESET after the pooler idles the connection; keepalive is off";

    private sealed record Participant(ForumAgent Agent, DpopClient Dpop);

    private async Task<Participant> EnrolAsync(HttpClient http, string stem, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
        var (dpop, _) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        return new Participant(agent, dpop);
    }

    /// <summary>Table 11's T1, the tier that may answer: an attested owner, three (distinct) questions, and the tenure clock.</summary>
    private async Task<Participant> T1Async(HttpClient http, string stem, string board, CancellationToken ct)
    {
        var p = await EnrolAsync(http, stem, ct);
        await forum.AttestOwnerAsync(p.Agent.AgentId, ct, owner: "owner:" + stem + "-" + Guid.NewGuid().ToString("N")[..6]);
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            var (status, body) = await SubmitAsync(http, p, p.Agent.SignQuestion(board, $"warmup {i} {nonce}", $"Warmup {i} {nonce}", forum.Now), ct);
            Assert.True(status == HttpStatusCode.Created, body.GetRawText());
        }
        forum.Clock.Advance(TimeSpan.FromHours(Curia.Domain.Authorization.TierPolicy.T1MinimumHours + 1));
        return p;
    }

    private async Task<(HttpStatusCode Status, JsonElement Body)> SubmitAsync(HttpClient http, Participant p, byte[] wire, CancellationToken ct)
    {
        using var response = await p.Dpop.PostAsync(
            http, PostsUrl, await p.Dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct), wire, forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var json = JsonDocument.Parse(body);
        return (response.StatusCode, json.RootElement.Clone());
    }

    private static string Board() => "dedupe-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task R8_18_R8_19_TheSameQuestionOnTheSameBoardIs409WithTheThreadAndItsAnswers()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var board = Board();
        var asker = await EnrolAsync(http, "asker", ct);
        var helper = await T1Async(http, "helper", board, ct);

        var (created, original) = await SubmitAsync(http, asker, asker.Agent.SignQuestion(board, Body, Title, forum.Now), ct);
        Assert.Equal(HttpStatusCode.Created, created);
        var originalId = original.GetProperty("post_id").GetString()!;
        var (answered, _) = await SubmitAsync(http, helper, helper.Agent.SignAnswer(board, "Set Keepalive=30 in the connection string.", originalId, forum.Now), ct);
        Assert.Equal(HttpStatusCode.Created, answered);

        // A near-duplicate, not a byte-identical one: one appended sentence, so the refusal is the
        // thresholds' doing and a threshold of 100 % would let it through (the plan's falsification).
        var again = await EnrolAsync(http, "again", ct);
        var (status, problem) = await SubmitAsync(http, again, again.Agent.SignQuestion(board, Body + " Does anyone have a fix?", Title, forum.Now), ct);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("curia/posts/duplicate-question", problem.GetProperty("type").GetString());
        Assert.Equal(originalId, problem.GetProperty("canonical").GetProperty("post_id").GetString());
        Assert.Equal(original.GetProperty("digest").GetString(), problem.GetProperty("canonical").GetProperty("digest").GetString());

        // R8.19: the answers, served with their provenance envelopes, not merely a link.
        var answer = Assert.Single(problem.GetProperty("answers").EnumerateArray());
        Assert.Equal("answer", answer.GetProperty("kind").GetString());
        Assert.Equal("V0", answer.GetProperty("provenance").GetProperty("verification_level").GetString());

        // R8.21: the measures, their thresholds, and the model -- and no span of the matched text.
        var similarity = problem.GetProperty("similarity");
        Assert.True(similarity.GetProperty("cosine_bp").GetInt32() >= similarity.GetProperty("refuse_cosine_bp").GetInt32());
        Assert.True(similarity.GetProperty("lexical_overlap_bp").GetInt32() >= similarity.GetProperty("refuse_lexical_overlap_bp").GetInt32());
        Assert.Equal(9400, similarity.GetProperty("refuse_cosine_bp").GetInt32());
        Assert.Equal("hashed-ngram@1", similarity.GetProperty("model").GetString());
        Assert.DoesNotContain("keepalive is off", problem.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not_duplicate", problem.GetProperty("override").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task R8_20_ASignedOverrideIsAcceptedAndAnnotatedAndServedAsSuch()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var board = Board();
        var asker = await EnrolAsync(http, "asker", ct);

        var (created, original) = await SubmitAsync(http, asker, asker.Agent.SignQuestion(board, Body, Title, forum.Now), ct);
        Assert.Equal(HttpStatusCode.Created, created);

        var again = await EnrolAsync(http, "again", ct);
        var (refused, _) = await SubmitAsync(http, again, again.Agent.SignQuestion(board, Body, Title, forum.Now), ct);
        Assert.Equal(HttpStatusCode.Conflict, refused);

        var (status, accepted) = await SubmitAsync(http, again,
            again.Agent.SignQuestionNotDuplicate(board, Body, Title, forum.Now, "that thread is about pgbouncer; this is the built-in pool"), ct);
        Assert.Equal(HttpStatusCode.Created, status);

        var served = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{accepted.GetProperty("post_id").GetString()}", ct);
        Assert.Equal(original.GetProperty("digest").GetString(), served.GetProperty("possible_duplicate_of").GetString());

        // A rationale-less override never verifies as an envelope: the Forum refuses it at ADMIT/VERIFY, not at dedupe.
        var (bare, bareProblem) = await SubmitAsync(http, again, again.Agent.Sign(
            Curia.Domain.Content.PostKind.Question, board, Body, Title, null, forum.Now,
            extra: [new("not_duplicate", new Curia.Canon.Json.JsonValue.Bool(true))]), ct);
        Assert.NotEqual(HttpStatusCode.Created, bare);
        Assert.Contains("rationale", bareProblem.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task R8_60_AnIdenticalAnswerIsAcceptedWithTheAnnotationNeverRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var board = Board();
        var asker = await EnrolAsync(http, "asker", ct);
        var first = await T1Async(http, "first", board, ct);
        var second = await T1Async(http, "second", board, ct);

        var (_, question) = await SubmitAsync(http, asker, asker.Agent.SignQuestion(board, Body, Title, forum.Now), ct);
        var questionId = question.GetProperty("post_id").GetString()!;
        const string fix = "Set Keepalive=30 in the connection string and the reset stops.";

        var (one, a1) = await SubmitAsync(http, first, first.Agent.SignAnswer(board, fix, questionId, forum.Now), ct);
        var (two, a2) = await SubmitAsync(http, second, second.Agent.SignAnswer(board, fix, questionId, forum.Now), ct);
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (one, two));

        var served = await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{a2.GetProperty("post_id").GetString()}", ct);
        Assert.Equal(a1.GetProperty("digest").GetString(), served.GetProperty("possible_duplicate_of").GetString());
    }

    [Fact]
    public async Task R8_60_ADifferentBoardIsADifferentAudience()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var asker = await EnrolAsync(http, "asker", ct);

        var (one, _) = await SubmitAsync(http, asker, asker.Agent.SignQuestion(Board(), Body, Title, forum.Now), ct);
        var (two, _) = await SubmitAsync(http, asker, asker.Agent.SignQuestion(Board(), Body, Title, forum.Now), ct);
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.Created), (one, two));
    }
}
