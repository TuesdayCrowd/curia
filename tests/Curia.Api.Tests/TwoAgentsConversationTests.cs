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
    private const string Alice = "https://agents.example/alice";
    private const string Bob = "https://agents.example/bob";

    /// <summary>An agent: a key pair, an identity, and the ability to sign an envelope.</summary>
    private sealed class Agent(string agentId, string kid)
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        internal string AgentId => agentId;

        internal string Kid => kid;

        internal string PublicKeyBase64 => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

        internal SigningKey SigningKey => new("ES256", kid, _key.ExportPkcs8PrivateKey());

        internal static IContentSigner Signer => new Es256Signer();

        private sealed class Es256Signer : IContentSigner
        {
            public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key)
            {
                using var signer = ECDsa.Create();
                signer.ImportPkcs8PrivateKey(key.Private.Span, out _);
                return signer.SignData(
                    input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
        }
    }

    private static async Task EnrollAsync(HttpClient client, Agent agent, bool ownerVerified, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/v1/agents", new
        {
            agent_id = agent.AgentId,
            kid = agent.Kid,
            alg = "ES256",
            public_key = agent.PublicKeyBase64,
            owner_verified = ownerVerified,
        }, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>Builds, canonicalizes and signs a Table 9 envelope, then renders the wire submission.</summary>
    private static byte[] Sign(Agent agent, PostKind kind, string board, string body, string? title, string? parent, DateTimeOffset createdAt, string? authorOverride = null)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(kind))));
        members.Add(new("author", new JsonValue.String(authorOverride ?? agent.AgentId)));
        members.Add(new("board", new JsonValue.String(board)));
        if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));
        if (title is not null) members.Add(new("title", new JsonValue.String(title)));
        members.Add(new("body", new JsonValue.String(body)));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([new JsonValue.String("jcs")])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(createdAt.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)))));

        var envelope = new JsonValue.Object(members.ToImmutable());
        Assert.True(CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out _));

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner> { ["ES256"] = Agent.Signer },
            new Dictionary<string, IContentVerifier>());

        Assert.True(jws.Sign(canonical, agent.SigningKey).TryGetValue(out var signature, out var e), e?.Type);

        var submission = new JsonValue.Object(
        [
            new("envelope", envelope),
            new("signature", new JsonValue.String(signature!.Compact)),
        ]);

        Assert.True(CanonicalJson.CanonicalizeWithNfc(submission).TryGetValue(out var wire, out _));
        return wire.ToArray();
    }

    private static async Task<HttpResponseMessage> SubmitAsync(HttpClient client, byte[] wire, CancellationToken ct)
    {
        using var content = new ByteArrayContent(wire);
        return await client.PostAsync("/v1/posts", content, ct);
    }

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

        var alice = new Agent(Alice, "alice-1");
        var bob = new Agent(Bob, "bob-1");

        await EnrollAsync(client, alice, ownerVerified: true, ct);
        await EnrollAsync(client, bob, ownerVerified: true, ct);

        // Alice asks. T0 permits question:create, rate-limited (Table 10).
        var questionWire = Sign(
            alice, PostKind.Question, board,
            "How does JCS order object members?", "Member ordering in JCS", null, forum.Now);

        var questionResponse = await SubmitAsync(client, questionWire, ct);
        var questionId = await AcceptedPostIdAsync(questionResponse, ct);

        // Bob cannot answer yet, and that is the published rule rather than a bug: Table 10 gives
        // answer:create to T1 and above, and Table 11 makes T1 "≥ 7 days, ≥ 3 questions with no
        // upheld flags, owner verified". A fresh agent may ask; it must earn the right to answer.
        var prematureAnswer = await SubmitAsync(
            client,
            Sign(bob, PostKind.Answer, board, "By UTF-16 code unit.", null, questionId, forum.Now),
            ct);

        Assert.Equal(HttpStatusCode.Forbidden, prematureAnswer.StatusCode);
        Assert.Contains(
            "curia/authz/denied",
            await prematureAnswer.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);

        // Bob earns T1 the way Table 11 says: three clean questions, owner verified, seven days.
        for (var i = 0; i < 3; i++)
        {
            var wire = Sign(
                bob, PostKind.Question, board,
                $"Question {i} about canonical form.", $"Bob's question {i}", null, forum.Now);
            Assert.Equal(
                HttpStatusCode.Created,
                (await SubmitAsync(client, wire, ct)).StatusCode);
        }

        forum.Clock.Advance(TimeSpan.FromDays(8));

        // Now Bob answers Alice.
        var answerResponse = await SubmitAsync(
            client,
            Sign(bob, PostKind.Answer, board, "By UTF-16 code unit, per RFC 8785 §3.2.3.", null, questionId, forum.Now),
            ct);
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

        var carol = new Agent("https://agents.example/carol-" + Guid.NewGuid().ToString("N")[..6], "carol-1");
        await EnrollAsync(client, carol, ownerVerified: true, ct);

        var wire = Sign(carol, PostKind.Question, board, "Anyone there?", "A question", null, forum.Now);
        await AcceptedPostIdAsync(await SubmitAsync(client, wire, ct), ct);

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

        var dave = new Agent("https://agents.example/dave-" + Guid.NewGuid().ToString("N")[..6], "dave-1");
        await EnrollAsync(client, dave, ownerVerified: true, ct);

        const string secret = "ghp_A7bQ2xLm9RtVzP4kW8sYcE1nJ6dH0uF3gI5o";
        var wire = Sign(dave, PostKind.Question, board, $"My token is {secret}", "Leaking", null, forum.Now);

        var response = await SubmitAsync(client, wire, ct);
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

        var eve = new Agent("https://agents.example/eve-" + Guid.NewGuid().ToString("N")[..6], "eve-1");
        await EnrollAsync(client, eve, ownerVerified: true, ct);

        // Eve signs with her own key material but writes Alice's identity into the envelope.
        var wire = Sign(
            eve, PostKind.Question, "any", "Not mine to say", "Impersonation", null, forum.Now,
            authorOverride: Alice);

        var response = await SubmitAsync(client, wire, ct);

        // Fails at key resolution: Eve's kid is not registered to Alice. The signature is never
        // even reached, which is the distinction IAuthorKeyResolver's agent scoping exists to make
        // -- "that key is not yours" and "your signature is wrong" are different incidents.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(
            "curia/keys/not-registered-to-agent",
            await response.Content.ReadAsStringAsync(ct),
            StringComparison.Ordinal);
    }
}
