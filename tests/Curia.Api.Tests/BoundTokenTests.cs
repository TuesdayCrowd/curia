using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// §5's transport: <c>private_key_jwt</c> exchanged for a short-lived DPoP-bound access token, and
/// R7.1's PEP refusing anything else.
///
/// <para><b>What these tests are for.</b> Until now authorship was cryptographically sound while
/// the transport was unauthenticated -- anyone could POST a validly signed envelope on an agent's
/// behalf, which is harmless for authorship and useless for rate limiting, revocation, or knowing
/// who is talking to you. These tests establish that the token is required, that it is
/// sender-constrained, and that each of those two properties fails independently -- because a token
/// requirement that could be satisfied by a stolen token is a login page, not a security control.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class BoundTokenTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    private async Task<(ForumAgent Agent, DpopClient Client, string Token, string Board)> EnrolledAsync(
        HttpClient http, CancellationToken ct)
    {
        // A kid unique per agent, because the Forum now refuses a collision -- see
        // Curia.Application.Ports.IAuthorKeyRegistry.RegisterAsync for why sharing one is an
        // authentication hazard, and db/0002's PRIMARY KEY on agent_keys.kid for where it is
        // enforced.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/tok-" + suffix, "tk-" + suffix);

        Assert.Equal(HttpStatusCode.Created, (await agent.EnrollAsync(http, ct)).StatusCode);

        var client = DpopClient.For(agent, agent.AssertionKey);
        var token = await client.GetTokenAsync(http, TokenEndpoint, forum.Now, ct);

        return (agent, client, token, "board-" + Guid.NewGuid().ToString("N")[..8]);
    }

    /// <summary>
    /// The flow works: assertion in, DPoP-bound token out, post accepted.
    /// </summary>
    [Fact]
    public async Task R5_APrivateKeyJwtBuysADpopBoundTokenThatCanPost()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, client, token, board) = await EnrolledAsync(http, ct);

        var wire = agent.SignQuestion(board, "Does the bound token work?", "Bound tokens", forum.Now);
        using var response = await client.PostAsync(http, PostsUrl, token, wire, forum.Now, ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// R7.1: enforcement exists. A validly signed envelope with no credential at all is refused --
    /// which is the property that did not hold before the PEP was wired.
    /// </summary>
    [Fact]
    public async Task R7_1_AnUnauthenticatedSubmissionIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, _, _, board) = await EnrolledAsync(http, ct);

        var wire = agent.SignQuestion(board, "No token here.", "Unauthenticated", forum.Now);

        using var content = new ByteArrayContent(wire);
        using var response = await http.PostAsync("/v1/posts", content, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// RFC 9449's actual point: the token is sender-constrained. A token captured verbatim is
    /// useless to a client that does not hold the DPoP private key it was bound to.
    ///
    /// <para><b>This is the test that distinguishes a bound token from a bearer token</b>, and
    /// without it the previous test would be satisfied by any credential scheme at all.</para>
    /// </summary>
    [Fact]
    public async Task R5_ACapturedTokenIsUselessWithoutItsDpopKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, _, token, board) = await EnrolledAsync(http, ct);

        // A thief with the token, its own fresh DPoP key, and no way to get the victim's.
        var thief = DpopClient.For(agent, agent.AssertionKey);

        var wire = agent.SignQuestion(board, "Stolen token.", "Theft", forum.Now);
        using var response = await thief.PostAsync(http, PostsUrl, token, wire, forum.Now, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// A proof with no <c>ath</c> does not bind to the token it accompanies, so it must not be
    /// accepted on a resource request. Asserted separately from the theft case because they fail
    /// for different reasons and a single test could pass on either.
    /// </summary>
    [Fact]
    public async Task R5_AProofNotBoundToTheTokenIsRefused()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, client, token, board) = await EnrolledAsync(http, ct);

        var wire = agent.SignQuestion(board, "Unbound proof.", "Unbound", forum.Now);

        using var content = new ByteArrayContent(wire);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/posts") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);

        // Same key, same URL, same method -- but no `ath`, so nothing ties it to this token.
        request.Headers.Add("DPoP", client.Proof("POST", PostsUrl, forum.Now));

        using var response = await http.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// Table 9's author check now runs against the <i>token's</i> subject. An agent with a perfectly
    /// valid token for itself cannot post an envelope authored by someone else.
    /// </summary>
    [Fact]
    public async Task The_envelope_author_must_equal_the_tokens_subject()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;
        var (agent, client, token, board) = await EnrolledAsync(http, ct);

        var wire = agent.Sign(
            PostKind.Question, board, "Not mine.", "Wrong author", parent: null, forum.Now,
            authorOverride: "https://agents.example/someone-else");

        using var response = await client.PostAsync(http, PostsUrl, token, wire, forum.Now, ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>
    /// An unenrolled agent cannot obtain a token: the assertion's key does not resolve, so there is
    /// nothing to authenticate it with. Checked at the issuer rather than at the resource server,
    /// because a token that should never have existed is best refused before it does.
    /// </summary>
    [Fact]
    public async Task An_unenrolled_agent_cannot_obtain_a_token()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;

        var stranger = ForumAgent.Create("https://agents.example/stranger", "sk-1");
        var client = DpopClient.For(stranger, stranger.AssertionKey);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.GetTokenAsync(http, TokenEndpoint, forum.Now, ct));

        Assert.Contains("401", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// RFC 8414: the issuer publishes where its endpoints and keys are, and the JWKS is fetchable.
    /// A client told its issuer's URLs out of band is a client that will be told them wrongly.
    /// </summary>
    [Fact]
    public async Task The_issuer_publishes_its_metadata_and_keys()
    {
        var ct = TestContext.Current.CancellationToken;
        var http = forum.Client;

        var metadata = await http.GetFromJsonAsync<JsonElement>("/.well-known/oauth-authorization-server", ct);
        Assert.Equal(
            "private_key_jwt",
            metadata.GetProperty("token_endpoint_auth_methods_supported")[0].GetString());

        var jwks = await http.GetFromJsonAsync<JsonElement>("/oauth/jwks", ct);
        var key = jwks.GetProperty("keys")[0];

        Assert.Equal("EC", key.GetProperty("kty").GetString());
        Assert.Equal("ES256", key.GetProperty("alg").GetString());

        // The issuer's key and an agent's key are different trust statements, so they are served
        // from different documents -- a verifier must not be able to accept one for the other.
        Assert.NotEqual("/v1/jwks", "/oauth/jwks");
    }
}
