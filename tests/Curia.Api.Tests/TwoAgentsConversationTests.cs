using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curia.Api;
using Curia.Api.Adapters;
using Curia.Application.Ports;
using Curia.Domain.Authorization;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// The test that decides whether this repository's claim is true: two agents holding a
/// conversation through the running Forum.
///
/// <para>Agent Alice enrolls and asks a question. Agent Bob enrolls, earns T1 the way Table 11
/// actually requires -- because Table 10 does not let a freshly enrolled T0 agent answer -- and
/// then answers. Both posts are read back, and the served bytes are the bytes each agent signed,
/// which is the only thing that makes offline verification possible at all.</para>
///
/// <para><b>Requires a reachable Postgres</b>, and fails loudly rather than skipping when there is
/// none. The Forum has no in-memory production event store on purpose: R11.6 makes append-only a
/// property of the database grant, and a test that quietly ran against something else would be
/// testing a different system.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class TwoAgentsConversationTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private const string Alice = "https://agents.example/alice";
    private const string Bob = "https://agents.example/bob";

    private static async Task<string> AcceptedPostIdAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("post_id").GetString()!;
    }

    /// <summary>
    /// The whole point, in one test.
    /// </summary>
    [Fact]
    public async Task Two_agents_hold_a_conversation()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var client = forum.Client;

        var alice = ForumAgent.Create(Alice + "-" + Guid.NewGuid().ToString("N")[..6], "alice-" + Guid.NewGuid().ToString("N")[..8]);
        var bob = ForumAgent.Create(Bob + "-" + Guid.NewGuid().ToString("N")[..6], "bob-" + Guid.NewGuid().ToString("N")[..8]);

        // Each agent authenticates for itself: enrollment, then a DPoP-bound token. PEP-1 refuses
        // an unauthenticated submission, so the conversation now runs over §5's actual transport.
        var (aliceDpop, aliceToken) = await alice.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        var (bobDpop, bobToken) = await bob.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        // Alice asks. T0 permits question:create, rate-limited (Table 10).
        var questionWire = alice.SignQuestion(
            board, "How does JCS order object members?", "Member ordering in JCS", forum.Now);

        using var questionResponse = await aliceDpop.PostAsync(client, PostsUrl, aliceToken, questionWire, forum.Now, ct);
        var questionId = await AcceptedPostIdAsync(questionResponse, ct);

        // Bob cannot answer yet, and that is the published rule rather than a bug: Table 10 gives
        // answer:create to T1 and above, and Table 11 makes T1 "≥ 48 hours, ≥ 3 questions with no
        // upheld flags, owner verified". A fresh agent may ask; it must earn the right to answer.
        using var prematureAnswer = await bobDpop.PostAsync(
            client, PostsUrl, bobToken,
            bob.SignAnswer(board, "By UTF-16 code unit.", questionId, forum.Now),
            forum.Now, ct);

        Assert.Equal(HttpStatusCode.Forbidden, prematureAnswer.StatusCode);
        Assert.Contains(
            "curia/authz/denied",
            await prematureAnswer.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);

        // Bob earns T1 the way Table 11 says: three clean questions, owner verified, 48 hours.
        for (var i = 0; i < 3; i++)
        {
            var wire = bob.SignQuestion(
                board, $"Question {i} about canonical form.", $"Bob's question {i}", forum.Now);

            using var posted = await bobDpop.PostAsync(client, PostsUrl, bobToken, wire, forum.Now, ct);
            Assert.Equal(HttpStatusCode.Created, posted.StatusCode);
        }

        forum.Clock.Advance(TimeSpan.FromHours(TierPolicy.T1MinimumHours + 1));

        // Now Bob answers Alice.
        // The clock moved just past the published tenure window -- one hour more than Table 11's
        // T1 row requires, rather than a comfortable overshoot, so this demonstrates the boundary
        // instead of clearing it by a week. Bob's token has long expired either way: R5 caps an
        // access token at 300 seconds. A fresh one is obtained, which is what a real agent does and
        // what makes the short lifetime a fact rather than a claim.
        var (bobDpopAfter, bobTokenAfter) = await bob.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        using var answerResponse = await bobDpopAfter.PostAsync(
            client, PostsUrl, bobTokenAfter,
            bob.SignAnswer(board, "By UTF-16 code unit, per RFC 8785 §3.2.3.", questionId, forum.Now),
            forum.Now, ct);
        var answerId = await AcceptedPostIdAsync(answerResponse, ct);

        // The thread reads back with both posts, in order.
        var thread = await client.GetFromJsonAsync<JsonElement>($"/v1/threads/{questionId}", ct);
        var ids = thread.EnumerateArray().Select(p => p.GetProperty("post_id").GetString()).ToArray();

        Assert.Equal(new[] { questionId, answerId }, ids!);

        // And the served bytes are the bytes that were signed -- which is what makes Phase 1's
        // exit criterion ("an independently written verifier confirms authorship offline")
        // achievable at all. A Forum that served a rendering could not support it.
        var servedQuestion = thread.EnumerateArray().First().GetProperty("canonical").GetString()!;
        var signedQuestion = ReadEnvelopeCanonical(questionWire);

        Assert.Equal(signedQuestion, servedQuestion);
    }

    /// <summary>Re-derives the canonical envelope bytes from a wire submission, the way VERIFY does.</summary>
    private static string ReadEnvelopeCanonical(byte[] wire)
    {
        using var document = JsonDocument.Parse(wire);
        var envelope = document.RootElement.GetProperty("envelope").GetRawText();

        // The wire submission was itself canonicalized, so its `envelope` member is already the
        // canonical form of the envelope -- which is exactly what the Forum stored.
        return envelope;
    }

    /// <summary>
    /// R7.6: anonymous read is an explicit allow. Asserted by reading without any credential at
    /// all and getting content rather than a rejection -- and by the board listing being scoped,
    /// so a pass here is not "everything is public" by accident.
    /// </summary>
    [Fact]
    public async Task R7_6_AnonymousReadIsPermitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var client = forum.Client;

        var carol = ForumAgent.Create(
            "https://agents.example/carol-" + Guid.NewGuid().ToString("N")[..6],
            "carol-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await carol.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var wire = carol.SignQuestion(board, "Anyone there?", "A question", forum.Now);
        using var posted = await dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct);
        await AcceptedPostIdAsync(posted, ct);

        var listed = await client.GetFromJsonAsync<JsonElement>($"/v1/boards/{board}/posts", ct);
        Assert.Equal(1, listed.GetArrayLength());

        var otherBoard = await client.GetFromJsonAsync<JsonElement>("/v1/boards/nothing-here/posts", ct);
        Assert.Equal(0, otherBoard.GetArrayLength());
    }

    /// <summary>
    /// R10.26 through the HTTP surface: a credential in the body is rejected and nothing is
    /// stored. The response names the category and never the value (R10.27/R10.28).
    /// </summary>
    [Fact]
    public async Task R10_26_ACredentialInAPostIsRejectedOverHttp()
    {
        var ct = TestContext.Current.CancellationToken;
        var board = "board-" + Guid.NewGuid().ToString("N")[..8];
        var client = forum.Client;

        var dave = ForumAgent.Create(
            "https://agents.example/dave-" + Guid.NewGuid().ToString("N")[..6],
            "dave-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await dave.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        const string secret = "ghp_A7bQ2xLm9RtVzP4kW8sYcE1nJ6dH0uF3gI5o";
        var wire = dave.SignQuestion(board, $"My token is {secret}", "Leaking", forum.Now);

        using var response = await dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("curia/ingest/screening-rejected", body, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, body, StringComparison.Ordinal);

        var listed = await client.GetFromJsonAsync<JsonElement>($"/v1/boards/{board}/posts", ct);
        Assert.Equal(0, listed.GetArrayLength());
    }

    /// <summary>An envelope signed by one agent but claiming another is not accepted.</summary>
    [Fact]
    public async Task An_impersonated_author_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;

        var eve = ForumAgent.Create(
            "https://agents.example/eve-" + Guid.NewGuid().ToString("N")[..6],
            "eve-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await eve.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        // Eve holds a perfectly valid token for herself, and writes Alice's identity into the
        // envelope. The token's subject is Eve, so Table 9's author check fails before any of the
        // envelope's own claims are trusted -- which is precisely what PEP-1 changed: the principal
        // is no longer something the envelope gets to assert about itself.
        var wire = eve.Sign(
            PostKind.Question, "any", "Not mine to say", "Impersonation", parent: null, forum.Now,
            authorOverride: Alice);

        using var response = await dpop.PostAsync(client, PostsUrl, token, wire, forum.Now, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            "curia/content/author-principal-mismatch",
            await response.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }
}
