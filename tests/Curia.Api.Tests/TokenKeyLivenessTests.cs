using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.Api.Issuer;
using Curia.AuthN.Ports;
using Curia.Domain.Primitives;
using Curia.Domain.Primitives.Boundary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>An <see cref="IAgentKeyLiveness"/> whose every read fails, as a log that cannot be read does (R5.22). It stands in for both an unreadable log and a log that will not fold, because <c>StatusAsync</c> answers both with the same type; the fold case itself is pinned in <c>Curia.Application.Tests</c> and is not exercised over HTTP.</summary>
internal sealed class FailingKeyLiveness : IAgentKeyLiveness
{
    public Task<Result<KeyStatus>> StatusAsync(AgentId agent, Kid kid, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<KeyStatus>.Fail(
            new Error("curia/log/unreadable", "The event log could not be read", "curia/events/unavailable")));
}

/// <summary>The Forum with its liveness read replaced by one that fails.</summary>
public sealed class UnreadableLivenessForumFixture : ForumFixture
{
    protected override void ConfigureStore(IServiceCollection services)
    {
        services.RemoveAll<IAgentKeyLiveness>();
        services.AddSingleton<IAgentKeyLiveness>(new FailingKeyLiveness());
    }
}

/// <summary>
/// R5.22 over HTTP: a token names the key its assertion was signed with (<c>curia_akid</c>), and every
/// authenticated request reads, uncached, that the key is live. A token issued on a key that has since
/// retired is refused 401 <c>curia/authn/token-key-not-live</c>; one naming no key,
/// <c>curia/authn/token-names-no-key</c>; one whose liveness read failed is refused with that read's error
/// and never served.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class TokenKeyLivenessTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static string Unique(string stem) => $"https://agents.example/{stem}-{Guid.NewGuid().ToString("N")[..8]}";

    internal static byte[] FlagBody() =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { kind = "spam", rationale = "this is spam" }));

    internal static readonly byte[] AcceptBody = Encoding.UTF8.GetBytes("{}");

    /// <summary>A question another agent posted, so a flag or an acceptance has a post to name.</summary>
    internal static async Task<string> PostedQuestionAsync(ForumFixture host, HttpClient client, CancellationToken ct)
    {
        var author = ForumAgent.Create(Unique("liveness-author"), "author-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await author.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, host.Now, ct);
        var wire = author.SignQuestion("board-" + Guid.NewGuid().ToString("N")[..8], "What does R5.22 read?", "Key liveness", host.Now);
        using var response = await dpop.PostAsync(client, "http://localhost/v1/posts", token, wire, host.Now, ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return json.RootElement.GetProperty("post_id").GetString()!;
    }

    private static Task<HttpResponseMessage> FlagAsync(
        ForumFixture host, HttpClient client, DpopClient dpop, string token, string postId, CancellationToken ct) =>
        dpop.PostAsync(client, $"http://localhost/v1/posts/{postId}/flags", token, FlagBody(), host.Now, ct, contentType: "application/json");

    private static Task<HttpResponseMessage> AcceptAsync(
        ForumFixture host, HttpClient client, DpopClient dpop, string token, string postId, CancellationToken ct) =>
        dpop.PostAsync(client, $"http://localhost/v1/posts/{postId}/accept", token, AcceptBody, host.Now, ct, contentType: "application/json");

    /// <summary>
    /// An agent with a token issued on its enrolled key, then rotated to a second key whose own token is
    /// also issued, then the first key retired by its own signature (R4.39). Returns both tokens.
    /// </summary>
    private async Task<(DpopClient First, string FirstToken, DpopClient Second, string SecondToken)> RetiredFirstKeyAsync(
        HttpClient client, CancellationToken ct)
    {
        var first = ForumAgent.Create(Unique("liveness"), "first-" + Guid.NewGuid().ToString("N")[..8]);
        var (firstDpop, firstToken) = await first.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);

        var second = ForumAgent.Create(first.AgentId, "second-" + Guid.NewGuid().ToString("N")[..8]);
        var next = new KeyActRequests.KeyPair(second.Kid, second.AssertionKey);
        var prev = await KeyActRequests.LatestLifecycleAsync(client, first.AgentId, ct);
        using (var rotated = await firstDpop.PostAsync(
            client, KeyActRequests.RotateUrl(first.AgentId), firstToken, KeyActRequests.RotationBody(first, next, prev),
            forum.Now, ct, contentType: "application/json"))
        {
            Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);
        }

        var secondDpop = DpopClient.For(second, second.AssertionKey);
        var secondToken = await secondDpop.GetTokenAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);

        var victim = new KeyActRequests.KeyPair(first.Kid, first.AssertionKey);
        using (var retired = await firstDpop.PostAsync(
            client, KeyActRequests.RetireUrl(first.AgentId, first.Kid), firstToken,
            KeyActRequests.RetirementBody(first.AgentId, victim, await KeyActRequests.LatestLifecycleAsync(client, first.AgentId, ct)),
            forum.Now, ct, contentType: "application/json"))
        {
            Assert.Equal(HttpStatusCode.Created, retired.StatusCode);
        }

        return (firstDpop, firstToken, secondDpop, secondToken);
    }

    [Fact]
    public async Task R5_22_AFlagRaisedWithATokenIssuedBeforeItsKeyRetiredIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var postId = await PostedQuestionAsync(forum, client, ct);
        var (dpop, token, _, _) = await RetiredFirstKeyAsync(client, ct);

        using var response = await FlagAsync(forum, client, dpop, token, postId, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("curia/authn/token-key-not-live", (await KeyActRequests.ProblemOfAsync(response, ct)).Type);
    }

    [Fact]
    public async Task R5_22_AcceptanceLikewise()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var postId = await PostedQuestionAsync(forum, client, ct);
        var (dpop, token, _, _) = await RetiredFirstKeyAsync(client, ct);

        using var response = await AcceptAsync(forum, client, dpop, token, postId, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("curia/authn/token-key-not-live", (await KeyActRequests.ProblemOfAsync(response, ct)).Type);
    }

    [Fact]
    public async Task R5_22_AnotherKeysTokenOfTheSameAgentStillWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var postId = await PostedQuestionAsync(forum, client, ct);
        var (_, _, dpop, token) = await RetiredFirstKeyAsync(client, ct);

        using var response = await FlagAsync(forum, client, dpop, token, postId, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task R5_22_ATokenNamingNoKeyIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var postId = await PostedQuestionAsync(forum, client, ct);
        var agent = ForumAgent.Create(Unique("liveness-bare"), "bare-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, issued) = await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);
        var token = WithoutAssertionKid(issued, forum.IssuerSigningKeyPem);

        using var response = await FlagAsync(forum, client, dpop, token, postId, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("curia/authn/token-names-no-key", (await KeyActRequests.ProblemOfAsync(response, ct)).Type);
    }

    /// <summary>The token as the issuer's own key would sign it had Mint not written <c>curia_akid</c>.</summary>
    private static string WithoutAssertionKid(string token, string issuerPem)
    {
        var parts = token.Split('.');
        var payload = JsonNode.Parse(Base64Url.DecodeFromChars(parts[1]))!.AsObject();
        Assert.True(payload.Remove("curia_akid"), "the issued token carried no curia_akid to remove");

        var signingInput = parts[0] + "." + Base64Url.EncodeToString(Encoding.UTF8.GetBytes(payload.ToJsonString()));
        using var key = IssuerSigningKey.FromPem(issuerPem);
        return signingInput + "." + Base64Url.EncodeToString(key.Sign(Encoding.ASCII.GetBytes(signingInput)));
    }

    [Fact]
    public async Task R5_22_AnIssuedTokenNamesTheKeyItsAssertionWasSignedWith()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = ForumAgent.Create(Unique("liveness-claim"), "claim-" + Guid.NewGuid().ToString("N")[..8]);
        var (_, token) = await agent.AuthenticateAsync(forum.Client, KeyActRequests.TokenEndpoint, forum.Now, ct);

        var payload = JsonNode.Parse(Base64Url.DecodeFromChars(token.Split('.')[1]))!.AsObject();

        Assert.Equal(agent.Kid, (string?)payload["curia_akid"]);
    }
}

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class TokenKeyLivenessUnreadableTests(UnreadableLivenessForumFixture forum) : IClassFixture<UnreadableLivenessForumFixture>
{
    [Fact]
    public async Task R5_22_ALivenessReadThatFailsRefusesTheRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var agent = ForumAgent.Create(KeyActRequests.Unique("liveness-unreadable"), "unreadable-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, KeyActRequests.TokenEndpoint, forum.Now, ct);

        using var response = await dpop.PostAsync(
            client, "http://localhost/v1/posts/any-post/flags", token, TokenKeyLivenessTests.FlagBody(), forum.Now, ct, contentType: "application/json");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("curia/log/unreadable", (await KeyActRequests.ProblemOfAsync(response, ct)).Type);
    }
}
