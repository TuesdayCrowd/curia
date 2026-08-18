using System.Buffers.Text;
using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curia.Client;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Client.Tests;

/// <summary>
/// The token and proof flow, observed from the server's side of the wire.
///
/// <para>These assert on the bytes a Forum would receive, not on the client agreeing with itself.
/// The nonce exchange in particular is a thing a client either handles or fails intermittently
/// against, so it is checked against a handler that behaves the way RFC 9449 §8 says a server
/// does: refuse the first write, name a nonce, accept the retry.</para>
/// </summary>
public sealed class DpopFlowTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("curia-dpop-tests-").FullName;
    private readonly ProfileStore _store;
    private readonly EnrolledAgent _agent;

    private static readonly Uri Forum = new("http://forum.test");

    public DpopFlowTests()
    {
        _store = new ProfileStore(_root);
        Assert.True(_store.Create("alice", "https://agents.example/alice", "alice-1", Forum)
            .TryGetValue(out var agent, out _));
        _agent = agent!;
    }

    public void Dispose()
    {
        _agent.Dispose();
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TheTokenRequestCarriesAPrivateKeyJwtAndAProofOverTheRealTokenUrl()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        Assert.True((await session.AccessTokenAsync(CancellationToken.None))
            .TryGetValue(out var token, out var refusal), refusal?.Error.Type);
        Assert.Equal("test-access-token", token);

        var request = Assert.Single(handler.Requests);
        Assert.Equal("/oauth/token", request.Path);

        var form = ParseForm(request.Body);
        Assert.Equal("https://agents.example/alice", form["client_id"]);
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);

        var assertion = Claims(form["client_assertion"]);
        Assert.Equal("https://agents.example/alice", assertion.GetProperty("iss").GetString());
        Assert.Equal("https://agents.example/alice", assertion.GetProperty("sub").GetString());

        // aud must be the URL the client actually dialled. The Forum builds it from the request it
        // received, so an aud derived from the RFC 8414 issuer identifier (which defaults to
        // https://forum.local on a locally-run Forum) fails with curia/authn/audience-mismatch.
        Assert.Equal("http://forum.test/oauth/token", assertion.GetProperty("aud").GetString());

        var proof = Header(request.Dpop!);
        Assert.Equal("dpop+jwt", proof.GetProperty("typ").GetString());     // errata A17
        Assert.Equal("EC", proof.GetProperty("jwk").GetProperty("kty").GetString());

        var proofClaims = Claims(request.Dpop!);
        Assert.Equal("POST", proofClaims.GetProperty("htm").GetString());
        Assert.Equal("http://forum.test/oauth/token", proofClaims.GetProperty("htu").GetString());

        // A token request's proof carries no ath: there is no token yet to bind to.
        Assert.False(proofClaims.TryGetProperty("ath", out _));
    }

    [Fact]
    public async Task TheProofIsSignedWithTheUnregisteredDpopKeyAndTheAssertionWithTheRegisteredOne()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        await session.AccessTokenAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        var jwk = Header(request.Dpop!).GetProperty("jwk");

        var dpopParameters = _agent.DpopKey.ExportParameters(includePrivateParameters: false);
        var signingParameters = _agent.SigningKey.ExportParameters(includePrivateParameters: false);

        Assert.Equal(Base64Url.EncodeToString(dpopParameters.Q.X!), jwk.GetProperty("x").GetString());
        Assert.NotEqual(Base64Url.EncodeToString(signingParameters.Q.X!), jwk.GetProperty("x").GetString());

        // And the assertion really is signed by the registered key: verify it as the Forum would.
        var form = ParseForm(request.Body);
        Assert.True(VerifyJwt(form["client_assertion"], _agent.SigningKey));
        Assert.True(VerifyJwt(request.Dpop!, _agent.DpopKey));
    }

    [Fact]
    public async Task TheNonceChallengeIsRetriedWithAFreshProofRatherThanTheOldOne()
    {
        using var handler = new ScriptedHandler { ChallengeFirstPost = true };
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        var draft = new PostDraft
        {
            Kind = PostKind.Question, Board = "b", Title = "t", Body = "body",
        };

        Assert.True(SubmissionBuilder.Build(_agent, draft, DateTimeOffset.UtcNow)
            .TryGetValue(out var signed, out _));

        Assert.True((await session.SubmitAsync(signed!.Wire, CancellationToken.None))
            .TryGetValue(out var receipt, out var refusal), refusal?.Error.Type);
        Assert.Equal("01TESTPOSTID0000000000000A", receipt.PostId);

        var posts = handler.Requests.Where(r => r.Path == "/v1/posts").ToImmutableArray();
        Assert.Equal(2, posts.Length);

        var first = Claims(posts[0].Dpop!);
        var second = Claims(posts[1].Dpop!);

        Assert.False(first.TryGetProperty("nonce", out _));
        Assert.Equal("server-nonce-1", second.GetProperty("nonce").GetString());

        // jti is burned in a replay cache on first sight, so the retry has to be a new proof.
        // Resending the challenged one is refused as a replay, which looks exactly like a nonce
        // that did not take.
        Assert.NotEqual(first.GetProperty("jti").GetString(), second.GetProperty("jti").GetString());

        // Both carry ath over the token they were issued for.
        var expectedAth = Base64Url.EncodeToString(
            SHA256.HashData(Encoding.ASCII.GetBytes("test-access-token")));
        Assert.Equal(expectedAth, first.GetProperty("ath").GetString());
        Assert.Equal(expectedAth, second.GetProperty("ath").GetString());

        Assert.Equal("DPoP", posts[0].AuthorizationScheme);
        Assert.Equal("test-access-token", posts[0].AuthorizationParameter);
    }

    [Fact]
    public async Task TheAccessTokenIsCachedRatherThanMintedPerCommand()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        await session.AccessTokenAsync(CancellationToken.None);
        await session.AccessTokenAsync(CancellationToken.None);

        Assert.Single(handler.Requests, r => r.Path == "/oauth/token");
    }

    // ---- helpers -------------------------------------------------------------------------

    private static Dictionary<string, string> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')), StringComparer.Ordinal);

    private static JsonElement Header(string jwt) => Segment(jwt, 0);

    private static JsonElement Claims(string jwt) => Segment(jwt, 1);

    private static JsonElement Segment(string jwt, int index)
    {
        var raw = Base64Url.DecodeFromChars(jwt.Split('.')[index]);
        return JsonDocument.Parse(raw).RootElement.Clone();
    }

    private static bool VerifyJwt(string jwt, ECDsa key)
    {
        var parts = jwt.Split('.');
        return key.VerifyData(
            Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
            Base64Url.DecodeFromChars(parts[2]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private sealed record CapturedRequest(
        string Path, string Body, string? Dpop, string? AuthorizationScheme, string? AuthorizationParameter);

    /// <summary>A Forum that answers the way RFC 9449 §8 says one does.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private int _posts;

        internal List<CapturedRequest> Requests { get; } = [];

        internal bool ChallengeFirstPost { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new CapturedRequest(
                path,
                body,
                request.Headers.TryGetValues("DPoP", out var proofs) ? proofs.First() : null,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));

            if (path == "/oauth/token")
                return Json(HttpStatusCode.OK,
                    """{"access_token":"test-access-token","token_type":"DPoP","expires_in":300,"scope":"question:create"}""");

            if (path == "/v1/posts" && ChallengeFirstPost && _posts++ == 0)
            {
                var challenge = Json(HttpStatusCode.Unauthorized,
                    """{"type":"curia/authn/nonce-missing","title":"DPoP proof carries no nonce","detail":null}""");
                challenge.Headers.TryAddWithoutValidation("DPoP-Nonce", "server-nonce-1");
                challenge.Headers.TryAddWithoutValidation("WWW-Authenticate", "DPoP error=\"use_dpop_nonce\"");
                return challenge;
            }

            return Json(HttpStatusCode.Created,
                """{"post_id":"01TESTPOSTID0000000000000A","digest":"abc","server_ts":"2026-08-16T12:00:00.0000000+00:00","risk_flags":[]}""");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
