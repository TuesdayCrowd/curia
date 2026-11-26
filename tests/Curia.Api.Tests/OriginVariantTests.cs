using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;
using static Curia.Api.Tests.OriginProbe;

namespace Curia.Api.Tests;

/// <summary>
/// The probes of errata G82 that need a host the plain fixture is not: a scheme the origin does not have (ORG7), a path base (ORG8), forwarded headers by the framework's own door and by code (ORG9(i), (ii)),
/// and an internationalized origin on a real socket (ORG20). <b>A token is minted first by whichever spelling the Forum under test honours</b> (its own name once D41 is closed, the request's on the tree
/// that builds from it), so a row that is red on the tree is red by its own assertion and not by the setup it needs. The credentials of every row are literals.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class OriginVariantTests(
    HttpsNamedForumFixture https,
    PathBaseForumFixture pathBase,
    ForwardedDoorForumFixture door,
    ForwardedHostForumFixture forwarded)
    : IClassFixture<HttpsNamedForumFixture>, IClassFixture<PathBaseForumFixture>, IClassFixture<ForwardedDoorForumFixture>, IClassFixture<ForwardedHostForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A token however this Forum will give one: the first of <paramref name="spellings"/> it honours.</summary>
    private static async Task<string> MintAsync(ForumFixture forum, HttpClient client, string path, DpopClient dpop, params string[] spellings)
    {
        var seen = new List<string>();
        foreach (var spelling in spellings)
        {
            var reply = await TokenAsync(client, path, dpop, spelling, spelling, forum.Now, Ct);
            if (AccessToken(reply) is { } token) return token;
            seen.Add($"{spelling}: {reply.Says}");
        }

        throw new Xunit.Sdk.XunitException("no spelling of the token endpoint was honoured, so the rows below could not be run: " + string.Join("; ", seen));
    }

    /// <summary>
    /// ORG7. A Forum named <c>https://forum.test</c> and served over <c>http</c>, as a TLS-terminating front serves it, is addressed as <c>https://forum.test</c>: credentials made for that are honoured on the
    /// token endpoint, a resource route and enrollment; credentials made for <c>http://forum.test</c>, the scheme the request arrived in, are refused. Red on the tree, which reads the scheme from the request.
    /// This is the probe the enrollment stage's FEPP5c survivor lacked.
    /// </summary>
    [Fact]
    public async Task R5_26_ORG7_TheSchemeIsTheOriginsAndNotTheRequests()
    {
        var failures = new List<string>();
        var client = https.Client;
        const string Own = "https://forum.test";
        const string Request = "http://forum.test";

        var (_, own) = await EnrolledAsync(https, "org7", Ct);
        var ownToken = await TokenAsync(client, "/oauth/token", own, Own + "/oauth/token", Own + "/oauth/token", https.Now, Ct);
        Expect(failures, ownToken.Status == 200, $"token endpoint, credentials for {Own}: expected 200, the Forum answered {ownToken.Says}");
        var (_, other) = await EnrolledAsync(https, "org7b", Ct);
        var requestToken = await TokenAsync(client, "/oauth/token", other, Request + "/oauth/token", Request + "/oauth/token", https.Now, Ct);
        Expect(failures, requestToken.Status != 200 && !requestToken.IsHostRefusal, $"token endpoint, credentials for {Request}: expected the binding's refusal, the Forum answered {requestToken.Says}");

        var (_, dpop) = await EnrolledAsync(https, "org7r", Ct);
        var token = await MintAsync(https, client, "/oauth/token", dpop, Own + "/oauth/token", Request + "/oauth/token");
        var resourceOwn = await GetAsync(client, "/v1/inbox", dpop, Own + "/v1/inbox", token, https.Now, Ct);
        Expect(failures, resourceOwn.Status == 200, $"resource route, proof for {Own}: expected 200, the Forum answered {resourceOwn.Says}");
        var resourceRequest = await GetAsync(client, "/v1/inbox", dpop, Request + "/v1/inbox", token, https.Now, Ct);
        Expect(failures, resourceRequest.Status == 401 && resourceRequest.Type == "curia/authn/url-mismatch", $"resource route, proof for {Request}: expected 401 curia/authn/url-mismatch, the Forum answered {resourceRequest.Says}");

        var enrolOwn = await EnrolAsync(client, EnrollProofProbe.NewAgent("org7-enrol"), Own + "/v1/agents", https.Now, Ct);
        Expect(failures, enrolOwn.Status == 201, $"enrollment, audience {Own}: expected 201, the Forum answered {enrolOwn.Says}");
        var enrolRequest = await EnrolAsync(client, EnrollProofProbe.NewAgent("org7-enrol-b"), Request + "/v1/agents", https.Now, Ct);
        Expect(failures, enrolRequest.Status == 400 && enrolRequest.Type == "curia/enroll/proof-mismatch" && enrolRequest.Detail == "claim=aud", $"enrollment, audience {Request}: expected 400 proof-mismatch claim=aud, the Forum answered {enrolRequest.Says}");
        NoFailures(failures, "R5.26: the scheme is the configured origin's, never the request's");
    }

    /// <summary>
    /// ORG8, a path base. No setting gives the Forum one (T0-5), so a test host that mounts it under <c>/forum</c> in code is still answered by the origin's name and the path the Forum reads: a credential for
    /// <c>origin/oauth/token</c> is honoured at <c>/forum/oauth/token</c> and one for <c>origin/forum/oauth/token</c> is refused (non-property (f)); likewise a resource route and enrollment. Red on the tree,
    /// which builds the URL from the request's path base and so does the reverse (T0-5).
    /// </summary>
    [Fact]
    public async Task R5_26_ORG8_ThePathBaseIsNotPartOfTheName()
    {
        var failures = new List<string>();
        var client = pathBase.Client;
        const string Own = "http://localhost";
        const string Prefix = PathBaseForumFixture.Prefix;

        var (_, own) = await EnrolledAsync(pathBase, "org8", Ct);
        var ownToken = await TokenAsync(client, Prefix + "/oauth/token", own, Own + "/oauth/token", Own + "/oauth/token", pathBase.Now, Ct);
        Expect(failures, ownToken.Status == 200, $"token endpoint at {Prefix}, credentials for {Own}/oauth/token: expected 200, the Forum answered {ownToken.Says}");
        var (_, other) = await EnrolledAsync(pathBase, "org8b", Ct);
        var prefixedToken = await TokenAsync(client, Prefix + "/oauth/token", other, Own + Prefix + "/oauth/token", Own + Prefix + "/oauth/token", pathBase.Now, Ct);
        Expect(failures, prefixedToken.Status != 200 && !prefixedToken.IsHostRefusal, $"token endpoint at {Prefix}, credentials for {Own}{Prefix}/oauth/token: expected the binding's refusal, the Forum answered {prefixedToken.Says}");

        var (_, dpop) = await EnrolledAsync(pathBase, "org8r", Ct);
        var token = await MintAsync(pathBase, client, Prefix + "/oauth/token", dpop, Own + "/oauth/token", Own + Prefix + "/oauth/token");
        var resourceOwn = await GetAsync(client, Prefix + "/v1/inbox", dpop, Own + "/v1/inbox", token, pathBase.Now, Ct);
        Expect(failures, resourceOwn.Status == 200, $"resource route at {Prefix}, proof for {Own}/v1/inbox: expected 200, the Forum answered {resourceOwn.Says}");
        var resourcePrefixed = await GetAsync(client, Prefix + "/v1/inbox", dpop, Own + Prefix + "/v1/inbox", token, pathBase.Now, Ct);
        Expect(failures, resourcePrefixed.Status == 401 && resourcePrefixed.Type == "curia/authn/url-mismatch", $"resource route at {Prefix}, proof for {Own}{Prefix}/v1/inbox: expected 401 curia/authn/url-mismatch, the Forum answered {resourcePrefixed.Says}");

        var enrolOwn = await EnrolAsync(client, EnrollProofProbe.NewAgent("org8-enrol"), Own + "/v1/agents", pathBase.Now, Ct, Prefix);
        Expect(failures, enrolOwn.Status == 201, $"enrollment at {Prefix}, audience {Own}/v1/agents: expected 201, the Forum answered {enrolOwn.Says}");
        var enrolPrefixed = await EnrolAsync(client, EnrollProofProbe.NewAgent("org8-enrol-b"), Own + Prefix + "/v1/agents", pathBase.Now, Ct, Prefix);
        Expect(failures, enrolPrefixed.Status == 400 && enrolPrefixed.Type == "curia/enroll/proof-mismatch" && enrolPrefixed.Detail == "claim=aud", $"enrollment at {Prefix}, audience {Own}{Prefix}/v1/agents: expected 400 proof-mismatch claim=aud, the Forum answered {enrolPrefixed.Says}");
        NoFailures(failures, "R5.26: a path base is not part of the Forum's name");
    }

    /// <summary>ORG9(i): the framework's own door (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c>, as a setting) with <c>X-Forwarded-Proto: https</c>: every route answers as without the header. Red on the tree, which got <c>https</c> from it (T0-5).</summary>
    [Fact]
    public Task R5_26_ORG9i_TheFrameworksForwardedHeadersDoorMovesNothing() =>
        ForwardedAsync(door, "i", [("X-Forwarded-Proto", "https")], ["https://localhost"]);

    /// <summary>
    /// ORG9(ii): <c>ForwardedHeaders</c> configured in code with <c>XForwardedHost</c> and <c>XForwardedProto</c>, and a request with <c>X-Forwarded-Host: another.example</c> and <c>X-Forwarded-Proto: https</c>: every route
    /// answers as without them, a credential for the name the header gives is refused, and the Host refusal reads the header as received and is not moved either. Red on the tree. Without this form the host half of
    /// the plan's ORG9 would have been vacuous, since the default door never applies <c>X-Forwarded-Host</c> (amendment 1, #9).
    /// </summary>
    [Fact]
    public Task R5_26_ORG9ii_AForwardedHostMovesNothing() =>
        ForwardedAsync(forwarded, "ii", [("X-Forwarded-Proto", "https"), ("X-Forwarded-Host", "another.example")], ["https://another.example", "http://another.example"]);

    private static async Task ForwardedAsync(ForumFixture forum, string form, (string Name, string Value)[] headers, string[] foreignSpellings)
    {
        const string Own = "http://localhost";
        var failures = new List<string>();
        using var plain = forum.CreateClient();
        using var client = forum.CreateClient();
        foreach (var (name, value) in headers) client.DefaultRequestHeaders.Add(name, value);

        var (_, dpop) = await EnrolledAsync(forum, "org9" + form, Ct);
        var token = await MintAsync(forum, plain, "/oauth/token", dpop, Own + "/oauth/token");

        var minted = await TokenAsync(client, "/oauth/token", dpop, Own + "/oauth/token", Own + "/oauth/token", forum.Now, Ct);
        Expect(failures, minted.Status == 200, $"token endpoint, credentials for {Own}, forwarded headers present: expected 200, the Forum answered {minted.Says}");
        foreach (var spelling in foreignSpellings)
        {
            var refused = await TokenAsync(client, "/oauth/token", dpop, spelling + "/oauth/token", spelling + "/oauth/token", forum.Now, Ct);
            Expect(failures, refused.Status != 200 && !refused.IsHostRefusal, $"token endpoint, credentials for {spelling} (what the forwarded headers say): expected the binding's refusal, the Forum answered {refused.Says}");
        }

        var resource = await GetAsync(client, "/v1/inbox", dpop, Own + "/v1/inbox", token, forum.Now, Ct);
        Expect(failures, resource.Status == 200, $"resource route, proof for {Own}, forwarded headers present: expected 200, the Forum answered {resource.Says}");
        var enrol = await EnrolAsync(client, EnrollProofProbe.NewAgent("org9" + form + "-enrol"), Own + "/v1/agents", forum.Now, Ct);
        Expect(failures, enrol.Status == 201, $"enrollment, audience {Own}/v1/agents, forwarded headers present: expected 201, the Forum answered {enrol.Says}");

        var health = await ReadAsync(await client.GetAsync("/health", Ct), Ct);
        Expect(failures, health.Status == 200, $"/health with forwarded headers present: expected 200, the Forum answered {health.Says}");
        NoFailures(failures, $"R5.26: no forwarded header is read for the Forum's name (form {form})");
    }
}

/// <summary>
/// ORG20 (amendment 1, #5): an origin that is an internationalized name in its A-label form, on a real socket, which the framework hands the code <b>decoded</b>. Every other fixture and every other probe is
/// ASCII, so a Host comparison made through <c>Request.Host</c> would pass the whole suite and refuse every request of such a Forum; this is the one probe that fails it (FORG23).
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IdnOriginTests(IdnOriginKestrelFixture forum) : IClassFixture<IdnOriginKestrelFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The own-spelling rows, under each admitted spelling of the <c>Host</c> (the origin's, and its capitals): the token endpoint, a resource route, enrollment, <c>/health</c> and the contract URL.</summary>
    [Fact]
    public async Task R5_27_ORG20_AnAlabelOriginIsServedUnderItsOwnSpellingAndItsCaseVariant()
    {
        var own = forum.PublicOrigin;
        var failures = new List<string>();
        foreach (var host in new[] { forum.Authority, forum.Authority.ToUpperInvariant() })
        {
            using var client = ClientWith(forum, host);
            var (agent, dpop) = await EnrolledAsync(forum, "org20", Ct);
            var token = await TokenAsync(client, "/oauth/token", dpop, own + "/oauth/token", own + "/oauth/token", forum.Now, Ct);
            Expect(failures, token.Status == 200, $"Host {host}, token endpoint: expected 200, the Forum answered {token.Says}");

            var minted = AccessToken(await TokenAsync(forum.Client, "/oauth/token", dpop, own + "/oauth/token", own + "/oauth/token", forum.Now, Ct));
            var resource = minted is null ? null : await GetAsync(client, "/v1/inbox", dpop, own + "/v1/inbox", minted, forum.Now, Ct);
            Expect(failures, resource?.Status == 200, $"Host {host}, resource route: expected 200, the Forum answered {resource?.Says ?? "no token to present"}");

            var enrol = await EnrolAsync(client, EnrollProofProbe.NewAgent("org20-enrol"), own + "/v1/agents", forum.Now, Ct);
            Expect(failures, enrol.Status == 201, $"Host {host}, enrollment: expected 201, the Forum answered {enrol.Says}");

            var health = await ReadAsync(await client.GetAsync("/health", Ct), Ct);
            Expect(failures, health.Status == 200, $"Host {host}, /health: expected 200, the Forum answered {health.Says}");

            if (minted is not null)
            {
                var (id, status) = await PostQuestionAsync(forum, agent, dpop, own, minted);
                var read = id is null ? null : await ReadAsync(await client.GetAsync("/v1/posts/" + id, Ct), Ct);
                var served = read is { Status: 200 } ? ReaderContractOf(read) : null;
                Expect(failures, served == own + "/.well-known/reader-contract/v1", $"Host {host}, contract URL: expected {own}/.well-known/reader-contract/v1, served {served ?? read?.Says ?? $"no post ({status})"}");
            }
        }

        NoFailures(failures, "R5.27: the comparison reads the header as received, so an A-label origin is served");
    }

    private static async Task<(string? Id, int Status)> PostQuestionAsync(IdnOriginKestrelFixture forum, ForumAgent agent, DpopClient dpop, string origin, string token)
    {
        var wire = agent.SignQuestion("org20-" + Guid.NewGuid().ToString("N")[..8], "A question under an internationalized name.", "IDN origin", forum.Now);
        using var client = forum.Client;
        using var response = await dpop.PostAsync(client, origin + "/v1/posts", token, wire, forum.Now, Ct);
        var id = response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["post_id"]!.GetValue<string>() : null;
        return (id, (int)response.StatusCode);
    }

    /// <summary>
    /// The same host named as a U-label (raw UTF-8 bytes) and an invalid A-label: <b>never admitted and never a 5xx</b>. The U-label is refused with a <c>400</c>, by Kestrel or by the Forum. The invalid A-label
    /// (<c>xn--</c>) is refused <b>before any Forum code runs</b> (Task 3's measurement): the hosting layer's request-start logging formats <c>Request.Host</c>, which throws on the IDN decode and closes the
    /// connection with no response. So this row asserts that the request is never admitted: the connection closes with no status line, or the answer is a <c>400</c>, and it is never a 2xx or a 5xx. It is
    /// a non-property of the Forum (the refusal is the framework's, not R5.27's), recorded by Task 8.
    /// </summary>
    [Fact]
    public async Task R5_27_ORG20_ANameTheOriginDoesNotSpellIsNeverAdmittedAndNeverAServerFault()
    {
        var address = forum.Client.BaseAddress!;
        var port = address.Port;
        var failures = new List<string>();
        var rows = new (string Name, byte[] Host)[]
        {
            ("U-label as UTF-8 bytes", Encoding.UTF8.GetBytes($"bücher.example:{port}")),
            ("invalid punycode", Encoding.ASCII.GetBytes($"xn--:{port}")),
        };
        foreach (var (name, host) in rows)
        {
            byte[] request = [.. Encoding.ASCII.GetBytes("GET /health HTTP/1.1\r\nHost: "), .. host, .. Encoding.ASCII.GetBytes("\r\nConnection: close\r\n\r\n")];
            var reply = await RawAsync(address, request, Ct);
            var closedUnanswered = name == "invalid punycode" && reply.Status == 0;
            Expect(failures, reply.Status == 400 || closedUnanswered, $"{name}: expected 400 (Kestrel's or the Forum's), or for an invalid A-label a closed connection with no response, the Forum answered {reply.Says}");
        }

        NoFailures(failures, "R5.27: an internationalized spelling the origin does not use is refused, not mapped");
    }
}

/// <summary>ORG20's TestServer row: the same A-label origin served in process, which records what the in-memory server does with the decode (Task 0 measured Kestrel only).</summary>
public sealed class IdnOriginTestServerFixture : ForumFixture
{
    internal override string PublicOrigin => "http://xn--bcher-kva.example";

    protected override void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Host = "xn--bcher-kva.example";
    }
}

/// <summary>ORG20, the in-process row.</summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class IdnOriginTestServerTests(IdnOriginTestServerFixture forum) : IClassFixture<IdnOriginTestServerFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task R5_27_ORG20_AnAlabelOriginIsServedInProcessToo()
    {
        const string Own = "http://xn--bcher-kva.example";
        var (_, dpop) = await EnrolledAsync(forum, "org20ts", Ct);
        var token = await TokenAsync(forum.Client, "/oauth/token", dpop, Own + "/oauth/token", Own + "/oauth/token", forum.Now, Ct);
        var health = await ReadAsync(await forum.Client.GetAsync("/health", Ct), Ct);
        var failures = new List<string>();
        Expect(failures, token.Status == 200, $"token endpoint: expected 200, the Forum answered {token.Says}");
        Expect(failures, health.Status == 200, $"/health: expected 200, the Forum answered {health.Says}");
        NoFailures(failures, "R5.27: an A-label origin is served in process (what TestServer does with the decode is recorded by this row)");
    }

    /// <summary>
    /// The origin's host spelled as a U-label (<c>bücher.example</c>), in process, where the header reaches the pipeline as written: <b>refused by the Forum</b>, because the comparison reads the header as received
    /// and does not map the U-label to the origin's A-label (R5.27, non-property (i)). Added by Task 6: FORG24 (the comparison through <c>HostString.ToUriComponent()</c>, which re-encodes the U-label) was killed
    /// by the fence alone, and ORG9(ii) stays green under it because the check runs outside a forwarding middleware.
    /// </summary>
    [Fact]
    public async Task R5_27_ORG20_AUnicodeSpellingOfTheOriginsHostIsRefusedNotMapped()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.TryAddWithoutValidation("Host", "b\u00FCcher.example");
        var reply = await ReadAsync(await forum.Client.SendAsync(request, Ct), Ct);
        Assert.True(reply.IsHostRefusal, $"a U-label spelling of the origin's host: expected the Forum's refusal (400 host-not-served), the Forum answered {reply.Says}");
    }
}
