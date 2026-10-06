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
/// handler reads, a conditional read's <c>If-None-Match</c> or a body's <c>Content-Type</c>, is not
/// swept.</para>
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
    /// answers 5xx.
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
                        if ((int)response.StatusCode >= 500)
                            faults.Add($"{(int)response.StatusCode} {method} {request.RequestUri}");
                    }
                }
            }
        }

        Assert.True(sent > routes.Count * Hostile.Length, $"only {sent} requests were sent over {routes.Count} routes; the sweep did not run");
        Assert.True(faults.Count == 0, "anonymous requests answered as a server fault:\n" + string.Join('\n', faults));
    }

    /// <summary>
    /// The same requests from an enrolled agent: each carries its DPoP-bound token and a proof over
    /// the URL it was sent to, and a write's is sent again with the nonce the Forum asks for (R5.19).
    /// None answers 5xx, and none is stopped at authentication, or the handlers behind it were never
    /// reached and the first assertion would hold of nothing.
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
                    if ((int)response.StatusCode >= 500)
                        faults.Add($"{(int)response.StatusCode} {method} {target}");
                    else if (response.StatusCode == HttpStatusCode.Unauthorized)
                        unauthenticated.Add($"{method} {target}");
                }
            }
        }

        Assert.True(sent > routes.Count * Hostile.Length, $"only {sent} requests were sent over {routes.Count} routes; the sweep did not run");
        Assert.True(unauthenticated.Count == 0, "requests an enrolled agent sent were stopped at authentication, so nothing behind it was reached:\n" + string.Join('\n', unauthenticated));
        Assert.True(faults.Count == 0, "requests an enrolled agent sent answered as a server fault:\n" + string.Join('\n', faults));
    }

    /// <summary>
    /// R11.33's headers. Every route is sent, with no credential, hostile <c>Authorization</c> and
    /// <c>DPoP</c> headers: a token that is not a JWS, one whose header is not an object, one naming
    /// a <c>kid</c> holding U+0000, a scheme the Forum does not know, a token four thousand bytes
    /// long, and a proof with no token. Then an enrolled agent obtains a token bound to a proof key
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
    /// page: no body carries the framework's or a backend's words. The Api test host runs in
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
                        if (body.Contains("Exception", StringComparison.Ordinal)
                            || body.Contains("Microsoft.AspNetCore", StringComparison.Ordinal)
                            || body.Contains("Npgsql", StringComparison.Ordinal)
                            || (int)response.StatusCode >= 500)
                            leaks.Add($"{(int)response.StatusCode} {method} {request.RequestUri}: {body[..Math.Min(body.Length, 160)]}");
                    }
                }
            }
        }

        Assert.True(sent > 0, "no request was sent to the production host; the sweep did not run");
        Assert.True(leaks.Count == 0, "a production host served text it did not compose, or a server fault:\n" + string.Join('\n', leaks));
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
    /// For a read, one GET. For a write, ten bodies, each made fresh so a request can be sent again:
    /// none; an empty object; an object whose members hold U+0000 and a line break; a form whose
    /// values hold U+0000; a multipart form cut off before its closing boundary; JSON cut off; JSON
    /// nested two hundred deep; JSON whose bytes are not UTF-8; a multipart form with no boundary; and
    /// a form whose key is five thousand bytes.
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
            "grant_type=client_credentials&client_id=a%00b&client_assertion=%00", Encoding.ASCII, "application/x-www-form-urlencoded"));
        yield return () => Post(uri, Typed(
            new StringContent("--b\r\nContent-Disposition: form-data; name=\"client_id\"\r\n\r\na", Encoding.ASCII),
            "multipart/form-data; boundary=b"));
        yield return () => Post(uri, new StringContent("{\"digests\":[", Encoding.UTF8, "application/json"));
        yield return () => Post(uri, new StringContent(new string('[', 200) + new string(']', 200), Encoding.UTF8, "application/json"));
        yield return () => Post(uri, Typed(new ByteArrayContent([0x7B, 0x22, 0x61, 0x22, 0x3A, 0x22, 0xFF, 0xFE, 0x22, 0x7D]), "application/json"));
        yield return () => Post(uri, Typed(
            new StringContent("--b\r\nContent-Disposition: form-data; name=\"client_id\"\r\n\r\na\r\n--b--\r\n", Encoding.ASCII),
            "multipart/form-data"));
        yield return () => Post(uri, new StringContent(new string('k', 5000) + "=v", Encoding.ASCII, "application/x-www-form-urlencoded"));
    }

    private static HttpRequestMessage Post(Uri uri, HttpContent content) =>
        new(HttpMethod.Post, uri) { Content = content };

    private static HttpContent Typed(HttpContent content, string mediaType)
    {
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(mediaType);
        return content;
    }
}
