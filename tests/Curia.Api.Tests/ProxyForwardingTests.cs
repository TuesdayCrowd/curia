using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Text.Json;
using Curia.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// A Forum on a real socket named <c>http://forum.example:&lt;port&gt;</c> (a port-bearing origin: amendment C7, because a default-port origin would not show <c>$host</c>'s harm; TLS is not modelled at all), that a
/// <see cref="RecipeForwarder"/> stands in front of. The forwarder listens on the port the origin names; its clients dial it over plain HTTP with <c>Host: forum.example:&lt;port&gt;</c>. The fixture also counts what reaches the Forum, by path, so that a limit's effect is read from the Forum's side.
/// </summary>
public sealed class ForwardedForumFixture : ForumFixture
{
    private readonly int _forwarderPort = FreePort();
    private RecipeForwarder? _forwarder;

    public ForwardedForumFixture() => UseReservedKestrelPort(port =>
    {
        ForumPort = port;
        return $"http://forum.example:{_forwarderPort}";
    });

    internal int ForumPort { get; private set; }

    /// <summary>R11.112 (errata G85): a Forum that holds a public origin runs Production, the shape the hosted Forum runs; Development with it is refused at start.</summary>
    protected override string? HostingEnvironment => "Production";

    /// <summary>
    /// The fixture's own client reaches the Forum behind the forwarder directly, and addresses it by the origin's name, as every fixture's client
    /// does (R5.26, ORG15): the <c>Host</c> it sends is the origin's authority, not the loopback socket it dials.
    /// </summary>
    internal override HttpClient Client
    {
        get
        {
            var client = base.Client;
            client.DefaultRequestHeaders.Host = Authority;
            return client;
        }
    }

    internal string Authority => $"forum.example:{_forwarderPort}";

    internal ConcurrentDictionary<string, int> Reached { get; } = new(StringComparer.Ordinal);

    internal RecipeForwarder Forwarder => _forwarder ?? throw new InvalidOperationException("no forwarder is running");

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>A forwarder from the recipe as it stands, replacing the one running (a test owns the forwarder it starts and the next test starts its own).</summary>
    internal async Task<RecipeForwarder> ForwardAsync(RecipeForwarder.Options? options = null)
    {
        _ = Client; // the host starts on first use, and the forwarder's upstream must be listening
        if (_forwarder is not null) await _forwarder.DisposeAsync().ConfigureAwait(false);
        Reached.Clear();
        _forwarder = await RecipeForwarder.StartAsync(_forwarderPort, ForumPort, options).ConfigureAwait(false);
        return _forwarder;
    }

    /// <summary>A client of the forwarder, as an agent behind the origin's name would be.</summary>
    internal HttpClient ClientOfForwarder()
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_forwarderPort}") };
        client.DefaultRequestHeaders.Host = Authority;
        ForumAgent.RecordOrigin(client, PublicOrigin);
        ForumAgent.RecordClock(client, Clock);
        return client;
    }

    internal int ForwarderPort => _forwarderPort;

    protected override void ConfigureStore(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IStartupFilter>(new CountingFilter(Reached));
    }

    public override async ValueTask DisposeAsync()
    {
        if (_forwarder is not null) await _forwarder.DisposeAsync();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private sealed class CountingFilter(ConcurrentDictionary<string, int> reached) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, n) =>
            {
                reached.AddOrUpdate(ctx.Request.Path.Value ?? "/", 1, (_, c) => c + 1);
                await n();
            });
            next(app);
        };
    }
}

/// <summary>
/// PXY1 to PXY7 (errata G83, R7.33): the recipe as a Forum meets it, through the in-process model of it (Tier A). <b>What this cannot show</b> is that nginx honours the stanza as the forwarder reads it;
/// the live run's real nginx is for that, and G83's Status says which ran where. The forwarder takes every figure from the recipe, and these probes hold the recipe to G83's table (read from the errata),
/// so a recipe that is empty, raised past the table or missing a directive turns a probe red by its own assertion.
/// </summary>
[SuppressMessage("Naming", "CA1707:Identifiers should not contain underscores", Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Each client is owned by its test, which lives as long as the process.")]
public sealed class ProxyForwardingTests(ForwardedForumFixture forum) : IClassFixture<ForwardedForumFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The operator's allow file as a test writes it (never the shipped one, which lists nothing): the forwarder's clients dial from the loopback address.</summary>
    private const string ListedLoopback = "allow 127.0.0.1;";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static string Words() => string.Join(' ', Enumerable.Range(0, 8).Select(_ => Guid.NewGuid().ToString("N")[..10]));

    private static async Task<(HttpStatusCode Status, string? Type, string? Error, string? ContentType)> GetAsync(HttpClient client, string path, string? host = null, Dictionary<string, string>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (host is not null) request.Headers.Host = host;
        foreach (var (k, v) in headers ?? []) request.Headers.TryAddWithoutValidation(k, v);
        using var response = await client.SendAsync(request, Ct);
        return Parse(response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response.Content.Headers.ContentType?.MediaType);
    }

    private static (HttpStatusCode, string?, string?, string?) Parse(HttpStatusCode status, string body, string? contentType)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return (status, root.TryGetProperty("type", out var t) ? t.GetString() : null, root.TryGetProperty("error", out var e) ? e.GetString() : null, contentType);
        }
        catch (JsonException)
        {
            return (status, "(not JSON) " + body[..Math.Min(120, body.Length)], null, contentType);
        }
    }

    /// <summary>PXY1: a full enrolment, a token and a post made through the forwarder succeed under the origin's name: the end-to-end the recipe exists for.</summary>
    [Fact]
    public async Task G83_PXY1_AnAgentEnrolsAuthenticatesAndPostsThroughTheEdge()
    {
        await forum.ForwardAsync(new RecipeForwarder.Options(EnrolmentAllow: ListedLoopback));
        using var client = forum.ClientOfForwarder();
        var agent = ForumAgent.Create($"https://agents.example/pxy-{Suffix()}", $"k-{Suffix()}");
        var tokenEndpoint = forum.PublicOrigin + "/oauth/token";
        string? failure = null;
        HttpStatusCode status = default;
        try
        {
            var (dpop, token) = await agent.AuthenticateAsync(client, tokenEndpoint, forum.Now, Ct);
            var wire = agent.SignQuestion("pxy-board-" + Suffix(), Words(), "About " + Words(), forum.Now);
            using var response = await dpop.PostAsync(client, forum.PublicOrigin + "/v1/posts", token, wire, forum.Now, Ct);
            status = response.StatusCode;
        }
        catch (Exception e) when (e is Xunit.Sdk.XunitException or InvalidOperationException or HttpRequestException)
        {
            failure = e.GetType().Name + ": " + e.Message.Split('\n')[0];
        }

        Assert.True(failure is null && status == HttpStatusCode.Created, $"through the edge an agent should enrol, authenticate and post (201); got {(failure ?? status.ToString())}. The recipe's Host line decides this: nginx with none sends the upstream's own authority, which the Forum refuses (R5.27).");
    }

    /// <summary>PXY2: a forwarder that rewrites the <c>Host</c> is answered <c>400 host-not-served</c>: the failure the runbook warns of, reproduced.</summary>
    [Fact]
    public async Task G83_PXY2_ARewrittenHostIsRefusedByTheForum()
    {
        await forum.ForwardAsync(new RecipeForwarder.Options(RewriteHost: true));
        using var client = forum.ClientOfForwarder();
        var (status, type, _, _) = await GetAsync(client, "/health");
        Assert.True(status == HttpStatusCode.BadRequest && type == "curia/request/host-not-served", $"a forwarder that rewrites Host should be answered 400 curia/request/host-not-served; got {(int)status} {type}");
    }

    /// <summary>PXY3: a client-supplied <c>X-Forwarded-Host</c> and <c>-Proto</c> change nothing: the Forum answers and names itself as without them.</summary>
    [Fact]
    public async Task G83_PXY3_AClientsForwardedHeadersChangeNothing()
    {
        await forum.ForwardAsync();
        using var client = forum.ClientOfForwarder();
        var forged = new Dictionary<string, string> { ["X-Forwarded-Host"] = "another.example", ["X-Forwarded-Proto"] = "http" };
        var plain = await GetAsync(client, "/health");
        var with = await GetAsync(client, "/health", headers: forged);
        using var discovery = await client.GetAsync("/.well-known/oauth-authorization-server", Ct);
        var discoveryText = await discovery.Content.ReadAsStringAsync(Ct);
        string? issuer = null;
        try
        {
            using var doc = JsonDocument.Parse(discoveryText);
            issuer = doc.RootElement.TryGetProperty("issuer", out var i) ? i.GetString() : null;
        }
        catch (JsonException)
        {
            issuer = $"(not a document: {(int)discovery.StatusCode} {discoveryText[..Math.Min(80, discoveryText.Length)]})";
        }

        Assert.True(plain.Status == HttpStatusCode.OK && with.Status == HttpStatusCode.OK && issuer == forum.PublicOrigin,
            $"/health answered {(int)plain.Status} without and {(int)with.Status} with forged forwarded headers, and the issuer is {issuer} (the origin is {forum.PublicOrigin})");
    }

    private async Task<(int Refused, int Sent, int ReachedForum, double Seconds)> BurstAsync(HttpClient client, HttpMethod method, string path, int count)
    {
        forum.Reached.Clear();
        var refused = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var n = 0; n < count; n++)
        {
            using var request = new HttpRequestMessage(method, path);
            if (method == HttpMethod.Post) request.Content = new FormUrlEncodedContent([]);
            using var response = await client.SendAsync(request, Ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) refused++;
        }

        return (refused, count, forum.Reached.GetValueOrDefault(path), watch.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// PXY4: sixty requests to each limited route reach the Forum no more than G83's burst (plus the one a bucket admits past it and what the rate refills in the time taken), and the rest are
    /// <c>429</c>. The evidence is the Forum's own count of what it was asked, read from its side.
    /// </summary>
    [Fact]
    public async Task G83_PXY4_TheBurstIsRefusedAtTheTablesNumbers()
    {
        await forum.ForwardAsync(new RecipeForwarder.Options(EnrolmentAllow: ListedLoopback));
        using var client = forum.ClientOfForwarder();
        var problems = new List<string>();
        foreach (var zone in PublishedEdge.Zones())
        {
            var method = zone.Route.StartsWith("GET", StringComparison.Ordinal) || zone.Name == "curia_nonce" ? HttpMethod.Get : HttpMethod.Post;
            var path = zone.Route.Split('?')[0];
            var r = await BurstAsync(client, method, path, 60);
            var allowed = zone.Burst + 1 + (int)Math.Ceiling(zone.RatePerSecond * r.Seconds) + 1;
            if (r.Refused == 0) problems.Add($"{zone.Name}: no request of {r.Sent} was refused 429");
            if (r.ReachedForum > allowed) problems.Add($"{zone.Name}: {r.ReachedForum} requests reached the Forum, more than the table's burst {zone.Burst} and rate {zone.RatePerSecond}/s allow ({allowed}) in {r.Seconds:0.00} s");
            if (r.Refused + r.ReachedForum != r.Sent) problems.Add($"{zone.Name}: {r.Refused} refused and {r.ReachedForum} reached the Forum do not make {r.Sent}");
        }

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>PXY5, the red twin of PXY4: the same burst with the limit absent reaches the Forum whole, so that PXY4's count is a measurement and not a number that always looks small.</summary>
    [Fact]
    public async Task G83_PXY5_WithNoBucketTheBurstReachesTheForum()
    {
        await forum.ForwardAsync(new RecipeForwarder.Options(DropBucket: true));
        using var client = forum.ClientOfForwarder();
        var r = await BurstAsync(client, HttpMethod.Get, "/v1/enroll/nonce", 60);
        Assert.True(r.Refused == 0 && r.ReachedForum == 60, $"with no bucket all 60 requests should reach the Forum; {r.ReachedForum} did and {r.Refused} were refused");
    }

    /// <summary>
    /// PXY6: a refused request is answered in a body the client's reader can read. The nonce route's <c>429</c> is a problem document of type <c>curia/edge/rate-limited</c>, the token route's an RFC 6749 object,
    /// and a body past R6.39's size is <c>413</c> as a problem document of type <c>curia/request/too-large</c> (the Forum's own type), each with its content type.
    /// </summary>
    [Fact]
    public async Task G83_PXY6_ARefusalIsAReadableBody()
    {
        await forum.ForwardAsync();
        using var client = forum.ClientOfForwarder();
        var problems = new List<string>();
        (HttpStatusCode, string?, string?, string?) last = default;
        for (var n = 0; n < 40; n++)
        {
            using var response = await client.GetAsync("/v1/enroll/nonce", Ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests) continue;
            last = Parse(response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response.Content.Headers.ContentType?.MediaType);
            break;
        }

        if (last.Item1 != HttpStatusCode.TooManyRequests) problems.Add("the nonce route never answered 429");
        else if (last.Item4 != "application/problem+json" || last.Item2 != "curia/edge/rate-limited") problems.Add($"the nonce route's 429 is {last.Item4} type {last.Item2}, not application/problem+json curia/edge/rate-limited");

        last = default;
        for (var n = 0; n < 40; n++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = new FormUrlEncodedContent([]) };
            using var response = await client.SendAsync(request, Ct);
            if (response.StatusCode != HttpStatusCode.TooManyRequests) continue;
            last = Parse(response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response.Content.Headers.ContentType?.MediaType);
            break;
        }

        if (last.Item1 != HttpStatusCode.TooManyRequests) problems.Add("the token route never answered 429");
        else if (last.Item3 != "temporarily_unavailable") problems.Add($"the token route's 429 is {last.Item4} error {last.Item3}, not RFC 6749's temporarily_unavailable");

        var tooLarge = await RawHttp.PostAsync(forum.ForwarderPort, forum.Authority, "/v1/agents", PublishedEdge.SubmissionCapBytes() + 1, 1024, "application/json", Ct);
        var parsed = Parse((HttpStatusCode)tooLarge.Status, tooLarge.Body, tooLarge.Headers.GetValueOrDefault("Content-Type")?.Split(';')[0]);
        if (tooLarge.Status != 413 || parsed.Item4 != "application/problem+json" || parsed.Item2 != "curia/request/too-large")
            problems.Add($"a body of R6.39's size plus one is answered {tooLarge.Status} {parsed.Item4} type {parsed.Item2}, not 413 application/problem+json curia/request/too-large");

        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    /// <summary>PXY7: a foreign <c>Host</c> through the forwarder reaches the Forum and is refused by it: the edge does not pre-empt R5.27, so the readiness check R2 sees the Forum's own refusal.</summary>
    [Fact]
    public async Task G83_PXY7_AForeignHostReachesTheForumAndIsRefusedThere()
    {
        await forum.ForwardAsync();
        using var client = forum.ClientOfForwarder();
        var (status, type, _, _) = await GetAsync(client, "/health", host: "not-this-forum.invalid");
        Assert.True(status == HttpStatusCode.BadRequest && type == "curia/request/host-not-served" && forum.Forwarder.Passed.GetValueOrDefault("/health") == 1,
            $"a foreign Host should be passed on and refused by the Forum (400 curia/request/host-not-served, and /health passed once); got {(int)status} {type}, passed {forum.Forwarder.Passed.GetValueOrDefault("/health")}");
    }
    private static string WellFormedEnrolment()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        return JsonSerializer.Serialize(new { agent_id = "https://agents.example/pxy-edge", alg = "ES256", kid = "pxy-edge", public_key = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) });
    }

    private static async Task<(HttpStatusCode Status, string? Type, string? ContentType)> PostEnrolmentAsync(HttpClient client, string path, string? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body ?? WellFormedEnrolment(), System.Text.Encoding.UTF8, "application/json") };
        using var response = await client.SendAsync(request, Ct);
        var parsed = Parse(response.StatusCode, await response.Content.ReadAsStringAsync(Ct), response.Content.Headers.ContentType?.MediaType);
        return (parsed.Item1, parsed.Item2, parsed.Item4);
    }

    /// <summary>
    /// G86_PXY8 (R7.33 (rev.) (d), under the model): a request to the enrolment route from an address the shipped allow file does not list is the edge's <c>403</c> problem document of the type the errata names (read from the errata),
    /// and the Forum is <b>not reached</b>: the Forum's own count of what it was asked is the oracle, as PXY4's is.
    /// </summary>
    [Fact]
    public async Task G86_PXY8_AnUnlistedAddressIsRefusedByTheEdgeAndTheForumIsNotReached()
    {
        await forum.ForwardAsync();
        using var client = forum.ClientOfForwarder();
        var (status, type, contentType) = await PostEnrolmentAsync(client, "/v1/agents");
        var reached = forum.Reached.GetValueOrDefault("/v1/agents");
        Assert.True(status == HttpStatusCode.Forbidden && type == PublishedEdge.EnrolmentClosedType() && contentType == "application/problem+json" && reached == 0,
            $"an unlisted address should be answered 403 {PublishedEdge.EnrolmentClosedType()} as application/problem+json with the Forum not reached; got {(int)status} {type} {contentType}, the Forum was asked {reached} times");
    }

    /// <summary>G86_PXY9: a listed address reaches the Forum, and a well-formed body with no proof is the Forum's own <c>400 curia/enroll/proof-required</c> (the allow file is the test's, never the shipped one).</summary>
    [Fact]
    public async Task G86_PXY9_AListedAddressReachesTheForumsOwnRefusal()
    {
        await forum.ForwardAsync(new RecipeForwarder.Options(EnrolmentAllow: ListedLoopback));
        using var client = forum.ClientOfForwarder();
        var (status, type, _) = await PostEnrolmentAsync(client, "/v1/agents");
        var reached = forum.Reached.GetValueOrDefault("/v1/agents");
        Assert.True(status == HttpStatusCode.BadRequest && type == "curia/enroll/proof-required" && reached == 1, $"a listed address should reach the Forum's own refusal (400 curia/enroll/proof-required, asked once); got {(int)status} {type}, asked {reached} times");
    }

    /// <summary>
    /// G86_PXY10: every spelling of the route the Forum accepts when it is sent to it directly (a status other than <c>404</c> and <c>405</c> to a <c>POST</c>; the 17 Task 0 measured, plus the ones that are not) is the edge's <c>403</c> through the
    /// forwarder and does not reach the Forum. <b>Under the model this restates the pattern's flag</b>: the differential through a real nginx is LCBE2, and this row does not count as that.
    /// </summary>
    [Fact]
    public async Task G86_PXY10_EverySpellingTheForumAcceptsIsRefusedByTheEdge()
    {
        var spellings = new[]
        {
            "/v1/agents", "/V1/agents", "/v1/Agents", "/V1/AGENTS", "/v1/AGENTS", "/v1/agents/", "/V1/Agents/", "/v1/agents//", "//v1/agents", "/v1//agents", "/v1/./agents", "/v1/x/../agents", "/v1/%61gents", "/v1/%41gents",
            "/%76%31/agents", "/v1%2Fagents", "/v1/agents;a=b", "/v1/agents;", "/v1/agents?x", "/v1/agents?x=1&y=2", "/v1/agents#f", "/v1/agents%20", "/v1/agents.", "/v1/agents/.", "/v1/agents%2F", "/v1/agents/%2e", "/v1/agents%09",
            "/v1/%2561gents", "/v1/agents/x", "/v1/agents%c0%af",
        };
        await forum.ForwardAsync();
        var accepted = new List<string>();
        foreach (var path in spellings)
        {
            var direct = await RawHttp.PostAsync(forum.ForumPort, forum.Authority, path, 0, 0, "application/json", Ct);
            if (direct.Status is not (404 or 405)) accepted.Add(path);
        }

        Assert.True(accepted.Count >= 9, $"the Forum should accept at least the case and slash variants directly; it accepted {accepted.Count}: {string.Join(' ', accepted)}");
        var open = new List<string>();
        foreach (var path in accepted)
        {
            forum.Reached.Clear();
            var viaEdge = await RawHttp.PostAsync(forum.ForwarderPort, forum.Authority, path, 0, 0, "application/json", Ct);
            if (viaEdge.Status != 403 || !viaEdge.Body.Contains(PublishedEdge.EnrolmentClosedType(), StringComparison.Ordinal) || forum.Reached.Any(r => r.Key.Contains("agents", StringComparison.OrdinalIgnoreCase)))
                open.Add($"{path} -> {viaEdge.Status}");
            await Task.Delay(1100, Ct);
        }

        Assert.True(open.Count == 0, $"spellings the Forum accepts that the edge did not refuse with its 403: {string.Join("; ", open)}");
    }

    /// <summary>G86_PXY10b: every method on the route is closed, not only <c>POST</c>: a <c>GET</c> to the route from an unlisted address is the edge's <c>403</c> where the Forum would answer <c>405</c>.</summary>
    [Fact]
    public async Task G86_PXY10b_EveryMethodOnTheRouteIsClosed()
    {
        await forum.ForwardAsync();
        using var client = forum.ClientOfForwarder();
        var statuses = new List<string>();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Put, HttpMethod.Delete, HttpMethod.Options })
        {
            using var request = new HttpRequestMessage(method, "/V1/Agents/");
            using var response = await client.SendAsync(request, Ct);
            statuses.Add($"{method} {(int)response.StatusCode}");
        }

        Assert.True(statuses.All(s => s.EndsWith(" 403", StringComparison.Ordinal)), string.Join(", ", statuses));
    }

    /// <summary>G86_PXY11: the credentialed rotation route is not closed (the anchor keeps it out): <c>POST /v1/agents/x/keys</c> from an unlisted address reaches the Forum, which answers it.</summary>
    [Fact]
    public async Task G86_PXY11_TheRotationRouteIsNotClosed()
    {
        await forum.ForwardAsync();
        using var client = forum.ClientOfForwarder();
        var (status, type, _) = await PostEnrolmentAsync(client, "/v1/agents/x/keys", "{}");
        var reached = forum.Reached.GetValueOrDefault("/v1/agents/x/keys");
        Assert.True(type != PublishedEdge.EnrolmentClosedType() && reached == 1, $"the rotation route should reach the Forum (asked once) and not be the enrolment closure; got {(int)status} {type}, asked {reached} times");
    }

    /// <summary>G86_PXY12: the mechanism can be opened: an allow file whose only line is <c>allow all;</c> admits every address (the operator's bounded window).</summary>
    [Fact]
    public async Task G86_PXY12_AnAllowAllFileOpensTheRoute()
    {
        await forum.ForwardAsync(new RecipeForwarder.Options(EnrolmentAllow: "allow all;"));
        using var client = forum.ClientOfForwarder();
        var (status, type, _) = await PostEnrolmentAsync(client, "/V1/AGENTS");
        Assert.True(status == HttpStatusCode.BadRequest && type == "curia/enroll/proof-required", $"allow all should open the route in any spelling; got {(int)status} {type}");
    }

    /// <summary>
    /// G86_PXY13: the closed route is limited too (nginx's <c>limit_req</c> runs before the access check and counts a denied request): sixty rapid requests from an unlisted address are the edge's <c>403</c> up to the table's burst and
    /// then <c>429</c>, and the Forum is never asked. The opened route's burst at the table's numbers is PXY4's third row.
    /// </summary>
    [Fact]
    public async Task G86_PXY13_AFloodAtTheClosedRouteIsLimitedAndNeverReachesTheForum()
    {
        await forum.ForwardAsync();
        using var client = forum.ClientOfForwarder();
        var burst = PublishedEdge.Zones().Single(z => z.Name == "curia_enrol").Burst;
        var counts = new Dictionary<HttpStatusCode, int>();
        for (var n = 0; n < 60; n++)
        {
            var (status, _, _) = await PostEnrolmentAsync(client, "/v1/agents");
            counts[status] = counts.GetValueOrDefault(status) + 1;
        }

        var reached = forum.Reached.GetValueOrDefault("/v1/agents");
        Assert.True(counts.GetValueOrDefault(HttpStatusCode.Forbidden) >= burst && counts.GetValueOrDefault(HttpStatusCode.TooManyRequests) > 0 && reached == 0,
            $"sixty rapid requests should be 403 up to the burst ({burst}) and then 429 with the Forum not asked; got {string.Join(", ", counts.Select(c => $"{(int)c.Key}: {c.Value}"))}, asked {reached} times");
    }
}

