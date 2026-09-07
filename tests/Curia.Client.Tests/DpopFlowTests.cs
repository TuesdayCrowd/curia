using System.Buffers.Text;
using System.Collections.Immutable;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Curia.Client;
using Curia.Domain.Content;
using Curia.Domain.Serving;
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
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
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
            // PathAndQuery, not AbsolutePath: the query string is where a search puts its filters,
            // and an assertion about them cannot see a field the handler discarded.
            var target = request.RequestUri!.PathAndQuery;
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new CapturedRequest(
                target,
                body,
                request.Headers.TryGetValues("DPoP", out var proofs) ? proofs.First() : null,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));

            if (path == "/oauth/token")
                return Json(HttpStatusCode.OK,
                    """{"access_token":"test-access-token","token_type":"DPoP","expires_in":300,"scope":"question:create"}""");

            if (path == "/v1/inbox")
                return Json(HttpStatusCode.OK,
                    """
                    {"results":[{"provenance":{"content_type":"agent-authored/untrusted","warning":"w",
                    "author":"https://agents.example/other","owner_verified":true,"signature_valid":true,
                    "verification_level":"V0","risk_flags":[],"marking":"None","marking_token":null,
                    "marking_caveat":null,"reader_contract":"http://forum.test/c"},
                    "post_id":"01TESTPOSTID0000000000000A","board":"b","kind":"question","parent":null,
                    "server_ts":"2026-08-16T12:00:00.0000000+00:00","digest":"sha-256:abc",
                    "canonical":"{}","signature":"sig","rendered":"r","accepted":false}],
                    "open_before_exclusions":3,"excluded_as_own":1,"excluded_as_already_answered":1}
                    """.ReplaceLineEndings(string.Empty));

            if (path == "/v1/search")
                return Json(HttpStatusCode.OK,
                    """
                    {"results":[{"post":{"provenance":{"content_type":"agent-authored/untrusted",
                    "warning":"w","author":"https://agents.example/alice","owner_verified":true,
                    "signature_valid":true,"verification_level":"V0","risk_flags":[],"marking":"None",
                    "marking_token":null,"marking_caveat":null,"reader_contract":"http://forum.test/c"},
                    "post_id":"01TESTPOSTID0000000000000A","board":"b","kind":"question","parent":null,
                    "server_ts":"2026-08-16T12:00:00.0000000+00:00","digest":"sha-256:abc",
                    "canonical":"{}","signature":"sig","rendered":"r"},"score_micro":32787,
                    "why_ranked":{"lexical":{"rank":1,"title_matches":1,"body_matches":2,"tag_matches":0,"score":7},
                    "vector":{"rank":1,"cosine_bp":9100,"model":"hashed-ngram@1"},"k":60,"lexical_term_micro":16393,
                    "vector_term_micro":16393,"fused_micro":32787,"verification_level":"V0","verification_weight_bp":10000,
                    "score_micro":32787,"deferred_by_diversification":false,"not_computed":{"n_eff":"Phase 4"}}}],
                    "next_cursor":"Nzox","floor":{"surface":"rest-search","min_verification":"V0","source":"published",
                    "applies_to":["answer","finding"],"not_applicable_to":["question","comment","revision"]},
                    "model":"hashed-ngram@1","corpus_bound":9,"k":60,"candidate_depth":200,"min_cosine_bp":2000}
                    """.ReplaceLineEndings(string.Empty));

            if (path.EndsWith("/accept", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created,
                    """{"thread_root":"01TESTROOT000000000000001","post_id":"01TESTPOSTID0000000000000A","accepted_at":"2026-08-16T12:00:00.0000000+00:00"}""");

            if (path.EndsWith("/flags", StringComparison.Ordinal))
                return Json(HttpStatusCode.Created,
                    """{"post_id":"01TESTPOSTID0000000000000A","kind":"incorrect","raised_at":"2026-08-16T12:00:00.0000000+00:00"}""");

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

    /// <summary>
    /// R10.35 over the wire: the flag reaches <c>POST /v1/posts/{id}/flags</c> as a DPoP-bound
    /// request carrying the typed kind and the rationale.
    ///
    /// <para>Asserted from the server's side, like every other test here: the client agreeing with
    /// itself about what it meant to send is not evidence that a Forum would accept it.</para>
    /// </summary>
    [Fact]
    public async Task R10_35_AFlagIsSentAsADpopBoundPostToThePostsFlagsRoute()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        var raised = await session.FlagAsync(
            "01TESTPOSTID0000000000000A", "incorrect", "the JCS claim is wrong", CancellationToken.None);

        Assert.True(raised.TryGetValue(out var receipt, out var refusal), refusal?.Error.Type);
        Assert.Equal("incorrect", receipt!.Kind);

        var request = handler.Requests.Last();
        Assert.Equal("/v1/posts/01TESTPOSTID0000000000000A/flags", request.Path);
        Assert.Equal("DPoP", request.AuthorizationScheme);
        Assert.NotNull(request.Dpop);

        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("incorrect", body.RootElement.GetProperty("kind").GetString());
        Assert.Equal("the JCS claim is wrong", body.RootElement.GetProperty("rationale").GetString());
    }

    /// <summary>
    /// The post id is percent-encoded into the path. Table 9 types <c>author</c> as a URI and post
    /// ids are ULIDs today, but a client that interpolates an identifier into a URL without encoding
    /// it is one identifier-format change away from a path-traversal bug — and the JWKS route
    /// already had to be moved for the same reason.
    /// </summary>
    [Fact]
    public async Task AFlaggedPostIdIsPercentEncodedIntoThePath()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        await session.FlagAsync("a/b", "spam", "nested", CancellationToken.None);

        Assert.Equal("/v1/posts/a%2Fb/flags", handler.Requests.Last().Path);
    }

    /// <summary>
    /// R10.35 requires a rationale, and the client refuses locally rather than spending a round trip
    /// discovering that. The same argument the submission path makes about credential material: the
    /// refusal a client can make itself is the one that costs nothing.
    /// </summary>
    [Fact]
    public async Task R10_35_AFlagWithoutARationaleIsRefusedLocally()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        var raised = await session.FlagAsync("01TESTPOSTID0000000000000A", "spam", "  ", CancellationToken.None);

        Assert.False(raised.TryGetValue(out _, out var refusal));
        Assert.Equal("curia/moderation/rationale-required", refusal!.Error.Type);
        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/flags", StringComparison.Ordinal));
    }

    /// <summary>
    /// R10.35's seven types, checked before the request goes out. An unknown spelling is a local
    /// usage error: the Forum would refuse it too, and a round trip to be told what
    /// <c>FlagKinds.Parse</c> already knows is a round trip wasted.
    /// </summary>
    [Fact]
    public async Task R10_35_AnUnknownFlagKindIsRefusedLocally()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        var raised = await session.FlagAsync("01TESTPOSTID0000000000000A", "vibes", "no", CancellationToken.None);

        Assert.False(raised.TryGetValue(out _, out var refusal));
        Assert.Equal("curia/flag/unknown-kind", refusal!.Error.Type);
        Assert.DoesNotContain(handler.Requests, r => r.Path.EndsWith("/flags", StringComparison.Ordinal));
    }

    /// <summary>
    /// R9.4's lexical half over the wire: the query reaches <c>GET /v1/search</c> with its filters,
    /// and the ranked results come back inside their provenance envelopes.
    /// </summary>
    [Fact]
    public async Task R9_4_ASearchReachesTheSearchRouteAndReturnsRankedResults()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var client = new ForumClient(http, Forum);

        var found = await client.SearchAsync(
            new SearchRequest("jcs ordering") { Board = "canon", Tags = ["jcs", "nfc"], Limit = 10, WhyRanked = true },
            MarkingMode.None,
            CancellationToken.None);

        Assert.True(found.TryGetValue(out var page, out var refusal), refusal?.Error.Type);

        var hit = Assert.Single(page!.Results);
        Assert.Equal("01TESTPOSTID0000000000000A", hit.Post.PostId);
        Assert.Equal(32787, hit.ScoreMicro);
        Assert.Equal(1, hit.Why!.Lexical!.TitleMatches);
        Assert.Equal(1, hit.Why.Vector!.Rank);
        Assert.Equal("hashed-ngram@1", hit.Why.Vector.Model);
        Assert.Equal(60, hit.Why.K);
        Assert.Equal("Phase 4", hit.Why.NotComputed["n_eff"]);
        Assert.Equal(("V0", "published", "rest-search"), (page.Floor.MinVerification, page.Floor.Source, page.Floor.Surface));
        Assert.Equal(["answer", "finding"], page.Floor.AppliesTo);
        Assert.Equal("hashed-ngram@1", page.Model);
        Assert.Equal(9, page.CorpusBound);
        Assert.Equal("Nzox", page.NextCursor);

        var request = handler.Requests.Last();
        Assert.StartsWith("/v1/search", request.Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// Query values are percent-encoded, not concatenated. A term containing <c>&amp;</c> would
    /// otherwise silently become a second parameter, and the agent would be told about results for
    /// a query it did not run.
    /// </summary>
    [Fact]
    public async Task SearchTermsArePercentEncodedIntoTheQueryString()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var client = new ForumClient(http, Forum);

        await client.SearchAsync(new SearchRequest("a&limit=999 b"), MarkingMode.None, CancellationToken.None);

        var query = handler.Requests.Last().Path;
        Assert.Contains("q=a%26limit%3D999%20b", query, StringComparison.Ordinal);
    }

    /// <summary>
    /// R9.26: the reference client sends the kind criterion as a set. The adapter that will drive
    /// this library must not hand-roll a query string to reach a member the client cannot express —
    /// two surfaces building the same URL differently is how they come to disagree about what a
    /// request means.
    /// </summary>
    [Fact]
    public async Task R9_26_TheKindCriterionIsSentAsASet()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var client = new ForumClient(http, Forum);

        // The set G12's compensation needs: the gradable kinds, in one request.
        await client.SearchAsync(
            new SearchRequest("jcs") { Kinds = ["answer", "finding"] }, MarkingMode.None, CancellationToken.None);
        Assert.Contains("kind=answer%2Cfinding", handler.Requests.Last().Path, StringComparison.Ordinal);

        // One kind is a set of one, not a different member.
        await client.SearchAsync(
            new SearchRequest("jcs") { Kinds = ["answer"] }, MarkingMode.None, CancellationToken.None);
        Assert.Contains("kind=answer", handler.Requests.Last().Path, StringComparison.Ordinal);

        // Absent means absent: an empty set sends no member rather than an empty one, because
        // `kind=` is a value the Forum would have to interpret and R9.25 forbids it inventing one.
        await client.SearchAsync(new SearchRequest("jcs"), MarkingMode.None, CancellationToken.None);
        Assert.DoesNotContain("kind=", handler.Requests.Last().Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// R9.8's breakdown is requested explicitly, so a client that does not ask does not receive it
    /// and cannot come to depend on it.
    /// </summary>
    [Fact]
    public async Task R9_8_TheBreakdownIsRequestedOnlyWhenAskedFor()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var client = new ForumClient(http, Forum);

        await client.SearchAsync(new SearchRequest("jcs"), MarkingMode.None, CancellationToken.None);
        Assert.DoesNotContain("why=true", handler.Requests.Last().Path, StringComparison.Ordinal);

        await client.SearchAsync(
            new SearchRequest("jcs") { WhyRanked = true }, MarkingMode.None, CancellationToken.None);
        Assert.Contains("why=true", handler.Requests.Last().Path, StringComparison.Ordinal);
    }

    /// <summary>
    /// Table 10's <c>answer</c>/<c>accept</c> over the wire: a DPoP-bound POST to the answer's
    /// <c>/accept</c> sub-resource, with the receipt naming the thread it resolved.
    /// </summary>
    [Fact]
    public async Task Table10_AcceptingAnAnswerPostsToTheAnswersAcceptRoute()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        var accepted = await session.AcceptAsync("01TESTPOSTID0000000000000A", CancellationToken.None);

        Assert.True(accepted.TryGetValue(out var receipt, out var refusal), refusal?.Error.Type);
        Assert.Equal("01TESTROOT000000000000001", receipt!.ThreadRoot);

        var request = handler.Requests.Last();
        Assert.Equal("/v1/posts/01TESTPOSTID0000000000000A/accept", request.Path);
        Assert.Equal("DPoP", request.AuthorizationScheme);
        Assert.NotNull(request.Dpop);
    }

    /// <summary>The answer id is percent-encoded, for the reason the flag path records.</summary>
    [Fact]
    public async Task AnAcceptedAnswerIdIsPercentEncodedIntoThePath()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        await session.AcceptAsync("a/b", CancellationToken.None);

        Assert.Equal("/v1/posts/a%2Fb/accept", handler.Requests.Last().Path);
    }

    /// <summary>
    /// The inbox is an authenticated read: a DPoP-bound GET carrying the agent's filters as query
    /// parameters, and returning what it could not know for itself — what it has already done.
    /// </summary>
    [Fact]
    public async Task TheInboxIsADpopBoundGetCarryingTheAgentsFilters()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        var read = await session.InboxAsync(
            new InboxRequest { Board = "canon", Tags = ["jcs"] }, MarkingMode.None, CancellationToken.None);

        Assert.True(read.TryGetValue(out var inbox, out var refusal), refusal?.Error.Type);
        Assert.Single(inbox!.Results);
        Assert.Equal(3, inbox.OpenBeforeExclusions);
        Assert.Equal(1, inbox.ExcludedAsOwn);
        Assert.Equal(1, inbox.ExcludedAsAlreadyAnswered);

        var request = handler.Requests.Last();
        Assert.StartsWith("/v1/inbox?", request.Path, StringComparison.Ordinal);
        Assert.Contains("board=canon", request.Path, StringComparison.Ordinal);
        Assert.Contains("tags=jcs", request.Path, StringComparison.Ordinal);
        Assert.Equal("DPoP", request.AuthorizationScheme);
    }

    /// <summary>
    /// <b>RFC 9449 §4.2: <c>htu</c> is the target URI without query and fragment.</b>
    ///
    /// <para>The inbox is the first authenticated request in this system that carries query
    /// parameters, so it is the first place this can be got wrong — and signing over the full URL
    /// produces a proof that never matches, on every request, for a reason no error message
    /// explains. Asserted against the decoded proof rather than against the client agreeing with
    /// itself.</para>
    /// </summary>
    [Fact]
    public async Task RFC9449_TheProofsHtuExcludesTheQueryString()
    {
        using var handler = new ScriptedHandler();
        using var http = new HttpClient(handler) { BaseAddress = Forum };
        var session = new ForumSession(new ForumClient(http, Forum), _agent, _store, TimeProvider.System);

        await session.InboxAsync(
            new InboxRequest { Board = "canon", Tags = ["jcs"] }, MarkingMode.None, CancellationToken.None);

        var proof = handler.Requests.Last().Dpop;
        Assert.NotNull(proof);

        var claims = Claims(proof!);
        Assert.Equal("http://forum.test/v1/inbox", claims.GetProperty("htu").GetString());
        Assert.Equal("GET", claims.GetProperty("htm").GetString());
    }
}
