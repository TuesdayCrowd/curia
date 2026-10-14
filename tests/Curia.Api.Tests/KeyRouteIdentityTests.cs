using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>What the host handed the pipeline for the last request: the raw target and the URL built from the path.</summary>
internal sealed class RequestRecorder
{
    internal volatile string? RawTarget;
    internal volatile string? Url;

    internal void Clear()
    {
        RawTarget = null;
        Url = null;
    }
}

/// <summary>Records every request before routing, so a fact can compare what a real host read with what a client signed.</summary>
[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes", Justification = "Resolved from the container.")]
internal sealed class RecordingStartupFilter(RequestRecorder recorder) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, pipeline) =>
        {
            recorder.RawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
            var request = context.Request;
            recorder.Url = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}";
            await pipeline();
        });
        next(app);
    };
}

/// <summary>
/// The Forum on a real socket, behind the permissive lifecycle gate, with the request recorder. The
/// aliasing of <c>%2F</c> and <c>%252F</c> is Kestrel's: the in-memory test server hands the pipeline no
/// raw target, so these facts run here.
/// </summary>
public sealed class KeyRouteKestrelFixture : ForumFixture
{
    public KeyRouteKestrelFixture() => UseKestrel(0);

    internal RequestRecorder Recorder { get; } = new();

    protected override void ConfigureFuzz(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(Recorder);
        services.AddTransient<IStartupFilter, RecordingStartupFilter>();
    }
}

/// <summary>The Forum as <c>Program.cs</c> composes it (the guard on), on a real socket.</summary>
public sealed class KeyRouteGuardedKestrelFixture : ForumFixture
{
    public KeyRouteGuardedKestrelFixture() => UseKestrel(0);

    protected override bool ComposesPermissiveLifecycleGate => false;
}

/// <summary>
/// Which identity a key route names (R4.18, R4.39): the route's values are read from the raw request
/// target and decoded once, so an identity or a kid holding a literal percent escape is addressable, and
/// <c>%2F</c> and <c>%252F</c> name two identities although the framework's path cannot tell them apart.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class KeyRouteIdentityTests(KeyRouteKestrelFixture forum) : IClassFixture<KeyRouteKestrelFixture>
{
    private string Origin => forum.Client.BaseAddress!.GetLeftPart(UriPartial.Authority);

    private async Task<(ForumAgent Agent, DpopClient Dpop, string Token)> EnrolledAsync(HttpClient client, string agentId, CancellationToken ct)
    {
        var agent = ForumAgent.Create(agentId, "k-" + Guid.NewGuid().ToString("N")[..8]);
        var (dpop, token) = await agent.AuthenticateAsync(client, Origin + "/oauth/token", forum.Now, ct);
        return (agent, dpop, token);
    }

    private Task<HttpResponseMessage> RotateAsync(
        HttpClient client, ForumAgent agent, DpopClient dpop, string token, KeyActRequests.KeyPair next, long prev, CancellationToken ct) =>
        dpop.PostAsync(
            client, KeyActRequests.RotateUrl(agent.AgentId, Origin), token, KeyActRequests.RotationBody(agent, next, prev), forum.Now, ct,
            contentType: "application/json", htu: KeyActRequests.RotateHtu(agent.AgentId, Origin));

    private Task<HttpResponseMessage> RetireAsync(
        HttpClient client, string agentId, DpopClient dpop, string token, KeyActRequests.KeyPair victim, long prev, CancellationToken ct) =>
        dpop.PostAsync(
            client, KeyActRequests.RetireUrl(agentId, victim.Kid, Origin), token, KeyActRequests.RetirementBody(agentId, victim, prev), forum.Now, ct,
            contentType: "application/json", htu: KeyActRequests.RetireHtu(agentId, victim.Kid, Origin));

    /// <summary>
    /// An identity whose id holds a literal percent escape, a space escape or a lone percent sign rotates,
    /// then retires the key it rotated from. Red when a route value is decoded twice or <c>%2F</c> is
    /// restored from the path (the old <c>SlashesOf</c>): the token's subject then never equals the route's
    /// identity (F1-105, F1-106, F1-107).
    /// </summary>
    [Theory]
    [InlineData("a%2Fb")]
    [InlineData("a%20b")]
    [InlineData("a%b")]
    public async Task R4_18_AnIdentityWhoseIdHoldsALiteralPercentEscapeCanRotate(string stem)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await EnrolledAsync(client, KeyActRequests.Unique(stem), ct);
        var next = KeyActRequests.KeyPair.New("next-" + Guid.NewGuid().ToString("N")[..8]);

        using var rotated = await RotateAsync(client, agent, dpop, token, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct), ct);
        Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);

        var enrolledKey = new KeyActRequests.KeyPair(agent.Kid, agent.AssertionKey);
        using var retired = await RetireAsync(client, agent.AgentId, dpop, token, enrolledKey, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct), ct);
        Assert.Equal(HttpStatusCode.Created, retired.StatusCode);
    }

    /// <summary>A rotated-to kid holding a literal percent escape is retired by its own route. Red on a doubled decode or the old <c>SlashesOf</c> (F1-105, F1-106, F1-107).</summary>
    [Theory]
    [InlineData("k%2Fx-")]
    [InlineData("k%20x-")]
    [InlineData("k%x-")]
    public async Task R4_39_AKidHoldingALiteralPercentEscapeCanBeRetired(string stem)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await EnrolledAsync(client, KeyActRequests.Unique("kid-holder"), ct);
        var next = KeyActRequests.KeyPair.New(stem + Guid.NewGuid().ToString("N")[..8]);

        using var rotated = await RotateAsync(client, agent, dpop, token, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct), ct);
        Assert.Equal(HttpStatusCode.Created, rotated.StatusCode);

        using var retired = await RetireAsync(client, agent.AgentId, dpop, token, next, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct), ct);
        Assert.Equal(HttpStatusCode.Created, retired.StatusCode);
    }

    /// <summary>
    /// <c>a/b</c> and <c>a%2Fb</c> are two identities that the framework's path reads alike (a sent
    /// <c>%2F</c> and a sent <c>%252F</c> both read <c>%2F</c>), so a proof for one verifies at the other's
    /// URL. The subject check, against the raw-decoded identity, is what keeps one from acting for the
    /// other: 403 not-own both ways, no key added to either, and each still acts on its own route.
    /// </summary>
    [Fact]
    public async Task R4_18_TheEscapedAndTheDoubleEscapedSlashNameDifferentIdentities()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (a, aDpop, aToken) = await EnrolledAsync(client, $"https://agents.example/a/b-{suffix}", ct);
        var (b, bDpop, bToken) = await EnrolledAsync(client, $"https://agents.example/a%2Fb-{suffix}", ct);
        Assert.Equal(KeyActRequests.RotateHtu(a.AgentId, Origin), KeyActRequests.RotateHtu(b.AgentId, Origin));
        Assert.NotEqual(KeyActRequests.RotateUrl(a.AgentId, Origin), KeyActRequests.RotateUrl(b.AgentId, Origin));

        var aNext = KeyActRequests.KeyPair.New("a-next-" + suffix);
        var bNext = KeyActRequests.KeyPair.New("b-next-" + suffix);
        var aPrev = await KeyActRequests.LatestLifecycleAsync(client, a.AgentId, ct);
        var bPrev = await KeyActRequests.LatestLifecycleAsync(client, b.AgentId, ct);

        // A's token and a proof for A's URL (the path form the two share), sent to B's route.
        using var aAtB = await aDpop.PostAsync(
            client, KeyActRequests.RotateUrl(b.AgentId, Origin), aToken, KeyActRequests.RotationBody(a, aNext, aPrev), forum.Now, ct,
            contentType: "application/json", htu: KeyActRequests.RotateHtu(a.AgentId, Origin));
        Assert.Equal(HttpStatusCode.Forbidden, aAtB.StatusCode);
        Assert.Equal("curia/keys/not-own", (await KeyActRequests.ProblemOfAsync(aAtB, ct)).Type);

        using var bAtA = await bDpop.PostAsync(
            client, KeyActRequests.RotateUrl(a.AgentId, Origin), bToken, KeyActRequests.RotationBody(b, bNext, bPrev), forum.Now, ct,
            contentType: "application/json", htu: KeyActRequests.RotateHtu(b.AgentId, Origin));
        Assert.Equal(HttpStatusCode.Forbidden, bAtA.StatusCode);
        Assert.Equal("curia/keys/not-own", (await KeyActRequests.ProblemOfAsync(bAtA, ct)).Type);

        foreach (var (agent, prev) in new[] { (a, aPrev), (b, bPrev) })
        {
            using var keySet = await KeyActRequests.KeySetAsync(client, agent.AgentId, ct);
            Assert.Single(keySet.RootElement.GetProperty("keys").EnumerateArray());
            Assert.Equal(prev, KeyActRequests.LifecycleOf(keySet)[^1]);
        }

        // Positive controls: each acts on its own route.
        using var aOwn = await RotateAsync(client, a, aDpop, aToken, aNext, aPrev, ct);
        Assert.Equal(HttpStatusCode.Created, aOwn.StatusCode);
        using var bOwn = await RotateAsync(client, b, bDpop, bToken, bNext, bPrev, ct);
        Assert.Equal(HttpStatusCode.Created, bOwn.StatusCode);
    }

    /// <summary>
    /// The helper that rebuilds the path form is what a real host reads: for each identifier and kid, the URL
    /// the pipeline built from its path equals <see cref="KeyActRequests.RotateHtu"/> and
    /// <see cref="KeyActRequests.RetireHtu"/>. Without this the helper and the server could drift together
    /// and every proof would still verify.
    /// </summary>
    [Theory]
    [InlineData("a%2Fb")]
    [InlineData("a%252Fb")]
    [InlineData("a%5Cb")]
    [InlineData("a%20b")]
    [InlineData("a%b")]
    [InlineData("a/b")]
    [InlineData("a b")]
    [InlineData("plain")]
    public async Task R4_18_ThePathFormTheClientSignsIsWhatTheHostReads(string stem)
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var agentId = KeyActRequests.Unique(stem);
        var kid = "k-" + stem;

        foreach (var url in new[] { KeyActRequests.RotateUrl(agentId, Origin), KeyActRequests.RetireUrl(agentId, kid, Origin) })
        {
            forum.Recorder.Clear();
            using var content = new ByteArrayContent([]);
            using var response = await client.PostAsync(new Uri(new Uri(url).PathAndQuery, UriKind.Relative), content, ct);

            var expected = url.EndsWith("/retire", StringComparison.Ordinal)
                ? KeyActRequests.RetireHtu(agentId, kid, Origin)
                : KeyActRequests.RotateHtu(agentId, Origin);
            Assert.Equal(expected, forum.Recorder.Url);
        }
    }

    /// <summary>
    /// The real host populates the raw target, so the route's values are decoded from it and the
    /// path-only fallback (which cannot tell <c>%2F</c> from <c>%252F</c>) is not what production takes.
    /// </summary>
    [Fact]
    public async Task R4_18_TheRealHostHandsTheRouteItsRawTarget()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var url = KeyActRequests.RotateUrl(KeyActRequests.Unique("a%252Fb"), Origin);
        var sent = new Uri(url).PathAndQuery;

        forum.Recorder.Clear();
        using var content = new ByteArrayContent([]);
        using var response = await client.PostAsync(new Uri(sent, UriKind.Relative), content, ct);

        Assert.False(string.IsNullOrEmpty(forum.Recorder.RawTarget));
        Assert.Equal(sent, forum.Recorder.RawTarget);
    }

    /// <summary>
    /// A malformed escape in the identity or the kid is refused as the request path, never repaired, and
    /// before any token is looked at (no token is sent): 400 <c>curia/request/malformed-path</c>, not 401.
    /// Kestrel hands the pipeline a malformed percent sequence (<c>%G1</c>, a lone <c>%</c>) and a sequence
    /// that is well formed and not UTF-8 (<c>%FF</c>) alike, so the Forum's own strict decode answers all of
    /// them. Red when the strict decode is dropped (F1-108).
    /// </summary>
    [Theory]
    [InlineData("/v1/agents/%FF/keys")]
    [InlineData("/v1/agents/x%FFy/keys")]
    [InlineData("/v1/agents/x/keys/%FF/retire")]
    [InlineData("/v1/agents/%C0%80/keys")]
    [InlineData("/v1/agents/%G1/keys")]
    [InlineData("/v1/agents/%/keys")]
    [InlineData("/v1/agents/x/keys/%G1/retire")]
    public async Task R4_18_AMalformedEscapeInARouteValueAnswers400BeforeAnyToken(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var (status, body) = await RawPostAsync(new Uri(Origin), path, ct);

        Assert.Equal(400, status);
        Assert.Contains("\"type\":\"curia/request/malformed-path\"", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// A kid holding NUL is refused and nothing is written. Kestrel refuses a <c>%00</c> in the path itself
    /// (an empty 400), so <c>Kid.Parse</c>'s own refusal of NUL is not reachable over HTTP and is pinned by
    /// its Domain facts; the identity side is not-own at best (a NUL identity is no token's subject).
    /// </summary>
    [Fact]
    public async Task R4_39_ARetirementKidHoldingNulIsRefusedByTheKidRule()
    {
        var ct = TestContext.Current.CancellationToken;
        var client = forum.Client;
        var (agent, dpop, token) = await EnrolledAsync(client, KeyActRequests.Unique("nul-kid"), ct);
        var enrolledKey = new KeyActRequests.KeyPair(agent.Kid, agent.AssertionKey);
        var prev = await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct);
        var url = $"{KeyActRequests.RotateUrl(agent.AgentId, Origin)}/%00/retire";

        using var response = await dpop.PostAsync(
            client, url, token, KeyActRequests.RetirementBody(agent.AgentId, enrolledKey, prev), forum.Now, ct,
            contentType: "application/json", htu: KeyActRequests.PathForm(url));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(prev, await KeyActRequests.LatestLifecycleAsync(client, agent.AgentId, ct));
    }

    /// <summary>One request line written raw, with no body and no credentials: the status and the body as text.</summary>
    internal static async Task<(int Status, string Body)> RawPostAsync(Uri address, string path, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(address.Host, address.Port, ct);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes(
            $"POST {path} HTTP/1.1\r\nHost: {address.Authority}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request, ct);

        using var answer = new MemoryStream();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await stream.CopyToAsync(answer, timeout.Token);

        var text = Encoding.Latin1.GetString(answer.ToArray());
        var split = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var head = split < 0 ? text : text[..split];
        var body = split < 0 ? string.Empty : text[(split + 4)..];
        var line = head.Split("\r\n")[0].Split(' ');
        var status = line.Length > 1 && int.TryParse(line[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code) ? code : 0;
        return (status, body);
    }
}

/// <summary>
/// R4.38 (add.) on a real socket: the guard is the first act, so a malformed path under the guarded Forum is
/// still 403 <c>curia/keys/lifecycle-not-enabled</c>, not 400.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
[Collection("forum")]
public sealed class KeyRouteGuardedIdentityTests(KeyRouteGuardedKestrelFixture forum) : IClassFixture<KeyRouteGuardedKestrelFixture>
{
    [Theory]
    [InlineData("/v1/agents/%FF/keys")]
    [InlineData("/v1/agents/x/keys/%FF/retire")]
    public async Task R4_38_TheGuardRefusesBeforeThePathIsRead(string path)
    {
        var ct = TestContext.Current.CancellationToken;
        var address = forum.Client.BaseAddress!;

        var (status, body) = await KeyRouteIdentityTests.RawPostAsync(address, path, ct);

        Assert.Equal(403, status);
        Assert.Contains("\"type\":\"curia/keys/lifecycle-not-enabled\"", body, StringComparison.Ordinal);
    }
}
