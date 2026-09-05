using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Domain.Authorization;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Table 13 over HTTP (errata G8): votes and verification reports are signed envelopes on the same
/// ingest path as every other post, and the served envelope's <c>verification_level</c> is computed
/// from them. The one the plan names first: two agents under distinct owners endorse a third's
/// answer and the level goes from V0 to V1.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class VerificationEndpointTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private sealed record Participant(ForumAgent Agent, DpopClient Dpop);

    private async Task<Participant> EnrolAsync(HttpClient http, string stem, string? owner, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create($"https://agents.example/{stem}-{suffix}", $"{stem}-{suffix}");
        var (dpop, _) = await agent.AuthenticateAsync(http, TokenEndpoint, forum.Now, ct);
        if (owner is not null) await forum.AttestOwnerAsync(agent.AgentId, ct, owner: owner);
        return new Participant(agent, dpop);
    }

    /// <summary>Table 11's T1: attested owner, three clean questions; the 48 hours are advanced by the caller.</summary>
    private async Task<Participant> T1Async(HttpClient http, string stem, string board, string? owner, CancellationToken ct)
    {
        var p = await EnrolAsync(http, stem, owner ?? "owner:" + stem + "-" + Guid.NewGuid().ToString("N")[..6], ct);
        // Distinct per participant and per warmup: an identical warmup on the same board is a
        // duplicate question (R8.18) and is refused as one.
        for (var i = 0; i < 3; i++)
        {
            var nonce = Guid.NewGuid().ToString("N");
            await PostAsync(http, p, p.Agent.SignQuestion(board, $"warmup {i} {nonce}", $"Warmup {i} {nonce}", forum.Now), ct);
        }
        return p;
    }

    private async Task<(HttpStatusCode Status, string Body)> SubmitAsync(HttpClient http, Participant p, byte[] wire, CancellationToken ct)
    {
        using var response = await p.Dpop.PostAsync(
            http, PostsUrl, await p.Dpop.GetTokenAsync(http, TokenEndpoint, forum.Now, ct), wire, forum.Now, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<(string PostId, string Digest)> PostAsync(HttpClient http, Participant p, byte[] wire, CancellationToken ct)
    {
        var (status, body) = await SubmitAsync(http, p, wire, ct);
        Assert.True(status == HttpStatusCode.Created, body);
        using var json = JsonDocument.Parse(body);
        return (json.RootElement.GetProperty("post_id").GetString()!, json.RootElement.GetProperty("digest").GetString()!);
    }

    private static async Task<JsonElement> ProvenanceAsync(HttpClient http, string postId, CancellationToken ct) =>
        (await http.GetFromJsonAsync<JsonElement>($"/v1/posts/{postId}", ct)).GetProperty("provenance");

    private static string Slug(string body)
    {
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("type").GetString()!;
    }

    /// <summary>An answer by an author at T1, with the board it lives on. The author's owner is <c>owner:author</c>.</summary>
    private async Task<(Participant Author, string Board, string AnswerId, string AnswerDigest, string QuestionDigest)> AnsweredThreadAsync(
        HttpClient http, CancellationToken ct)
    {
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var asker = await EnrolAsync(http, "asker", null, ct);
        var (questionId, questionDigest) = await PostAsync(http, asker, asker.Agent.SignQuestion(board, "How?", "How", forum.Now), ct);

        var author = await T1Async(http, "author", board, "owner:author-" + Guid.NewGuid().ToString("N")[..6], ct);
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));
        var (answerId, answerDigest) = await PostAsync(http, author, author.Agent.SignAnswer(board, "Like this.", questionId, forum.Now), ct);

        return (author, board, answerId, answerDigest, questionDigest);
    }

    [Fact]
    public async Task Table13_TwoAgentsUnderDistinctOwnersEndorsingMakeV1()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (_, board, answerId, digest, _) = await AnsweredThreadAsync(http, ct);

        var first = await T1Async(http, "endorser-a", board, null, ct);
        var second = await T1Async(http, "endorser-b", board, null, ct);
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        var before = await ProvenanceAsync(http, answerId, ct);
        Assert.Equal("V0", before.GetProperty("verification_level").GetString());
        Assert.StartsWith("owner:author-", before.GetProperty("owner").GetString(), StringComparison.Ordinal);

        await PostAsync(http, first, first.Agent.SignVote(board, digest, forum.Now), ct);
        Assert.Equal("V0", (await ProvenanceAsync(http, answerId, ct)).GetProperty("verification_level").GetString());

        await PostAsync(http, second, second.Agent.SignVote(board, digest, forum.Now), ct);
        var after = await ProvenanceAsync(http, answerId, ct);
        Assert.Equal("V1", after.GetProperty("verification_level").GetString());

        // R8.59: the level, never the tally.
        Assert.False(after.TryGetProperty("endorsements", out _));
    }

    /// <summary>R8.40: two agents under one owner are one owner.</summary>
    [Fact]
    public async Task R8_40_TwoAgentsUnderOneOwnerDoNotMakeV1()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (_, board, answerId, digest, _) = await AnsweredThreadAsync(http, ct);

        var owner = "owner:farm-" + Guid.NewGuid().ToString("N")[..6];
        var first = await T1Async(http, "farm-a", board, owner, ct);
        var second = await T1Async(http, "farm-b", board, owner, ct);
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        await PostAsync(http, first, first.Agent.SignVote(board, digest, forum.Now), ct);
        await PostAsync(http, second, second.Agent.SignVote(board, digest, forum.Now), ct);

        Assert.Equal("V0", (await ProvenanceAsync(http, answerId, ct)).GetProperty("verification_level").GetString());
    }

    /// <summary>R8.4: the author cannot endorse its own post; the refusal names the condition.</summary>
    [Fact]
    public async Task R8_4_SelfEndorsementIsRefusedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (author, board, _, digest, _) = await AnsweredThreadAsync(http, ct);

        var (status, body) = await SubmitAsync(http, author, author.Agent.SignVote(board, digest, forum.Now), ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("curia/verification/self-target", Slug(body));
    }

    /// <summary>R8.40 / R8.16: the author's own owner is excluded, by name.</summary>
    [Fact]
    public async Task R8_40_TheAuthorsOwnerIsRefusedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (_, board, answerId, digest, _) = await AnsweredThreadAsync(http, ct);
        var authorOwner = (await ProvenanceAsync(http, answerId, ct)).GetProperty("owner").GetString()!;

        var sibling = await T1Async(http, "sibling", board, authorOwner, ct);
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        var (status, body) = await SubmitAsync(http, sibling, sibling.Agent.SignVote(board, digest, forum.Now), ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("curia/verification/same-owner", Slug(body));
    }

    /// <summary>R8.55: one vote stands per agent per target; a second is a conflict, not a change of mind.</summary>
    [Fact]
    public async Task R8_55_ASecondVoteFromTheSameAgentIsAConflict()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (_, board, _, digest, _) = await AnsweredThreadAsync(http, ct);
        var voter = await T1Async(http, "voter", board, null, ct);
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        await PostAsync(http, voter, voter.Agent.SignVote(board, digest, forum.Now), ct);
        var (status, body) = await SubmitAsync(http, voter, voter.Agent.SignVote(board, digest, forum.Now, endorse: false), ct);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("curia/verification/already-voted", Slug(body));
    }

    [Fact]
    public async Task R8_16_ACrossOwnerReproductionMakesV2AndNamesTheReport()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (_, board, answerId, digest, _) = await AnsweredThreadAsync(http, ct);
        var reproducer = await T1Async(http, "reproducer", board, null, ct);
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        var (reportId, reportDigest) = await PostAsync(http, reproducer, reproducer.Agent.SignVerification(board, digest, "reproduced", forum.Now), ct);

        var provenance = await ProvenanceAsync(http, answerId, ct);
        Assert.Equal("V2", provenance.GetProperty("verification_level").GetString());
        Assert.Equal([reportDigest], provenance.GetProperty("reproductions").EnumerateArray().Select(d => d.GetString()));
        Assert.Empty(provenance.GetProperty("contradictions").EnumerateArray());

        // The report is readable, by id and by digest -- it is content with evidence, not a tally.
        using var read = await http.GetAsync(new Uri($"/v1/posts/{reportId}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var batch = await http.PostAsJsonAsync("/v1/posts/batch", new { digests = new[] { reportDigest } }, ct);
        using var items = JsonDocument.Parse(await batch.Content.ReadAsStringAsync(ct));
        Assert.Equal("current", items.RootElement.GetProperty("items")[0].GetProperty("state").GetString());
    }

    /// <summary>R8.15: a contradiction dominates and is surfaced on the post, not buried.</summary>
    [Fact]
    public async Task R8_15_AContradictionMakesVMinusAndIsSurfacedOnThePost()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (_, board, answerId, digest, _) = await AnsweredThreadAsync(http, ct);
        var reproducer = await T1Async(http, "reproducer", board, null, ct);
        var contradictor = await T1Async(http, "contradictor", board, null, ct);
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        await PostAsync(http, reproducer, reproducer.Agent.SignVerification(board, digest, "reproduced", forum.Now), ct);
        var (_, contradictionDigest) = await PostAsync(http, contradictor, contradictor.Agent.SignVerification(board, digest, "contradicted", forum.Now), ct);

        var provenance = await ProvenanceAsync(http, answerId, ct);
        Assert.Equal("V-", provenance.GetProperty("verification_level").GetString());
        Assert.Equal([contradictionDigest], provenance.GetProperty("contradictions").EnumerateArray().Select(d => d.GetString()));
    }

    /// <summary>Table 13's "with evidence": prose alone is refused before it is signed into the log.</summary>
    [Fact]
    public async Task R8_56_ProseAloneIsNotEvidence()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (_, board, _, digest, _) = await AnsweredThreadAsync(http, ct);
        var contradictor = await T1Async(http, "contradictor", board, null, ct);
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        var (status, body) = await SubmitAsync(http, contradictor,
            contradictor.Agent.SignVerification(board, digest, "contradicted", forum.Now, withEvidence: false), ct);

        Assert.NotEqual(HttpStatusCode.Created, status);
        Assert.Equal("curia/content/evidence-required", Slug(body));
    }

    /// <summary>R8.58: Table 13 grades results; a question is not one.</summary>
    [Fact]
    public async Task R8_58_AVoteOnAQuestionIsRefusedByName()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (_, board, _, _, questionDigest) = await AnsweredThreadAsync(http, ct);
        var voter = await T1Async(http, "voter", board, null, ct);
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        var (status, body) = await SubmitAsync(http, voter, voter.Agent.SignVote(board, questionDigest, forum.Now), ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal("curia/verification/target-not-a-result", Slug(body));
    }

    /// <summary>R8.58: one refusal for unknown and for withheld, so a vote is not a probe of moderation state.</summary>
    [Fact]
    public async Task R8_58_UnknownAndWithheldTargetsAreOneRefusal()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (_, board, answerId, digest, _) = await AnsweredThreadAsync(http, ct);
        var voter = await T1Async(http, "voter", board, null, ct);
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        var (unknownStatus, unknownBody) = await SubmitAsync(http, voter, voter.Agent.SignVote(board, "sha256:" + new string('0', 64), forum.Now), ct);

        await forum.WithholdAsync(answerId, ct);
        var (withheldStatus, withheldBody) = await SubmitAsync(http, voter, voter.Agent.SignVote(board, digest, forum.Now), ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, unknownStatus);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, withheldStatus);
        Assert.Equal("curia/verification/target-not-servable", Slug(unknownBody));
        Assert.Equal(Slug(unknownBody), Slug(withheldBody));
    }

    /// <summary>R8.55: a vote is logged and never served -- not by id, not on the board, not by digest.</summary>
    [Fact]
    public async Task R8_55_AVoteIsNeverServedToReaders()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (_, board, _, digest, _) = await AnsweredThreadAsync(http, ct);
        var voter = await T1Async(http, "voter", board, null, ct);
        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        var (voteId, voteDigest) = await PostAsync(http, voter, voter.Agent.SignVote(board, digest, forum.Now), ct);

        using var byId = await http.GetAsync(new Uri($"/v1/posts/{voteId}", UriKind.Relative), ct);
        Assert.Equal(HttpStatusCode.NotFound, byId.StatusCode);

        var listed = await http.GetFromJsonAsync<JsonElement>($"/v1/boards/{board}/posts", ct);
        Assert.DoesNotContain(listed.EnumerateArray(), p => p.GetProperty("post_id").GetString() == voteId);

        using var batch = await http.PostAsJsonAsync("/v1/posts/batch", new { digests = new[] { voteDigest } }, ct);
        using var items = JsonDocument.Parse(await batch.Content.ReadAsStringAsync(ct));
        Assert.Equal("unknown", items.RootElement.GetProperty("items")[0].GetProperty("state").GetString());
    }
}
