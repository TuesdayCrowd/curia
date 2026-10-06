using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Curia.OperatorTool;

namespace Curia.Api.Tests.Fuzz;

/// <summary>An agent the fuzzer signs as: its registered key, and a DPoP key of the fuzzer's own that its tokens are bound to.</summary>
internal sealed class FuzzAgent(ForumAgent agent) : IDisposable
{
    internal ForumAgent Agent { get; } = agent;

    internal ECDsa Dpop { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    internal string Id => Agent.AgentId;

    public void Dispose() => Dpop.Dispose();
}

/// <summary>What a consumed row's <c>Prepare</c> left for its next send.</summary>
internal sealed record Prepared(string? PostId = null, string? Digest = null, string? Board = null, ForumAgent? Enrollee = null);

/// <summary>
/// One seeded Forum the fuzzer sends to: a T0 seed agent and its questions, the T1 fuzz agent and a
/// second T1 agent under another owner, a signed head, and cursors. Every fixture is seeded by the
/// same steps (spec §4.10). Every <c>iat</c>, <c>exp</c>, <c>nbf</c> and <c>created_at</c> comes from
/// the fixture's clock.
/// </summary>
internal sealed class FuzzContext : IDisposable
{
    internal const string TokenEndpoint = RequestModel.Origin + "/oauth/token";
    internal const string SeedBoard = "fuzz-board";
    internal const string OwnBoard = "fuzz-own";
    internal const string PostBoard = "fuzz-posts";
    internal const string SeedWord = "zebra";

    private static readonly string LogKeyPem = LogSigningKey.GeneratePem();

    private FuzzContext(ForumFixture forum, HttpClient? client)
    {
        Forum = forum;
        Client = client;
        IssuerKey = ECDsa.Create();
        IssuerKey.ImportFromPem(forum.IssuerSigningKeyPem);
    }

    internal ForumFixture Forum { get; }

    /// <summary>Null for an offline context, which builds exemplars and sends nothing.</summary>
    internal HttpClient? Client { get; }

    internal ECDsa IssuerKey { get; }

    internal FuzzAgent Seed { get; private set; } = null!;

    internal FuzzAgent Agent { get; private set; } = null!;

    internal FuzzAgent Second { get; private set; } = null!;

    internal string SeedQuestionId { get; private set; } = "01J00000000000000000000000";

    internal string SeedDigest { get; private set; } = "sha256:" + new string('0', 64);

    internal string OwnQuestionId { get; private set; } = "01J00000000000000000000001";

    internal long HeadSize { get; private set; } = 1;

    internal string SearchCursor { get; private set; } = "cursor";

    internal string InboxCursor { get; private set; } = "cursor";

    internal JsonObject TokenHeader { get; private set; } = new() { ["alg"] = "ES256", ["kid"] = "kid", ["typ"] = "at+jwt" };

    internal JsonObject TokenClaims { get; private set; } = new()
    {
        ["iss"] = "https://forum.local", ["sub"] = "s", ["aud"] = "https://forum.local", ["client_id"] = "s",
        ["iat"] = 0, ["exp"] = 0, ["nbf"] = 0, ["jti"] = "j", ["scope"] = "s", ["cnf"] = new JsonObject { ["jkt"] = "t" },
        ["owner"] = "s", ["tier"] = "T1",
    };

    /// <summary>The DPoP nonce last issued, and the clock instant it was asked for at.</summary>
    internal string Nonce { get; private set; } = "nonce";

    private DateTimeOffset? _nonceAt;

    /// <summary>What the row's <c>Prepare</c> left for the next send, or null before one has run.</summary>
    internal Prepared? Prepared { get; set; }

    /// <summary>Whether the fuzz agent has been raised to T2, which a finding needs (Table 10).</summary>
    internal bool RaisedToT2 { get; set; }

    internal DateTimeOffset Now => Forum.Now;

    /// <summary>A context with placeholder values, for the facts that inspect exemplars without sending them.</summary>
    internal static FuzzContext Offline(ForumFixture forum)
    {
        var context = new FuzzContext(forum, null);
        context.Seed = new FuzzAgent(ForumAgent.Create("https://agents.example/offline-seed", "offline-seed"));
        context.Agent = new FuzzAgent(ForumAgent.Create("https://agents.example/offline-agent", "offline-agent"));
        context.Second = new FuzzAgent(ForumAgent.Create("https://agents.example/offline-second", "offline-second"));
        return context;
    }

    /// <summary>Seeds a fixture by the steps every fixture is seeded by.</summary>
    internal static async Task<FuzzContext> SeedAsync(ForumFixture forum, CancellationToken ct)
    {
        var client = forum.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(120);
        var context = new FuzzContext(forum, client);
        var suffix = Guid.NewGuid().ToString("N")[..8];

        context.Seed = new FuzzAgent(ForumAgent.Create("https://agents.example/fuzz-seed-" + suffix, "fuzz-seed-" + suffix));
        using (var enrolled = await context.Seed.Agent.EnrollAsync(client, ct))
            Require(enrolled.StatusCode == HttpStatusCode.Created, $"the seed agent did not enroll ({(int)enrolled.StatusCode})");

        var template = await context.RequestTokenAsync(context.Seed, ct);
        context.TokenHeader = JwsBuilder.Segment(template, 0);
        context.TokenClaims = JwsBuilder.Segment(template, 1);

        // Three questions sharing one word, so search has a second page and the inbox one too.
        for (var i = 0; i < 3; i++)
        {
            var (id, digest) = await context.PostAsync(
                context.Seed,
                context.Seed.Agent.SignQuestion(SeedBoard, Entropy("A seed question body"), $"{SeedWord} {Entropy("seed")}", context.Now),
                ct);
            if (i == 0) (context.SeedQuestionId, context.SeedDigest) = (id, digest);
        }

        var (fuzz, _, _) = await FuzzRun.EnrolledAtT1Async(forum, client, ct, "owner:fuzz-" + suffix);
        context.Agent = new FuzzAgent(fuzz);
        var (second, _, _) = await FuzzRun.EnrolledAtT1Async(forum, client, ct, "owner:second-" + suffix);
        context.Second = new FuzzAgent(second);

        (context.OwnQuestionId, _) = await context.AskAsync(context.Agent, OwnBoard, ct);

        using (var stdout = new StringWriter(CultureInfo.InvariantCulture))
        using (var stderr = new StringWriter(CultureInfo.InvariantCulture))
        {
            var exit = await OperatorCommands.RunAsync(
                ["sign-head", "--by", "ops"], forum.ConnectionString, forum.Clock, stdout, stderr, ct, logSigningKeyPem: LogKeyPem);
            Require(exit == ExitCode.Ok, $"the head was not signed: {stderr}");
        }

        using (var head = await context.GetJsonAsync(null, "/v1/log/head", ct))
            context.HeadSize = head.RootElement.GetProperty("head").GetProperty("tree_size").GetInt64();

        using (var page = await context.GetJsonAsync(
            null, $"/v1/search?q={SeedWord}&board={SeedBoard}&author={Uri.EscapeDataString(context.Seed.Id)}&kind=question&tags=jcs&limit=1", ct))
            context.SearchCursor = page.RootElement.GetProperty("next_cursor").GetString()!;

        using (var inbox = await context.GetJsonAsync(context.Agent, $"/v1/inbox?board={SeedBoard}&tags=jcs&limit=1", ct))
            context.InboxCursor = inbox.RootElement.GetProperty("next_cursor").GetString()!;

        return context;
    }

    /// <summary>High-entropy text, so §8.5's dedupe never refuses a second send as a near-duplicate.</summary>
    internal static string Entropy(string stem) => stem + " " + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)) + " " + Guid.NewGuid().ToString("N");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("fuzz seeding: " + message);
    }

    /// <summary>A token for <paramref name="agent"/>, minted as the issuer mints one and signed with the fixture's issuer key, bound to the agent's DPoP key.</summary>
    internal JwsEntry Token(FuzzAgent agent)
    {
        var header = (JsonObject)TokenHeader.DeepClone();
        var claims = (JsonObject)TokenClaims.DeepClone();
        var now = Now.ToUnixTimeSeconds();
        foreach (var name in (string[])["sub", "client_id", "owner"])
            if (claims.ContainsKey(name)) claims[name] = agent.Id;
        if (claims.ContainsKey("iat")) claims["iat"] = now;
        if (claims.ContainsKey("nbf")) claims["nbf"] = now;
        if (claims.ContainsKey("exp")) claims["exp"] = now + 300;
        if (claims.ContainsKey("jti")) claims["jti"] = Guid.NewGuid().ToString("N");
        if (claims["cnf"] is JsonObject cnf) cnf["jkt"] = JwsBuilder.Thumbprint(agent.Dpop);
        return new JwsEntry(header, claims, IssuerKey);
    }

    /// <summary>A DPoP proof for <paramref name="agent"/>; <c>htu</c> and <c>ath</c> are derived when the request is rendered.</summary>
    internal JwsEntry Proof(FuzzAgent agent, string method, bool ath, bool nonce)
    {
        var header = new JsonObject { ["alg"] = "ES256", ["typ"] = "dpop+jwt", ["jwk"] = JwsBuilder.Jwk(agent.Dpop) };
        var claims = new JsonObject
        {
            ["htm"] = method,
            ["htu"] = TokenEndpoint,
            ["iat"] = Now.ToUnixTimeSeconds(),
            ["jti"] = Guid.NewGuid().ToString("N"),
        };
        if (ath) claims["ath"] = string.Empty;
        if (nonce) claims["nonce"] = Nonce;
        return new JwsEntry(header, claims, agent.Dpop);
    }

    /// <summary>Gives <paramref name="model"/> the agent's token and proof, and marks what is regenerated per send.</summary>
    internal void Authenticate(RequestModel model, FuzzAgent agent, bool write)
    {
        ArgumentNullException.ThrowIfNull(model);
        model.Jws["token"] = Token(agent);
        model.Jws["proof"] = Proof(agent, model.Method, ath: true, nonce: write);
        model.Headers.Add(new HeaderEntry("Authorization", "DPoP ", [], "token"));
        model.Headers.Add(new HeaderEntry("DPoP", string.Empty, [], "proof"));
        foreach (var claim in (string[])["iat", "exp", "nbf", "jti"])
            model.Fresh.Add("jws:token:claims/" + claim);
        model.Fresh.Add("jws:proof:claims/iat");
        model.Fresh.Add("jws:proof:claims/jti");
    }

    /// <summary>A nonce the Forum issued at this clock instant: asked for again whenever the clock has moved.</summary>
    internal async Task EnsureNonceAsync(FuzzAgent agent, CancellationToken ct)
    {
        if (Client is null || _nonceAt == Now) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/posts") { Content = new ByteArrayContent([]) };
        var token = Compact(Token(agent));
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
        request.Headers.Add("DPoP", ProofFor(agent, "POST", RequestModel.Origin + "/v1/posts", token, null));
        using var response = await Client.SendAsync(request, ct);
        Require(
            response.StatusCode == HttpStatusCode.Unauthorized && response.Headers.TryGetValues("DPoP-Nonce", out _),
            $"no nonce was issued ({(int)response.StatusCode})");
        Nonce = response.Headers.GetValues("DPoP-Nonce").First();
        _nonceAt = Now;
    }

    private static string Compact(JwsEntry entry) =>
        JwsBuilder.Compact(RawJson.Write(entry.Header, null, null), RawJson.Write(entry.Claims, null, null), entry.Signer);

    private string ProofFor(FuzzAgent agent, string method, string htu, string? token, string? nonce)
    {
        var proof = Proof(agent, method, ath: token is not null, nonce: nonce is not null);
        proof.Claims!["htu"] = htu;
        if (token is not null) proof.Claims["ath"] = JwsBuilder.Ath(token);
        if (nonce is not null) proof.Claims["nonce"] = nonce;
        return Compact(proof);
    }

    /// <summary>A real token request for <paramref name="agent"/>, through the endpoint: the template every minted token copies.</summary>
    private async Task<string> RequestTokenAsync(FuzzAgent agent, CancellationToken ct)
    {
        var now = Now.ToUnixTimeSeconds();
        var assertion = JwsBuilder.Compact(
            RawJson.Write(new JsonObject { ["alg"] = "ES256", ["kid"] = agent.Agent.Kid, ["typ"] = "JWT" }, null, null),
            RawJson.Write(new JsonObject
            {
                ["iss"] = agent.Id, ["sub"] = agent.Id, ["aud"] = TokenEndpoint,
                ["iat"] = now, ["exp"] = now + 60, ["jti"] = Guid.NewGuid().ToString("N"),
            }, null, null),
            agent.Agent.AssertionKey);
        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = agent.Id,
            ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer",
            ["client_assertion"] = assertion,
            ["scope"] = "question:create answer:create",
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/oauth/token") { Content = form };
        request.Headers.Add("DPoP", ProofFor(agent, "POST", TokenEndpoint, null, null));
        using var response = await Client!.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Require(response.StatusCode == HttpStatusCode.OK, $"no token was issued ({(int)response.StatusCode} {body})");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>A GET, as <paramref name="agent"/> or anonymously, which must answer 2xx.</summary>
    internal async Task<JsonDocument> GetJsonAsync(FuzzAgent? agent, string pathAndQuery, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, pathAndQuery);
        if (agent is not null)
        {
            var token = Compact(Token(agent));
            request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
            request.Headers.Add("DPoP", ProofFor(agent, "GET", RequestModel.Origin + pathAndQuery.Split('?')[0], token, null));
        }

        using var response = await Client!.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Require(response.IsSuccessStatusCode, $"GET {pathAndQuery} answered {(int)response.StatusCode} {body}");
        return JsonDocument.Parse(body);
    }

    /// <summary>A write as <paramref name="agent"/>, with the nonce the Forum issued; it must answer 2xx.</summary>
    internal async Task<JsonDocument> WriteAsync(FuzzAgent agent, string path, byte[]? body, string? contentType, CancellationToken ct)
    {
        await EnsureNonceAsync(agent, ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            if (contentType is not null) request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
        }

        var token = Compact(Token(agent));
        request.Headers.Authorization = new AuthenticationHeaderValue("DPoP", token);
        request.Headers.Add("DPoP", ProofFor(agent, "POST", RequestModel.Origin + path, token, Nonce));
        using var response = await Client!.SendAsync(request, ct);
        var answer = await response.Content.ReadAsStringAsync(ct);
        Require(response.IsSuccessStatusCode, $"POST {path} as {agent.Id} answered {(int)response.StatusCode} {answer}");
        return JsonDocument.Parse(answer);
    }

    /// <summary>Posts a signed submission as <paramref name="agent"/>: its post id and digest.</summary>
    internal async Task<(string PostId, string Digest)> PostAsync(FuzzAgent agent, byte[] wire, CancellationToken ct)
    {
        using var json = await WriteAsync(agent, "/v1/posts", wire, null, ct);
        return (json.RootElement.GetProperty("post_id").GetString()!, json.RootElement.GetProperty("digest").GetString()!);
    }

    internal Task<(string PostId, string Digest)> AskAsync(FuzzAgent agent, string board, CancellationToken ct) =>
        PostAsync(agent, agent.Agent.SignQuestion(board, Entropy("A question body"), Entropy("A question"), Now), ct);

    internal Task<(string PostId, string Digest)> AnswerAsync(FuzzAgent agent, string board, string parent, CancellationToken ct) =>
        PostAsync(agent, agent.Agent.SignAnswer(board, Entropy("An answer body"), parent, Now), ct);

    /// <summary>
    /// Table 11's T2 for the fuzz agent, which a finding needs (Table 10): five of its answers accepted
    /// by the second agent's questions, and thirty days at T1.
    /// </summary>
    internal async Task RaiseToT2Async(CancellationToken ct)
    {
        if (RaisedToT2 || Client is null) return;
        for (var i = 0; i < 5; i++)
        {
            Forum.Clock.Advance(TimeSpan.FromHours(1));
            var (question, _) = await AskAsync(Second, OwnBoard, ct);
            var (answer, _) = await AnswerAsync(Agent, OwnBoard, question, ct);
            using var accepted = await WriteAsync(Second, $"/v1/posts/{answer}/accept", null, null, ct);
        }

        Forum.Clock.Advance(TimeSpan.FromDays(31));
        RaisedToT2 = true;
    }

    public void Dispose()
    {
        Client?.Dispose();
        IssuerKey.Dispose();
        Seed?.Dispose();
        Agent?.Dispose();
        Second?.Dispose();
    }

    /// <summary>Bytes for a JSON body the fuzzer writes.</summary>
    internal static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);
}
