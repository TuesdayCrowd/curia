using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Headers;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// Errata G84 (R6.68 (rev.), R10.68 (rev.), R11.33), D125: the Forum bounds a request body at R6.39's size plus one byte, on every route and for every method, by one middleware, so that these probes
/// run in process and a Kestrel run (<see cref="BodyBoundKestrelTests"/>) holds what only a socket can. Every size is R6.39's number parsed from the white paper (<see cref="BodyBoundKit.Cap"/>)
/// and never read from the code the probe is about. Each declared-length probe has the twin one byte under it, because a probe that sees only the refusal is satisfied by an unmapped route's 404 (BND1);
/// each probe of a declared body has a chunked twin, because <c>HttpClient</c>'s default is a declared length and an undeclared body takes the other path (trap 304; BND2). In process the twin is still a declared body (<c>TestServer</c> declares a length for the content it is given); the undeclared path is held over a socket by BND6b and BND2k in <see cref="BodyBoundKestrelTests"/>.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class BodyBoundTests(ForumFixture forum) : IClassFixture<ForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string TokenTitle = "The request is larger than this route reads";

    /// <summary>The key acts' two routes have their own 32,768 bytes, which answer first; BND7 holds them, and the one-byte-under twin of the global figure would be refused by the route's own bound.</summary>
    private static bool IsKeyAct(string path) => path.StartsWith("/v1/agents/", StringComparison.Ordinal) && (path.EndsWith("/keys", StringComparison.Ordinal) || path.EndsWith("/retire", StringComparison.Ordinal));

    private async Task<(int Status, string Text)> SendAsync(string method, string path, byte[] body, string contentType, bool chunked)
    {
        using var http = forum.Client;
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = BodyBoundKit.Content(body, contentType, chunked) };
        using var response = await http.SendAsync(request, Ct);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private static string? ProblemType(string text) => BodyBoundKit.MemberText(text, "type");

    private static bool IsTheRefusal(string path, int status, string text) =>
        status == 413
        && (path == "/oauth/token"
            ? BodyBoundKit.MemberText(text, "error") == "invalid_request" && BodyBoundKit.MemberText(text, "error_description") == TokenTitle
            : ProblemType(text) == BodyBoundKit.TooLargeType);

    /// <summary>
    /// BND1. For every route and method the host registers (read from the endpoint data source, so a route added later is held without a row), a declared body of cap + 1 bytes is refused 413 in R11.33's shape
    /// (RFC 6749's object at the token route), and the twin at exactly the cap is not: a route that reads answers its own refusal, a route that does not read answers as it always did, and an unmapped
    /// path answers 404. The key acts' twin is BND7's.
    /// </summary>
    [Fact]
    public async Task R6_68_BND1_EveryRouteRefusesADeclaredBodyPastTheBoundAndOnlyThat()
    {
        var routes = BodyBoundKit.EveryRoute(forum);
        // The enumeration holds the plan's table: a data source that lost a reading route would shrink what is compared.
        foreach (var named in new[] { "/v1/agents", "/v1/posts", "/v1/posts/batch", "/v1/posts/zz/flags", "/v1/posts/zz/accept", "/oauth/token" })
            Assert.True(routes.Any(r => r.Path == named), $"the endpoint data source lists no route {named}");
        Assert.True(routes.Any(r => IsKeyAct(r.Path)), "the endpoint data source lists no key-act route");
        var problems = new List<string>();
        var bodyOver = new byte[BodyBoundKit.OverCap];
        Array.Fill(bodyOver, (byte)'a');
        var bodyAt = new byte[BodyBoundKit.Cap];
        Array.Fill(bodyAt, (byte)'a');

        foreach (var (method, path) in routes.Append(("POST", "/v1/zz-unmapped")))
        {
            var type = path == "/oauth/token" ? "application/x-www-form-urlencoded" : "application/json";
            var (over, overText) = await SendAsync(method, path, bodyOver, type, chunked: false);
            if (!IsTheRefusal(path, over, overText)) problems.Add($"{method} {path} declared {bodyOver.Length}: expected 413 {BodyBoundKit.TooLargeType}, got {BodyBoundKit.Describe(over, overText)}");

            if (IsKeyAct(path)) continue;
            var (at, atText) = await SendAsync(method, path, bodyAt, type, chunked: false);
            if (at == 413) problems.Add($"{method} {path} declared {bodyAt.Length} (the bound itself): answered 413 {atText}");
            if (path == "/v1/zz-unmapped" && at != 404) problems.Add($"{method} {path} declared {bodyAt.Length}: expected 404, got {BodyBoundKit.Describe(at, atText)}");
        }

        Assert.True(problems.Count == 0, $"{problems.Count} problems over {routes.Count + 1} routes:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    /// <summary>BND2. The same on a body sent with no declared length, on each of the five routes the global figure alone bounds (the key acts' chunked rows are BND7's, answered by the route's own bound): cap + 1 is refused, the bound itself is not.</summary>
    [Theory]
    [InlineData("agents")]
    [InlineData("batch")]
    [InlineData("flags")]
    [InlineData("token")]
    [InlineData("posts")]
    public async Task R6_68_BND2_AChunkedBodyPastTheBoundIsRefusedAndTheBoundItselfIsNot(string route)
    {
        using var http = forum.Client;
        var credential = route == "posts" ? await BodyBoundKit.SignInAsync(forum, http, Ct) : null;
        var path = BodyBoundKit.PathOf(route);
        var type = BodyBoundKit.ContentTypeOf(route);

        async Task<(int Status, string Text)> SendChunkedAsync(long size)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = BodyBoundKit.Content(BodyBoundKit.Body(route, size), type, chunked: true) };
            if (credential is not null) BodyBoundKit.Authorize(request, credential, forum, "POST", forum.PublicOrigin + path);
            using var response = await http.SendAsync(request, Ct);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }

        var (over, overText) = await SendChunkedAsync(BodyBoundKit.OverCap);
        var (at, atText) = await SendChunkedAsync(BodyBoundKit.Cap);
        Assert.True(IsTheRefusal(path, over, overText), $"{route}, chunked, {BodyBoundKit.OverCap} bytes: expected 413 {BodyBoundKit.TooLargeType}, got {BodyBoundKit.Describe(over, overText)}");
        Assert.True(at != 413, $"{route}, chunked, {BodyBoundKit.Cap} bytes (the bound itself): answered {BodyBoundKit.Describe(at, atText)}");
    }

    /// <summary>
    /// BND3. R6.40 stays reachable over HTTP: a post of exactly the bound answers the route's 400 <c>curia/admit/size-exceeded</c>, and one byte more answers 413; at <c>/v1/agents</c> the first is the route's own 400 and the
    /// second 413. The two sizes are the white paper's number plus one and plus two.
    /// </summary>
    [Fact]
    public async Task R6_68_BND3_TheBoundaryIsOneByteBetweenAdmitsRefusalAndTheBounds()
    {
        using var http = forum.Client;
        var credential = await BodyBoundKit.SignInAsync(forum, http, Ct);

        async Task<(int Status, string Text)> PostAsync(string route, long size)
        {
            var path = BodyBoundKit.PathOf(route);
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = BodyBoundKit.Content(BodyBoundKit.Body(route, size), "application/json", chunked: false) };
            if (route == "posts") BodyBoundKit.Authorize(request, credential, forum, "POST", forum.PublicOrigin + path);
            using var response = await http.SendAsync(request, Ct);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }

        var (postAt, postAtText) = await PostAsync("posts", BodyBoundKit.Cap);
        var (postOver, postOverText) = await PostAsync("posts", BodyBoundKit.OverCap);
        var (agentsAt, agentsAtText) = await PostAsync("agents", BodyBoundKit.Cap);
        var (agentsOver, agentsOverText) = await PostAsync("agents", BodyBoundKit.OverCap);

        var problems = new List<string>();
        if (postAt != 400 || ProblemType(postAtText) != "curia/admit/size-exceeded") problems.Add($"POST /v1/posts, {BodyBoundKit.Cap} bytes: expected 400 curia/admit/size-exceeded, got {BodyBoundKit.Describe(postAt, postAtText)}");
        if (!IsTheRefusal("/v1/posts", postOver, postOverText)) problems.Add($"POST /v1/posts, {BodyBoundKit.OverCap} bytes: expected 413, got {BodyBoundKit.Describe(postOver, postOverText)}");
        if (agentsAt != 400 || ProblemType(agentsAtText) == BodyBoundKit.TooLargeType) problems.Add($"POST /v1/agents, {BodyBoundKit.Cap} bytes: expected the route's own 400, got {BodyBoundKit.Describe(agentsAt, agentsAtText)}");
        if (!IsTheRefusal("/v1/agents", agentsOver, agentsOverText)) problems.Add($"POST /v1/agents, {BodyBoundKit.OverCap} bytes: expected 413, got {BodyBoundKit.Describe(agentsOver, agentsOverText)}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// BND4. The refusal's shape: a problem document whose members are exactly those of the Forum's own 404 problem (the control, so the probe reads what a problem is here and not what it assumes), a
    /// <c>Content-Type</c> equal to the control's, no value of any request header sent; at the token route exactly <c>error</c> and <c>error_description</c>.
    /// </summary>
    [Fact]
    public async Task R6_68_BND4_TheRefusalIsAProblemDocumentThatEchoesNothing()
    {
        using var http = forum.Client;
        var secret = "bnd4-" + Guid.NewGuid().ToString("N");

        async Task<(HttpResponseMessage Response, string Text)> SendAsync(string path, long size, string type)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = BodyBoundKit.Content(new byte[size], type, chunked: false) };
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", secret);
            request.Headers.Add("DPoP", secret);
            request.Headers.Add("X-Probe", secret);
            var response = await http.SendAsync(request, Ct);
            return (response, await response.Content.ReadAsStringAsync(Ct));
        }

        var (control, controlText) = await SendAsync("/v1/zz-unmapped", 1, "application/json");
        var (refused, refusedText) = await SendAsync("/v1/agents", BodyBoundKit.OverCap, "application/json");
        var (token, tokenText) = await SendAsync("/oauth/token", BodyBoundKit.OverCap, "application/x-www-form-urlencoded");
        var problems = new List<string>();
        if (refused.StatusCode != HttpStatusCode.RequestEntityTooLarge) problems.Add($"POST /v1/agents over the bound: expected 413, got {BodyBoundKit.Describe((int)refused.StatusCode, refusedText)}");
        else
        {
            var controlMembers = string.Join(",", BodyBoundKit.MembersOf(controlText) ?? ["(control is not an object)"]);
            var members = string.Join(",", BodyBoundKit.MembersOf(refusedText) ?? ["(not an object)"]);
            if (members != controlMembers) problems.Add($"members {members}, the Forum's own 404 problem has {controlMembers}");
            if (refused.Content.Headers.ContentType?.ToString() != control.Content.Headers.ContentType?.ToString()) problems.Add($"content type {refused.Content.Headers.ContentType}, the control's is {control.Content.Headers.ContentType}");
            if (refusedText.Contains(secret, StringComparison.Ordinal)) problems.Add("the body echoes a request header value");
            if (BodyBoundKit.MemberText(refusedText, "detail") is not (null or "")) problems.Add("the body carries a detail");
        }

        if (token.StatusCode != HttpStatusCode.RequestEntityTooLarge) problems.Add($"POST /oauth/token over the bound: expected 413, got {BodyBoundKit.Describe((int)token.StatusCode, tokenText)}");
        else
        {
            var members = string.Join(",", BodyBoundKit.MembersOf(tokenText) ?? ["(not an object)"]);
            if (members != "error,error_description") problems.Add($"the token route's members are {members}, expected error,error_description");
            if (tokenText.Contains(secret, StringComparison.Ordinal)) problems.Add("the token route's body echoes a request header value");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>BND5. A refusal leaves the Forum serving: a new connection's ordinary request is answered after a declared and a chunked refusal.</summary>
    [Fact]
    public async Task R6_68_BND5_AfterARefusalANewRequestIsServed()
    {
        var (declared, declaredText) = await SendAsync("POST", "/v1/agents", new byte[BodyBoundKit.OverCap], "application/json", chunked: false);
        var (chunked, chunkedText) = await SendAsync("POST", "/v1/agents", new byte[BodyBoundKit.OverCap], "application/json", chunked: true);
        using var http = forum.Client;
        using var after = await http.GetAsync(new Uri("/v1/enroll/nonce", UriKind.Relative), Ct);
        var problems = new List<string>();
        if (declared != 413) problems.Add($"declared: expected 413, got {BodyBoundKit.Describe(declared, declaredText)}");
        if (chunked != 413) problems.Add($"chunked: expected 413, got {BodyBoundKit.Describe(chunked, chunkedText)}");
        if (after.StatusCode != HttpStatusCode.OK) problems.Add($"the next request answered {(int)after.StatusCode}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>
    /// BND7. The key acts' two bands: a body of 32,768 bytes is read as today (the route's 400, not 413); 32,769 answers 413 by the route's own bound, with a credential (the route authenticates before it reads),
    /// chunked and declared alike, and <b>this row does not tell the global bound from the route's own</b>; a declared body past the global figure and with no credential answers 413, which the route
    /// alone could not (it would answer 401 first), and so is the global bound's row.
    /// </summary>
    [Fact]
    public async Task R6_68_BND7_TheKeyActsRouteKeepsItsOwnBoundAndTheGlobalOneAnswersFirstBeforeAnyCredential()
    {
        using var http = forum.Client;
        var credential = await BodyBoundKit.SignInAsync(forum, http, Ct);
        var segment = Uri.EscapeDataString(credential.AgentId).Replace("%3A", ":", StringComparison.Ordinal);
        var path = $"/v1/agents/{segment}/keys";
        var target = forum.PublicOrigin + path;

        async Task<(int Status, string Text)> SendAsync(long size, bool chunked, bool authorized)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = BodyBoundKit.Content(new byte[size], "application/json", chunked) };
            if (authorized) BodyBoundKit.Authorize(request, credential, forum, "POST", target);
            using var response = await http.SendAsync(request, Ct);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
        }

        var (atBand, atBandText) = await SendAsync(32_768, chunked: false, authorized: true);
        var (overBand, overBandText) = await SendAsync(32_769, chunked: false, authorized: true);
        var (overBandChunked, overBandChunkedText) = await SendAsync(32_769, chunked: true, authorized: true);
        var (globalDeclared, globalDeclaredText) = await SendAsync(BodyBoundKit.OverCap, chunked: false, authorized: false);
        var problems = new List<string>();
        if (atBand == 413) problems.Add($"32,768 bytes: answered {BodyBoundKit.Describe(atBand, atBandText)}, expected the route's own 400");
        if (!IsTheRefusal(path, overBand, overBandText)) problems.Add($"32,769 bytes declared: expected 413, got {BodyBoundKit.Describe(overBand, overBandText)}");
        if (!IsTheRefusal(path, overBandChunked, overBandChunkedText)) problems.Add($"32,769 bytes chunked: expected 413, got {BodyBoundKit.Describe(overBandChunked, overBandChunkedText)}");
        if (!IsTheRefusal(path, globalDeclared, globalDeclaredText)) problems.Add($"{BodyBoundKit.OverCap} bytes declared with no credential: expected 413, got {BodyBoundKit.Describe(globalDeclared, globalDeclaredText)}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>BND8. <c>accept</c> reads no body: a declared body past the bound is refused 413 before the handler, and a body at the bound is answered as today (401, no credential).</summary>
    [Fact]
    public async Task R6_68_BND8_AcceptRefusesADeclaredBodyPastTheBoundAndAnswersAsTodayAtIt()
    {
        var (over, overText) = await SendAsync("POST", BodyBoundKit.PathOf("accept"), new byte[BodyBoundKit.OverCap], "application/json", chunked: false);
        var (at, atText) = await SendAsync("POST", BodyBoundKit.PathOf("accept"), new byte[BodyBoundKit.Cap], "application/json", chunked: false);
        var problems = new List<string>();
        if (!IsTheRefusal("/v1/posts/zz/accept", over, overText)) problems.Add($"{BodyBoundKit.OverCap} bytes: expected 413, got {BodyBoundKit.Describe(over, overText)}");
        if (at != 401) problems.Add($"{BodyBoundKit.Cap} bytes: expected the handler's 401, got {BodyBoundKit.Describe(at, atText)}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>BND9. The bound precedes routing: an unmapped path refuses a declared body past the bound 413 and answers 404 at it.</summary>
    [Fact]
    public async Task R6_68_BND9_TheBoundPrecedesRouting()
    {
        var (over, overText) = await SendAsync("POST", "/v1/zz-unmapped", new byte[BodyBoundKit.OverCap], "application/json", chunked: false);
        var (at, atText) = await SendAsync("POST", "/v1/zz-unmapped", new byte[BodyBoundKit.Cap], "application/json", chunked: false);
        var problems = new List<string>();
        if (!IsTheRefusal("/v1/zz-unmapped", over, overText)) problems.Add($"{BodyBoundKit.OverCap} bytes: expected 413, got {BodyBoundKit.Describe(over, overText)}");
        if (at != 404) problems.Add($"{BodyBoundKit.Cap} bytes: expected 404, got {BodyBoundKit.Describe(at, atText)}");
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }
}
