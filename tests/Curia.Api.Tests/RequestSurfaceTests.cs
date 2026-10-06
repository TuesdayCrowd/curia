using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R11.33 (errata G17), as a gate: a request the Forum cannot read is a client's error, never the
/// server's, on every route, from a caller with no credential and from an enrolled agent.
///
/// <para><b>The scope is derived, not written.</b> Every route and every route parameter comes from
/// the host's <see cref="EndpointDataSource"/>, as R14.9's P22 gate derives its scope. Query
/// parameters are the one hand-written list, because two handlers read theirs from the request
/// rather than binding them; <see cref="EveryQueryParameterAHandlerBindsIsProbed"/> fails for a bound
/// one the list lacks, so a new parameter cannot go unprobed by being bound.</para>
///
/// <para><b>Why an enrolled agent too.</b> Without a credential, every route that needs one answers
/// 401 before it reads its path, its query or its body, so an anonymous sweep of those routes tests
/// authentication and nothing behind it. Enrollment costs nothing, so a request only an enrolled
/// agent can send is a request anyone can send, and it is the only one that reaches those handlers.
/// </para>
///
/// <para><b>What it found.</b> Probed by hand before it was written, the anonymous surface answered
/// 500 twice: <c>GET /v1/threads/{id}</c> for an id of white space alone, and <c>POST /oauth/token</c>
/// for a body that is not a form and for any form value holding U+0000, which ASP.NET's form reader
/// refuses with an exception (register D25). A probe of the finished stage found a third body there,
/// a multipart form cut off before its closing boundary, on which the reader throws
/// <see cref="IOException"/>. A 500 tells a caller to retry, and on a route anyone can reach it is
/// also a way to fill a log.</para>
///
/// <para><b>What it does not reach.</b> A path holding U+0000 is refused by the test host's client
/// before it is sent, so it is not probed here; the probe list records which values each route
/// received. Query parameters are sent to the routes that read, not to the writes: the one a write
/// reads, the batch's <c>marking</c>, is read by the same function the reads' is. Of the headers,
/// the two every route reads first are probed, <c>Authorization</c> and <c>DPoP</c>
/// (<see cref="R11_33_NoHeaderARouteCannotReadIsAnsweredAsAServerFault"/>); a header a single
/// handler reads, a conditional read's <c>If-None-Match</c>, is not swept. A JSON body's declared
/// charset is swept, the quoted form included (Task 8's review, I1): five of <see cref="Requests"/>'
/// bodies name one other than the bare token utf-8, and
/// <see cref="R11_33_AJsonBodyInACharsetOtherThanUtf8IsRefusedBeforeItIsBound"/> holds both sides.
/// A token request's declared charset is swept too, on the form and on a multipart part (the stage's
/// final gate, second round): four of the bodies declare UTF-7, which the form reader cannot decode,
/// and <see cref="R11_33_ATokenRequestInACharsetTheFormReaderCannotDecodeIsInvalidRequest"/> holds
/// both sides. Claims inside a JWT the agent signs are probed for their NumericDates by
/// <see cref="R11_33_NoNumericDateAnEnrolledAgentSignsIsAnsweredAsAServerFault"/>, and for every
/// string a store reads by <see cref="R11_33_NoStringAnEnrolledAgentSignsIsAnsweredAsAServerFault"/>
/// (a proof's or an assertion's <c>jti</c>, a write proof's <c>nonce</c>) and
/// <see cref="R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown"/> (an assertion's
/// <c>kid</c>, read before any signature); the jwk by the header fact.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class RequestSurfaceTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private const string TokenEndpoint = "http://localhost/oauth/token";
    private const string PostsUrl = "http://localhost/v1/posts";

    /// <summary>What a stranger can put in a path or a query: control, separator and noncharacter text, and bytes that are not UTF-8.</summary>
    private static readonly string[] Hostile =
    [
        "%00", "a%00b", "%0A", "%20", "%E2%80%A8", "%EF%BF%BE", "%ED%A0%80", "%FF", "%C0%80", "%F4%90%80%80",
    ];

    /// <summary>
    /// Every query parameter a Forum handler reads: those it binds (checked against the handlers by
    /// <see cref="EveryQueryParameterAHandlerBindsIsProbed"/>) and those search and the batch read from
    /// the request itself.
    /// </summary>
    private static readonly string[] QueryParameters =
    [
        "q", "board", "author", "kind", "tags", "cursor", "limit", "why", "min_verification", "marking",
        "agent", "tree_size", "from", "to",
    ];

    /// <summary>
    /// Every route, anonymous, with each hostile value in each route parameter and, for a read, in
    /// each query parameter; and every write with each of <see cref="Requests"/>' bodies. None
    /// answers a server fault, or a 4xx that is no problem document (Task 8's second review, I2).
    /// </summary>
    [Fact]
    public async Task R11_33_NoRequestACallerWithoutACredentialCanSendIsAnsweredAsAServerFault()
    {
        var ct = TestContext.Current.CancellationToken;
        var routes = Routes(forum);
        Assert.NotEmpty(routes);

        var faults = new List<string>();
        var sent = 0;

        foreach (var (method, pattern, parameters) in routes)
        {
            foreach (var target in Targets(pattern, parameters, method == "GET"))
            {
                foreach (var make in Requests(method, target))
                {
                    using var request = make();
                    HttpResponseMessage response;
                    try
                    {
                        response = await forum.Client.SendAsync(request, ct);
                    }
                    catch (InvalidOperationException refused) when (NotSent(refused))
                    {
                        continue;
                    }

                    using (response)
                    {
                        sent++;
                        var status = (int)response.StatusCode;
                        var body = await response.Content.ReadAsStringAsync(ct);
                        if (status >= 500)
                            faults.Add($"{status} {method} {request.RequestUri}");
                        else if (NotAProblem(target, status, body) is { } reason)
                            faults.Add($"{status} {method} {request.RequestUri}: {reason}: {body[..Math.Min(body.Length, 160)]}");
                    }
                }
            }
        }

        Assert.True(sent > routes.Count * Hostile.Length, $"only {sent} requests were sent over {routes.Count} routes; the sweep did not run");
        Assert.True(faults.Count == 0, "anonymous requests answered as a server fault, or a 4xx that is no problem document:\n" + string.Join('\n', faults));
    }

    /// <summary>
    /// The same requests from an enrolled agent: each carries its DPoP-bound token and a proof over
    /// the URL it was sent to, and a write's is sent again with the nonce the Forum asks for (R5.19).
    /// None answers a server fault, or a 4xx that is no problem document (Task 8's second review, I2),
    /// and none is stopped at authentication, or the handlers behind it were never reached and the
    /// first assertion would hold of nothing.
    /// </summary>
    [Fact]
    public async Task R11_33_NoRequestAnEnrolledAgentCanSendIsAnsweredAsAServerFault()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var routes = Routes(forum);
        var (dpop, token) = await EnrolledAtT1Async(client, ct);

        var faults = new List<string>();
        var unauthenticated = new List<string>();
        var sent = 0;

        foreach (var (method, pattern, parameters) in routes)
        {
            foreach (var target in Targets(pattern, parameters, method == "GET"))
            {
                // RFC 9449 §4.2: a proof's htu is the URL without its query.
                var htu = "http://localhost" + target.Split('?')[0];

                foreach (var make in Requests(method, target))
                {
                    using var response = await SendAsAgentAsync(client, make, dpop, token, method, htu, ct);
                    if (response is null) continue;

                    sent++;
                    var status = (int)response.StatusCode;
                    var body = await response.Content.ReadAsStringAsync(ct);
                    if (status >= 500)
                        faults.Add($"{status} {method} {target}");
                    else if (response.StatusCode == HttpStatusCode.Unauthorized)
                        unauthenticated.Add($"{method} {target}");
                    else if (NotAProblem(target, status, body) is { } reason)
                        faults.Add($"{status} {method} {target}: {reason}: {body[..Math.Min(body.Length, 160)]}");
                }
            }
        }

        Assert.True(sent > routes.Count * Hostile.Length, $"only {sent} requests were sent over {routes.Count} routes; the sweep did not run");
        Assert.True(unauthenticated.Count == 0, "requests an enrolled agent sent were stopped at authentication, so nothing behind it was reached:\n" + string.Join('\n', unauthenticated));
        Assert.True(faults.Count == 0, "requests an enrolled agent sent answered as a server fault, or a 4xx that is no problem document:\n" + string.Join('\n', faults));
    }

    /// <summary>
    /// R11.33's headers. Every route is sent, with no credential, hostile <c>Authorization</c> and
    /// <c>DPoP</c> headers: a token that is not a JWS, one whose header is not an object, one naming
    /// a <c>kid</c> holding U+0000, a scheme the Forum does not know, a token four thousand bytes
    /// long, a proof with no token, and an <c>alg</c> or <c>kid</c>, and a proof's <c>jwk</c> member,
    /// holding an unpaired-surrogate escape (Task 11's fix review), and an access token whose
    /// <c>kid</c> is absent, empty, white space or a number (the stage's final gate, second round,
    /// which found each already answered 401, and keeps them here so it stays so). Then an enrolled agent obtains a token bound to a proof key
    /// that is no point on P-256 -- the token endpoint issues it, since it reads a proof's key without
    /// building it (register D29) -- and sends it to every route with a proof carrying that key. Every
    /// route behind authentication threw on it, a 500 any agent could cause (the register's
    /// "Observed during the enrollment stage"); none may answer 5xx, and some must have read the
    /// token, or nothing here reached the check.
    /// </summary>
    [Fact]
    public async Task R11_33_NoHeaderARouteCannotReadIsAnsweredAsAServerFault()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var routes = Routes(forum);

        (string? Authorization, string? Proof)[] hostile =
        [
            ("DPoP a.b.c", null),
            ("DPoP " + Segment("[1]") + ".e30.AA", null),
            ("DPoP " + Segment("{\"alg\":\"ES256\",\"typ\":\"at+jwt\",\"kid\":\"\\u0000\"}") + ".e30.AA", "a.b.c"),
            ("Bearer x", null),
            ("DPoP " + new string('A', 4096), null),
            (null, "a.b.c"),
            ("DPoP " + Segment("{\"alg\":\"\\ud800\",\"typ\":\"at+jwt\"}") + ".e30.AA", Segment("{\"typ\":\"dpop+jwt\",\"alg\":\"ES256\",\"jwk\":{\"kty\":\"\\ud800\"}}") + ".e30.AA"),
            ("DPoP " + Segment("{\"alg\":\"ES256\",\"typ\":\"at+jwt\",\"kid\":\"\\ud800\"}") + ".e30.AA", Segment("{\"typ\":\"dpop+jwt\",\"alg\":\"ES256\",\"jwk\":{\"kty\":\"OKP\",\"crv\":\"\\udc00\"}}") + ".e30.AA"),
            (null, Segment("{\"typ\":\"dpop+jwt\",\"alg\":\"\\ud800\"}") + ".e30.AA"),
            ("DPoP " + Segment("{\"alg\":\"ES256\",\"typ\":\"at+jwt\"}") + ".e30.AA", "a.b.c"),
            ("DPoP " + Segment("{\"alg\":\"ES256\",\"typ\":\"at+jwt\",\"kid\":\"\"}") + ".e30.AA", "a.b.c"),
            ("DPoP " + Segment("{\"alg\":\"ES256\",\"typ\":\"at+jwt\",\"kid\":\" \"}") + ".e30.AA", "a.b.c"),
            ("DPoP " + Segment("{\"alg\":\"ES256\",\"typ\":\"at+jwt\",\"kid\":1}") + ".e30.AA", "a.b.c"),
        ];

        var faults = new List<string>();
        var sent = 0;
        foreach (var (method, pattern, parameters) in routes)
        {
            var path = Plain(pattern, parameters);
            foreach (var (authorization, proof) in hostile)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (authorization is not null) request.Headers.TryAddWithoutValidation("Authorization", authorization);
                if (proof is not null) request.Headers.TryAddWithoutValidation("DPoP", proof);
                using var response = await client.SendAsync(request, ct);
                sent++;
                if ((int)response.StatusCode >= 500)
                    faults.Add($"{(int)response.StatusCode} {method} {path} (anonymous, Authorization {authorization ?? "(none)"}, DPoP {proof ?? "(none)"})");
            }
        }

        // The token endpoint reads its form before its proof, so a request with no form never reaches
        // the proof. Each hostile token and proof above is sent again as the proof on a token request
        // whose form is well formed (Task 11's review, I1; trap 26).
        foreach (var proof in hostile.Select(h => h.Authorization?.Split(' ', 2)[1]).Concat(hostile.Select(h => h.Proof)).OfType<string>().Distinct())
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = WellFormedTokenForm() };
            request.Headers.TryAddWithoutValidation("DPoP", proof);
            using var response = await client.SendAsync(request, ct);
            sent++;
            if ((int)response.StatusCode >= 500)
                faults.Add($"{(int)response.StatusCode} POST /oauth/token (anonymous, a well-formed form, DPoP {proof[..Math.Min(proof.Length, 64)]})");
        }

        // An agent's token bound to a proof key that is no point on the curve.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/off-curve-" + suffix, "off-curve-" + suffix);
        var (dpop, _) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = agent.AgentId,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = dpop.ClientAssertion(TokenEndpoint, forum.Now),
            ["scope"] = "question:create answer:create",
        });
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = form };
        tokenRequest.Headers.Add("DPoP", OffCurveProof("POST", TokenEndpoint, forum.Now));
        using var issued = await client.SendAsync(tokenRequest, ct);
        var body = await issued.Content.ReadAsStringAsync(ct);
        Assert.True(
            issued.StatusCode == HttpStatusCode.OK,
            $"the token endpoint did not issue a token bound to a key off the curve ({(int)issued.StatusCode} {body}); if it now verifies a proof's key (D29), this fact's second half needs its token minted another way");
        using var json = JsonDocument.Parse(body);
        var token = json.RootElement.GetProperty("access_token").GetString()!;

        // A write is sent an empty JSON object, so a route that binds its body reads the token too.
        var read = 0;
        foreach (var (method, pattern, parameters) in routes)
        {
            var path = Plain(pattern, parameters);
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method != "GET") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
            request.Headers.Add("DPoP", OffCurveProof(method, "http://localhost" + path, forum.Now, token));
            using var response = await client.SendAsync(request, ct);
            sent++;
            if ((int)response.StatusCode >= 500)
                faults.Add($"{(int)response.StatusCode} {method} {path} (a token bound to a proof key off the curve)");
            else if (response.StatusCode == HttpStatusCode.Unauthorized)
                read++;
        }

        Assert.True(sent > routes.Count * hostile.Length, $"only {sent} requests were sent over {routes.Count} routes; the sweep did not run");
        Assert.True(faults.Count == 0, "headers a route could not read answered as a server fault:\n" + string.Join('\n', faults));
        Assert.True(read > 0, "no route refused the token bound to a key off the curve, so none read it; a defect in this fact");
    }

    /// <summary>
    /// A DPoP proof whose <c>jwk</c> names P-256 with coordinates that are no point on it, carrying no
    /// valid signature: nothing that reads the key can verify it, and nothing may throw on it.
    /// </summary>
    private static string OffCurveProof(string method, string url, DateTimeOffset now, string? accessToken = null)
    {
        var header = new JsonObject
        {
            ["alg"] = "ES256",
            ["typ"] = "dpop+jwt",
            ["jwk"] = new JsonObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = Base64Url.EncodeToString(Enumerable.Repeat((byte)1, 32).ToArray()),
                ["y"] = Base64Url.EncodeToString(Enumerable.Repeat((byte)2, 32).ToArray()),
            },
        };
        var payload = new JsonObject
        {
            ["htm"] = method,
            ["htu"] = url,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (accessToken is not null)
            payload["ath"] = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken)));

        return Segment(header.ToJsonString()) + "." + Segment(payload.ToJsonString()) + "." + Base64Url.EncodeToString(new byte[64]);
    }

    private static string Segment(string json) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

    /// <summary>A token request's form that passes the endpoint's form checks, so what follows them is read.</summary>
    private static FormUrlEncodedContent WellFormedTokenForm() =>
        new(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = "x",
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = "a.b.c",
        });

    /// <summary>
    /// R11.33 for a token request's DPoP proof whose header is JSON but not an object (Task 11's
    /// review, I1). The token endpoint read the header's <c>jwk</c> without asking what the header
    /// was, and <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> throws
    /// <see cref="InvalidOperationException"/> on any other kind, so a request carrying no credential
    /// answered 500 before its assertion was read. The sweep had sent the same proof with no form, and
    /// the endpoint refused the form first (trap 26). The proof's key cannot be read, so it is
    /// <c>invalid_dpop_proof</c>.
    /// </summary>
    [Theory]
    [InlineData("[1]")]
    [InlineData("1")]
    [InlineData("\"s\"")]
    [InlineData("null")]
    public async Task R11_33_ATokenRequestsDpopProofWhoseHeaderIsNotAnObjectIsRefusedNotThrown(string header)
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = WellFormedTokenForm() };
        request.Headers.TryAddWithoutValidation("DPoP", Segment(header) + ".e30.AA");

        using var response = await forum.Client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode} {body[..Math.Min(body.Length, 240)]}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal("invalid_dpop_proof", json.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// R11.33 for a token request whose DPoP proof or client assertion holds an unpaired-surrogate
    /// escape (Task 11's fix review). <c>JsonDocument.Parse</c> accepts an escaped unpaired surrogate
    /// and <see cref="JsonElement.GetString()"/> throws <see cref="InvalidOperationException"/> on it,
    /// so a proof whose <c>jwk</c> held one in <c>kty</c>, <c>crv</c> or <c>x</c> answered 500 to a
    /// caller holding no credential: the endpoint read the proof through a parse of its own. The
    /// sweep had held string decodability fixed, sending U+0000, which decodes (trap 26). A proof's
    /// key that cannot be read is <c>invalid_dpop_proof</c>; an assertion that cannot be read, behind
    /// a proof whose key can, is <c>invalid_client</c>. The last four rows are an assertion whose header
    /// <c>kid</c> is absent, empty, white space or a number (the stage's final gate, second round):
    /// <c>PostgresAgentKeyStore</c> refuses a blank <c>kid</c> by throwing, and this answered 500 to
    /// anyone before any signature was checked.
    /// </summary>
    [Theory]
    [InlineData(false, "{\"typ\":\"dpop+jwt\",\"alg\":\"ES256\",\"jwk\":{\"kty\":\"\\ud800\"}}")]
    [InlineData(false, "{\"typ\":\"dpop+jwt\",\"alg\":\"ES256\",\"jwk\":{\"kty\":\"OKP\",\"crv\":\"\\udc00\"}}")]
    [InlineData(false, "{\"typ\":\"dpop+jwt\",\"alg\":\"ES256\",\"jwk\":{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"a\\ud800\",\"y\":\"AA\"}}")]
    [InlineData(true, "{\"alg\":\"\\ud800\",\"typ\":\"JWT\"}")]
    [InlineData(true, "{\"alg\":\"ES256\",\"typ\":\"JWT\"}")]
    [InlineData(true, "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\"\"}")]
    [InlineData(true, "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":\" \"}")]
    [InlineData(true, "{\"alg\":\"ES256\",\"typ\":\"JWT\",\"kid\":1}")]
    public async Task R11_33_ATokenRequestsProofOrAssertionItCannotReadIsRefusedNotThrown(bool inAssertion, string header)
    {
        var ct = TestContext.Current.CancellationToken;
        var form = inAssertion
            ? new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "x",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
                ["client_assertion"] = Segment(header) + ".e30.AA",
            })
            : WellFormedTokenForm();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = form };
        request.Headers.TryAddWithoutValidation("DPoP", inAssertion ? OffCurveProof("POST", TokenEndpoint, forum.Now) : Segment(header) + ".e30.AA");

        using var response = await forum.Client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        var expected = inAssertion ? HttpStatusCode.Unauthorized : HttpStatusCode.BadRequest;
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode} {body[..Math.Min(body.Length, 240)]}");
        using var json = JsonDocument.Parse(body);
        Assert.Equal(inAssertion ? "invalid_client" : "invalid_dpop_proof", json.RootElement.GetProperty("error").GetString());
    }

    /// <summary>
    /// R11.33's claims (Task 8's review, C1). The header fact varies a proof's <c>jwk</c> and no claim;
    /// a JWT the agent signs also carries <c>iat</c>, <c>exp</c> and <c>nbf</c>, parsed after its
    /// signature verifies, and a number <see cref="DateTimeOffset"/> cannot hold threw there. An
    /// enrolled agent sends the token endpoint client assertions its registered key signs with such
    /// an <c>iat</c> or <c>exp</c>, and then every route its valid token, with proofs its bound key
    /// signs whose <c>iat</c> is such a number. None may answer 5xx, no assertion may be honoured, and
    /// some route must answer 401, or no proof was read.
    /// </summary>
    [Fact]
    public async Task R11_33_NoNumericDateAnEnrolledAgentSignsIsAnsweredAsAServerFault()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var routes = Routes(forum);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/numeric-date-" + suffix, "numeric-date-" + suffix);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        var now = forum.Now.ToUnixTimeSeconds();
        (long Iat, long Exp)[] assertions =
        [
            (10000000000000, 10000000000000),
            (now, 10000000000000),
            (-100000000000, now + 60),
        ];

        var faults = new List<string>();
        foreach (var (iat, exp) in assertions)
        {
            var (status, body) = await dpop.RequestTokenAsync(
                client, TokenEndpoint, forum.Now, agent.AgentId, dpop.ClientAssertion(TokenEndpoint, iat, exp), ct);
            if ((int)status >= 500 || status == HttpStatusCode.OK)
                faults.Add($"{(int)status} POST /oauth/token (an assertion with iat {iat}, exp {exp}): {body[..Math.Min(body.Length, 160)]}");
        }

        long[] proofIats = [10000000000000, -100000000000];
        var sent = 0;
        var read = 0;
        foreach (var (method, pattern, parameters) in routes)
        {
            var path = Plain(pattern, parameters);
            foreach (var iat in proofIats)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), path);
                if (method != "GET") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
                request.Headers.Add("DPoP", dpop.Proof(method, "http://localhost" + path, iat, token));
                using var response = await client.SendAsync(request, ct);
                sent++;
                if ((int)response.StatusCode >= 500)
                    faults.Add($"{(int)response.StatusCode} {method} {path} (a proof with iat {iat})");
                else if (response.StatusCode == HttpStatusCode.Unauthorized)
                    read++;
            }
        }

        Assert.True(sent > routes.Count, $"only {sent} requests were sent over {routes.Count} routes; the sweep did not run");
        Assert.True(faults.Count == 0, "NumericDates an enrolled agent signed answered as a server fault, or were honoured:\n" + string.Join('\n', faults));
        Assert.True(read > 0, "no route refused a proof whose iat is out of range, so none read it; a defect in this fact");
    }

    /// <summary>
    /// The rows of <see cref="R11_33_NoStringAnEnrolledAgentSignsIsAnsweredAsAServerFault"/>: each
    /// <c>jti</c> no store can hold, on a proof to a read and to a write and on a client assertion at
    /// the token endpoint, and a proof <c>nonce</c> holding U+0000 on each write route that asks for one.
    /// </summary>
    public static TheoryData<string, string, string> UnreadableSignedStrings()
    {
        string[] jtis = ["absent", "empty", "spaces", "newline", "number", "object", "nul-inside", "ascii-257", "hex-3000"];
        var rows = new TheoryData<string, string, string>();
        foreach (var target in new[] { "GET /v1/inbox", "POST /v1/posts", "POST /oauth/token" })
            foreach (var jti in jtis)
                rows.Add(target, "jti", jti);
        foreach (var target in new[] { "POST /v1/posts", "POST /v1/posts/x/flags", "POST /v1/posts/x/accept" })
            rows.Add(target, "nonce", "nul-inside");
        return rows;
    }

    /// <summary>
    /// R11.33's strings (the stage's final gate, second round). The claims fact varied a signed
    /// claim's NumericDates and never its strings, so every <c>jti</c>, <c>kid</c> and <c>nonce</c> the
    /// enrolled sweep sent was one the reference client mints. An agent enrolled through the route
    /// signs, with its own bound keys, a proof or a client assertion whose <c>jti</c> is absent, not a
    /// string, empty, white space, holds U+0000, or runs past what an index row holds; each reached the
    /// replay cache, which threw, or Postgres, which threw 22021 or 54000, and answered 500. A write's
    /// proof <c>nonce</c> holding U+0000 reached the nonce store, and Postgres threw 22021. Each is a
    /// 401 now: a <c>jti</c> is <c>curia/authn/malformed</c> (at the token endpoint,
    /// <c>invalid_client</c> naming it), a nonce the Forum could not have issued is stale. A write is
    /// sent the nonce the Forum asks for first, so the <c>jti</c> reaches the replay cache on a tree
    /// that does not refuse it.
    /// </summary>
    [Theory]
    [MemberData(nameof(UnreadableSignedStrings))]
    public async Task R11_33_NoStringAnEnrolledAgentSignsIsAnsweredAsAServerFault(string target, string claim, string row)
    {
        ArgumentNullException.ThrowIfNull(target);
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/signed-string-" + suffix, "signed-string-" + suffix);
        var (dpop, token) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);

        void Edit(JsonObject payload)
        {
            payload.Remove(claim);
            JsonNode? value = row switch
            {
                "absent" => null,
                "empty" => "",
                "spaces" => "   ",
                "newline" => "\n",
                "number" => 1,
                "object" => new JsonObject { ["a"] = 1 },
                "nul-inside" => "a\0b",
                "ascii-257" => new string('a', 257),
                "hex-3000" => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(1500)),
                _ => throw new ArgumentOutOfRangeException(nameof(row), row, "no such row"),
            };
            if (row != "absent") payload[claim] = value;
        }

        var (method, path) = (target.Split(' ')[0], target.Split(' ')[1]);
        HttpStatusCode status;
        string body;
        if (path == "/oauth/token")
        {
            (status, body) = await dpop.RequestTokenAsync(
                client, TokenEndpoint, forum.Now, agent.AgentId, dpop.ClientAssertion(TokenEndpoint, forum.Now, Edit), ct);
            Assert.True(status == HttpStatusCode.Unauthorized, $"{(int)status} {body[..Math.Min(body.Length, 240)]}");
            using var answer = JsonDocument.Parse(body);
            Assert.Equal("invalid_client", answer.RootElement.GetProperty("error").GetString());
            Assert.Equal("curia/authn/malformed", answer.RootElement.GetProperty("detail").GetString());
            return;
        }

        var htu = "http://localhost" + path;
        HttpRequestMessage Make(string proof)
        {
            var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (method != "GET") request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
            request.Headers.Add("DPoP", proof);
            return request;
        }

        // A write asks for a nonce; the Forum's own is sent beside the row's jti, so the jti is read.
        string? nonce = null;
        if (method != "GET" && claim == "jti")
        {
            using var challenge = Make(dpop.Proof(method, htu, forum.Now, token));
            using var challenged = await client.SendAsync(challenge, ct);
            Assert.True(
                challenged.Headers.TryGetValues("DPoP-Nonce", out var nonces),
                $"{(int)challenged.StatusCode} {method} {path} asked for no nonce, so the row's jti is not what it tests");
            nonce = nonces.First();
        }

        using var sent = Make(dpop.Proof(method, htu, forum.Now, token, nonce, Edit));
        using var response = await client.SendAsync(sent, ct);
        status = response.StatusCode;
        body = await response.Content.ReadAsStringAsync(ct);

        Assert.True(status == HttpStatusCode.Unauthorized, $"{(int)status} {target} ({claim} {row}): {body[..Math.Min(body.Length, 240)]}");
        using var problem = JsonDocument.Parse(body);
        Assert.Equal(claim == "nonce" ? "curia/authn/nonce-stale" : "curia/authn/malformed", problem.RootElement.GetProperty("type").GetString());
    }

    /// <summary>
    /// R11.33 for a token request's declared charset (the stage's final gate, second round). The form
    /// reader looks a declared charset up through <c>MediaTypeHeaderValue.Encoding</c>, which throws
    /// <see cref="NotSupportedException"/> for UTF-7 and its aliases (SYSLIB0001), on the form's own
    /// Content-Type or on any multipart part's. The endpoint caught only what the reader throws for a
    /// body it cannot parse, so each answered 500 to anyone; <c>JsonCharset</c> exempts <c>/oauth</c>.
    /// Each is RFC 6749's <c>invalid_request</c> now. The last row is the other side: a form declared
    /// UTF-8 is read, and refused for what it lacks, so the refusal is the reader's and not a blanket one.
    /// </summary>
    [Theory]
    [InlineData("application/x-www-form-urlencoded; charset=utf-7", "a=b", true)]
    [InlineData("application/x-www-form-urlencoded; charset=csUnicode11UTF7", "a=b", true)]
    [InlineData("application/x-www-form-urlencoded; charset=unicode-1-1-utf-7", "a=b", true)]
    [InlineData("multipart/form-data; boundary=b", "--b\r\nContent-Disposition: form-data; name=\"a\"\r\nContent-Type: text/plain; charset=utf-7\r\n\r\nb\r\n--b--\r\n", true)]
    [InlineData("application/x-www-form-urlencoded; charset=utf-8", "a=b", false)]
    public async Task R11_33_ATokenRequestInACharsetTheFormReaderCannotDecodeIsInvalidRequest(string contentType, string body, bool unreadable)
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = Post(new Uri("/oauth/token", UriKind.Relative), Declared(new StringContent(body, Encoding.ASCII), contentType));

        using var response = await forum.Client.SendAsync(request, ct);
        var answer = await response.Content.ReadAsStringAsync(ct);

        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{(int)response.StatusCode} {answer[..Math.Min(answer.Length, 240)]}");
        using var json = JsonDocument.Parse(answer);
        Assert.Equal("invalid_request", json.RootElement.GetProperty("error").GetString());
        Assert.Equal(
            unreadable ? "The request body is not a form this endpoint can read" : "client_assertion_type must be jwt-bearer",
            json.RootElement.GetProperty("error_description").GetString());
    }

    /// <summary>
    /// R11.33 for a JSON body's declared charset (Task 8's review, I1): the minimal-API binder threw
    /// <see cref="InvalidOperationException"/> for a charset it does not know, before any filter ran,
    /// and answered 500 to anyone. A JSON body is read as UTF-8 only (RFC 8259 §8.1), so one that
    /// declares another charset is refused 415 before it is bound, a quoted <c>"utf-8"</c> included,
    /// since the binder does not unquote it and threw there too; one that declares none, or the bare
    /// token utf-8 in any case, is bound as before. The binder reads any +json media type as JSON, so
    /// the guard covers the suffix and the rows pin it (Task 8's second review, I1).
    /// </summary>
    [Theory]
    [InlineData("/v1/agents", "application/json; charset=bogus-xyz", true)]
    [InlineData("/v1/agents", "application/json; charset=utf-16", true)]
    [InlineData("/v1/agents", "application/json", false)]
    [InlineData("/v1/agents", "application/json; charset=utf-8", false)]
    [InlineData("/v1/agents", "application/json; charset=UTF-8", false)]
    [InlineData("/v1/agents", "application/json; charset=\"utf-8\"", true)]
    [InlineData("/v1/posts/batch", "application/json; charset=bogus-xyz", true)]
    [InlineData("/v1/posts/batch", "application/json; charset=utf-16", true)]
    [InlineData("/v1/posts/batch", "application/json", false)]
    [InlineData("/v1/posts/batch", "application/json; charset=utf-8", false)]
    [InlineData("/v1/posts/batch", "application/json; charset=UTF-8", false)]
    [InlineData("/v1/posts/batch", "application/json; charset=\"utf-8\"", true)]
    [InlineData("/v1/agents", "application/vnd.x+json; charset=bogus-xyz", true)]
    [InlineData("/v1/posts/batch", "application/vnd.x+json; charset=bogus-xyz", true)]
    [InlineData("/v1/agents", "application/vnd.x+json; charset=utf-8", false)]
    public async Task R11_33_AJsonBodyInACharsetOtherThanUtf8IsRefusedBeforeItIsBound(string path, string contentType, bool refused)
    {
        var ct = TestContext.Current.CancellationToken;
        var content = new ByteArrayContent("{}"u8.ToArray());
        Assert.True(content.Headers.TryAddWithoutValidation("Content-Type", contentType));
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = content };

        using var response = await forum.Client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (refused)
        {
            Assert.True(response.StatusCode == HttpStatusCode.UnsupportedMediaType, $"{(int)response.StatusCode} {body}");
            using var json = JsonDocument.Parse(body);
            Assert.Equal("curia/request/unsupported-charset", json.RootElement.GetProperty("type").GetString());
        }
        else
        {
            Assert.True(
                response.StatusCode != HttpStatusCode.UnsupportedMediaType && (int)response.StatusCode < 500,
                $"{(int)response.StatusCode} {body}");
        }
    }

    /// <summary>
    /// R11.33's problem document (Task 8's second review, I2): a request no handler can read, refused
    /// by the binder or by routing before any Forum code runs, is answered with the status the
    /// framework chose and an RFC 9457 problem document, with no detail, so nothing the framework said
    /// is echoed. It runs against the Development host on purpose: there the binder would throw, and
    /// the exception page serve its stack trace as text, unless it is told not to.
    /// </summary>
    [Theory]
    [InlineData("POST", "/v1/agents", "application/json", "{", 400, "curia/request/unreadable")]
    [InlineData("POST", "/v1/posts/batch", "application/json", "[", 400, "curia/request/unreadable")]
    [InlineData("POST", "/v1/agents", "text/plain", "{}", 415, "curia/request/unsupported-media-type")]
    [InlineData("GET", "/v1/log/entries/x", "", "", 404, "curia/request/no-route")]
    [InlineData("GET", "/oauth/nope", "", "", 404, "curia/request/no-route")]
    [InlineData("POST", "/oauth/jwks", "", "", 405, "curia/request/method-not-allowed")]
    public async Task R11_33_ARequestNoHandlerCanReadIsAnsweredWithAProblemDocument(
        string method, string path, string contentType, string body, int status, string type)
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (!string.IsNullOrEmpty(contentType))
        {
            request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
            Assert.True(request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType));
        }

        using var response = await forum.Client.SendAsync(request, ct);
        var served = await response.Content.ReadAsStringAsync(ct);

        Assert.True((int)response.StatusCode == status, $"{(int)response.StatusCode} {served[..Math.Min(served.Length, 160)]}");
        using var json = JsonDocument.Parse(served);
        Assert.Equal(type, json.RootElement.GetProperty("type").GetString());
        Assert.True(
            !json.RootElement.TryGetProperty("detail", out var detail) || detail.ValueKind == JsonValueKind.Null,
            $"the problem document carries a detail: {served}");
    }

    /// <summary>
    /// R11.33 names RFC 6749's error response as the one other form a 4xx may take, "at the token
    /// endpoint". Routing's refusal there -- a method the endpoint does not take -- is composed by no
    /// handler, so it is answered with RFC 6749 §5.2's error object (<c>invalid_request</c>) and the
    /// status routing chose, not a problem document and not an empty body (the stage's final gate).
    /// </summary>
    [Theory]
    [InlineData("GET", "/oauth/token", 405)]
    [InlineData("PUT", "/oauth/token", 405)]
    [InlineData("DELETE", "/oauth/token", 405)]
    public async Task R11_33_ARequestRoutingRefusesAtTheTokenEndpointIsAnsweredWithRfc6749sError(
        string method, string path, int status)
    {
        var ct = TestContext.Current.CancellationToken;
        using var request = new HttpRequestMessage(new HttpMethod(method), path);

        using var response = await forum.Client.SendAsync(request, ct);
        var served = await response.Content.ReadAsStringAsync(ct);

        Assert.True((int)response.StatusCode == status, $"{(int)response.StatusCode} {served[..Math.Min(served.Length, 160)]}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        using var json = JsonDocument.Parse(served);
        Assert.Equal(JsonValueKind.Object, json.RootElement.ValueKind);
        Assert.True(json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String, served);
        Assert.Equal("invalid_request", error.GetString());
        Assert.False(json.RootElement.TryGetProperty("type", out _), $"the error object carries a type: {served}");
    }

    /// <summary>
    /// Null when an answer is no 4xx, or is a 4xx in the form its route owes; otherwise why it is not
    /// (Task 8's second review, I2). The token endpoint answers RFC 6749 §5.2's error object; every
    /// other route an RFC 9457 problem document, whose type need not be one of <c>curia/</c>'s.
    /// </summary>
    private static string? NotAProblem(string path, int status, string body)
    {
        if (status < 400 || status > 499) return null;

        JsonElement root;
        try
        {
            using var json = JsonDocument.Parse(body);
            root = json.RootElement.Clone();
        }
        catch (JsonException)
        {
            return "not JSON";
        }

        if (root.ValueKind != JsonValueKind.Object) return "not a JSON object";

        if (path.Equals("/oauth/token", StringComparison.Ordinal))
        {
            return root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String
                ? null
                : "no string error member";
        }

        return root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString()!.Length > 0
            ? null
            : "no non-empty string type member";
    }

    /// <summary>
    /// The one hand-written list is held to the handlers: a parameter a handler binds from the query
    /// and the list does not name would go unprobed, so it fails here by name.
    /// </summary>
    [Fact]
    public void EveryQueryParameterAHandlerBindsIsProbed()
    {
        var unprobed = new List<string>();
        var bound = 0;

        foreach (var endpoint in forum.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
        {
            if (endpoint.Metadata.GetMetadata<MethodInfo>() is not { } handler) continue;
            var routeNames = endpoint.RoutePattern.Parameters.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var parameter in handler.GetParameters())
            {
                var type = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;
                if (type != typeof(string) && type != typeof(long) && type != typeof(int) && type != typeof(bool)) continue;
                if (routeNames.Contains(parameter.Name!)) continue;

                bound++;
                var name = parameter.GetCustomAttribute<FromQueryAttribute>()?.Name ?? parameter.Name!;
                if (!QueryParameters.Contains(name, StringComparer.Ordinal))
                    unprobed.Add($"{endpoint.RoutePattern.RawText} binds '{name}'");
            }
        }

        Assert.True(bound > 0, "no handler binds a query parameter, so this fact checked nothing; the reflection is wrong");
        Assert.True(unprobed.Count == 0, "query parameters the sweep never sends:\n" + string.Join('\n', unprobed));
    }

    /// <summary>
    /// The anonymous requests to a host running as production does, which has no developer exception
    /// page: no body carries the framework's or a backend's words, and none is a server fault, or a
    /// 4xx that is no problem document (Task 8's second review, I2). The Api test host runs in
    /// Development, whose exception page serves a binding failure's exception text; what a deployed
    /// Forum serves had not been probed (register D25).
    /// </summary>
    [Fact]
    public async Task R11_33_AProductionHostServesNoTextItDidNotCompose()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var production = forum.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = production.CreateClient();

        var leaks = new List<string>();
        var sent = 0;

        foreach (var (method, pattern, parameters) in Routes(forum))
        {
            foreach (var target in Targets(pattern, parameters, method == "GET"))
            {
                foreach (var make in Requests(method, target))
                {
                    using var request = make();
                    HttpResponseMessage response;
                    try
                    {
                        response = await client.SendAsync(request, ct);
                    }
                    catch (InvalidOperationException refused) when (NotSent(refused))
                    {
                        continue;
                    }
                    catch (Exception thrown) when (thrown is not OperationCanceledException)
                    {
                        // A production host has no exception page: the test server hands the
                        // host's own exception to its caller, where Kestrel would answer 500.
                        leaks.Add($"500 {method} {request.RequestUri}: the host threw {thrown.GetType().Name}");
                        continue;
                    }

                    using (response)
                    {
                        sent++;
                        var body = await response.Content.ReadAsStringAsync(ct);
                        var status = (int)response.StatusCode;
                        if (body.Contains("Exception", StringComparison.Ordinal)
                            || body.Contains("Microsoft.AspNetCore", StringComparison.Ordinal)
                            || body.Contains("Npgsql", StringComparison.Ordinal)
                            || status >= 500)
                            leaks.Add($"{status} {method} {request.RequestUri}: {body[..Math.Min(body.Length, 160)]}");
                        else if (NotAProblem(target, status, body) is { } reason)
                            leaks.Add($"{status} {method} {request.RequestUri}: {reason}: {body[..Math.Min(body.Length, 160)]}");
                    }
                }
            }
        }

        Assert.True(sent > 0, "no request was sent to the production host; the sweep did not run");
        Assert.True(leaks.Count == 0, "a production host served text it did not compose, a server fault, or a 4xx that is no problem document:\n" + string.Join('\n', leaks));
    }

    /// <summary>
    /// An agent enrolled through the route and raised to T1 as Table 11 raises one: its owner
    /// attested (R4.30), three questions asked, and 49 hours on the fixture's clock. T1 because a tier
    /// may do everything a lesser one may, so its requests reach every handler a T0 agent's reach, and
    /// those a T0 agent is refused before.
    /// </summary>
    private async Task<(DpopClient Dpop, string Token)> EnrolledAtT1Async(HttpClient client, CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var agent = ForumAgent.Create("https://agents.example/surface-" + suffix, "surface-" + suffix);
        var (dpop, _) = await agent.AuthenticateAsync(client, TokenEndpoint, forum.Now, ct);
        await forum.AttestOwnerAsync(agent.AgentId, ct);

        for (var i = 0; i < 3; i++)
        {
            using var asked = await dpop.PostAsync(
                client,
                PostsUrl,
                await dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct),
                agent.SignQuestion("surface-" + suffix, "A warm-up question " + Guid.NewGuid().ToString("N"), "Warm-up " + i, forum.Now),
                forum.Now,
                ct);
            Assert.Equal(HttpStatusCode.Created, asked.StatusCode);
        }

        forum.Clock.Advance(TimeSpan.FromHours(49));
        return (dpop, await dpop.GetTokenAsync(client, TokenEndpoint, forum.Now, ct));
    }

    /// <summary>
    /// One request with the agent's token and a fresh proof, sent again once with the nonce a write
    /// path asks for (RFC 9449 §8); or null when the test host's client refuses to send it.
    /// </summary>
    private async Task<HttpResponseMessage?> SendAsAgentAsync(
        HttpClient client, Func<HttpRequestMessage> make, DpopClient dpop, string token, string method, string htu, CancellationToken ct)
    {
        string? nonce = null;
        for (var attempt = 0; ; attempt++)
        {
            using var request = make();
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
            request.Headers.Add("DPoP", dpop.Proof(method, htu, forum.Now, token, nonce));

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, ct);
            }
            catch (InvalidOperationException refused) when (NotSent(refused))
            {
                return null;
            }

            if (attempt == 0
                && response.StatusCode == HttpStatusCode.Unauthorized
                && response.Headers.TryGetValues("DPoP-Nonce", out var nonces))
            {
                nonce = nonces.First();
                response.Dispose();
                continue;
            }

            return response;
        }
    }

    /// <summary>The test host's client refuses a path holding U+0000 before sending it; nothing reached the Forum.</summary>
    private static bool NotSent(InvalidOperationException refused) =>
        refused.Message.Contains("null characters", StringComparison.Ordinal);

    /// <summary>Every route the host registers: its method, its pattern and its route parameters.</summary>
    private static List<(string Method, string Pattern, string[] Parameters)> Routes(ForumFixture forum) =>
        [.. forum.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"])
                .Select(m => (m, e.RoutePattern.RawText ?? "", e.RoutePattern.Parameters.Select(p => p.Name).ToArray())))];

    /// <summary>
    /// The URLs to send for one route: each hostile value in each route parameter, the others filled
    /// with a plain value; and for a read, each hostile value in each query parameter.
    /// </summary>
    private static IEnumerable<string> Targets(string pattern, string[] parameters, bool read)
    {
        string Fill(string except, string value) =>
            parameters.Aggregate(pattern, (path, name) =>
                path.Replace("{" + name + ":long}", name == except ? value : "0", StringComparison.Ordinal)
                    .Replace("{" + name + "}", name == except ? value : "x", StringComparison.Ordinal));

        var plain = Plain(pattern, parameters);

        foreach (var name in parameters)
            foreach (var value in Hostile)
                yield return Fill(name, value);

        // A write's hostile bodies go to its plain path too, so a write with no route parameter --
        // the token endpoint, the enrollment route, the batch -- is sent them.
        if (!read)
        {
            yield return plain;
            yield break;
        }

        foreach (var name in QueryParameters)
            foreach (var value in Hostile)
                yield return $"{plain}?{name}={value}";
    }

    /// <summary>A route's path with every parameter a plain value.</summary>
    private static string Plain(string pattern, string[] parameters) =>
        parameters.Aggregate(pattern, (path, name) =>
            path.Replace("{" + name + ":long}", "0", StringComparison.Ordinal)
                .Replace("{" + name + "}", "x", StringComparison.Ordinal));

    /// <summary>
    /// For a read, one GET. For a write, nineteen bodies, each made fresh so a request can be sent again:
    /// none; an empty object; an object whose members hold U+0000 and a line break; a form whose
    /// values hold U+0000; a multipart form cut off before its closing boundary; JSON cut off; JSON
    /// nested two hundred deep; JSON whose bytes are not UTF-8; an empty object whose Content-Type
    /// names a charset no encoder knows, one naming UTF-16, one naming a quoted "utf-8", and one
    /// naming an empty charset (Task 8's review, I1); an empty object declared as a +json media type
    /// in a charset no encoder knows (Task 8's second review, I1); a multipart form with no boundary;
    /// a form whose key is five thousand bytes; and a form declared in UTF-7 under each of three of
    /// its names, and a multipart form one of whose parts is, which the form reader cannot decode (the
    /// stage's final gate, second round).
    /// </summary>
    private static IEnumerable<Func<HttpRequestMessage>> Requests(string method, string target)
    {
        var uri = new Uri(target, UriKind.Relative);
        if (method == "GET")
        {
            yield return () => new HttpRequestMessage(HttpMethod.Get, uri);
            yield break;
        }

        yield return () => new HttpRequestMessage(HttpMethod.Post, uri);
        yield return () => Post(uri, new StringContent("{}", Encoding.UTF8, "application/json"));
        yield return () => Post(uri, new StringContent(
            "{\"digests\":[\"a\\u0000b\"],\"agent_id\":\"\\u0000\",\"kid\":\"\\n\",\"kind\":\"\\n\",\"rationale\":\"\\u0000\"}",
            Encoding.UTF8,
            "application/json"));
        yield return () => Post(uri, new StringContent(
            "{\"digests\":[\"\\ud800\"],\"agent_id\":\"\\ud800\",\"kid\":\"\\udc00\",\"kind\":\"\\ud800\",\"rationale\":\"\\ud800\"}",
            Encoding.UTF8,
            "application/json"));
        yield return () => Post(uri, new StringContent(
            "grant_type=client_credentials&client_id=a%00b&client_assertion=%00", Encoding.ASCII, "application/x-www-form-urlencoded"));
        yield return () => Post(uri, Typed(
            new StringContent("--b\r\nContent-Disposition: form-data; name=\"client_id\"\r\n\r\na", Encoding.ASCII),
            "multipart/form-data; boundary=b"));
        yield return () => Post(uri, new StringContent("{\"digests\":[", Encoding.UTF8, "application/json"));
        yield return () => Post(uri, new StringContent(new string('[', 200) + new string(']', 200), Encoding.UTF8, "application/json"));
        yield return () => Post(uri, Typed(new ByteArrayContent([0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xFF, 0xFE, 0x22, 0x7D]), "application/json"));
        yield return () => Post(uri, Declared(new StringContent("{}", Encoding.UTF8), "application/json; charset=bogus-xyz"));
        yield return () => Post(uri, Declared(new StringContent("{}", Encoding.UTF8), "application/json; charset=utf-16"));
        yield return () => Post(uri, Declared(new StringContent("{}", Encoding.UTF8), "application/json; charset=\"utf-8\""));
        yield return () => Post(uri, Declared(new StringContent("{}", Encoding.UTF8), "application/json; charset="));
        yield return () => Post(uri, Declared(new StringContent("{}", Encoding.UTF8), "application/vnd.x+json; charset=bogus-xyz"));
        yield return () => Post(uri, Typed(
            new StringContent("--b\r\nContent-Disposition: form-data; name=\"client_id\"\r\n\r\na\r\n--b--\r\n", Encoding.ASCII),
            "multipart/form-data"));
        yield return () => Post(uri, new StringContent(new string('k', 5000) + "=v", Encoding.ASCII, "application/x-www-form-urlencoded"));
        yield return () => Post(uri, Declared(new StringContent("a=b", Encoding.ASCII), "application/x-www-form-urlencoded; charset=utf-7"));
        yield return () => Post(uri, Declared(new StringContent("a=b", Encoding.ASCII), "application/x-www-form-urlencoded; charset=csUnicode11UTF7"));
        yield return () => Post(uri, Declared(new StringContent("a=b", Encoding.ASCII), "application/x-www-form-urlencoded; charset=unicode-1-1-utf-7"));
        yield return () => Post(uri, Declared(
            new StringContent("--b\r\nContent-Disposition: form-data; name=\"a\"\r\nContent-Type: text/plain; charset=utf-7\r\n\r\nb\r\n--b--\r\n", Encoding.ASCII),
            "multipart/form-data; boundary=b"));
    }

    private static HttpRequestMessage Post(Uri uri, HttpContent content) =>
        new(HttpMethod.Post, uri) { Content = content };

    private static HttpContent Typed(HttpContent content, string mediaType)
    {
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        return content;
    }

    /// <summary>
    /// The Content-Type as written, unvalidated: the client's parser refuses an empty charset, which a
    /// caller that is not this client can still send.
    /// </summary>
    private static HttpContent Declared(HttpContent content, string contentType)
    {
        content.Headers.Remove("Content-Type");
        if (!content.Headers.TryAddWithoutValidation("Content-Type", contentType))
            throw new InvalidOperationException("the test client would not carry this Content-Type");
        return content;
    }
}
