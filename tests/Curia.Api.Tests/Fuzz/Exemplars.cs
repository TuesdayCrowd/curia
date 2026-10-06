using System.Text.Json.Nodes;
using Curia.Domain.Content;
using Curia.Domain.Serving;

namespace Curia.Api.Tests.Fuzz;

/// <summary>
/// One exemplar of a route: a request the Forum answers 2xx, built afresh for every send so its
/// Fresh parts are regenerated. A consumed row's <see cref="Prepare"/> runs before every send.
/// </summary>
internal sealed record ExemplarRow(
    string Method, string Pattern, string Variant,
    Func<FuzzContext, CancellationToken, Task<RequestModel>> Build,
    Func<FuzzContext, CancellationToken, Task>? Prepare = null,
    bool CreatesPosts = false)
{
    internal string Route => $"{Method} {Pattern}";

    /// <summary>Spec §4.10's order: reads and anonymous routes, then writes that create no posts, then <c>POST /v1/posts</c>.</summary>
    internal int Group => Method == "GET" || Pattern == "/v1/posts/batch" ? 1 : Pattern == "/v1/posts" ? 3 : 2;

    /// <summary>A row that creates posts, and <c>accept</c>, runs on a fixture of its own (spec §4.10).</summary>
    internal bool OwnFixture => CreatesPosts || Pattern.EndsWith("/accept", StringComparison.Ordinal);

    /// <summary>How far the clock moves before a <c>Prepare</c>: past R7.22's trailing window for a flag, an hour otherwise.</summary>
    internal TimeSpan PrepareStep =>
        Method == "POST" && Pattern.EndsWith("/flags", StringComparison.Ordinal) ? TimeSpan.FromHours(25) : TimeSpan.FromHours(1);
}

/// <summary>Every route's exemplars (R14.10; plan Task A1, Step 2). A fact holds this to the route table both ways.</summary>
internal static class Exemplars
{
    private const int EnvelopeStringCap = 262_144;
    private const int RationaleCap = 4_096;

    internal static IReadOnlyList<ExemplarRow> All { get; } =
    [
        // Group 1: reads and anonymous routes.
        Anonymous("GET", "/health"),
        Anonymous("GET", ReaderContract.WellKnownPath),
        Anonymous("GET", "/oauth/jwks"),
        Anonymous("GET", "/.well-known/oauth-authorization-server"),
        Anonymous("GET", "/v1/log/head"),
        Anonymous("GET", "/v1/log/jwks"),
        Anonymous("GET", "/v1/log/proof/{index:long}", (m, c) =>
        {
            m.RouteValues.Add(new("index", "0"));
            m.Query.Add(new("tree_size", Number(c.HeadSize)));
        }),
        Anonymous("GET", "/v1/log/consistency", (m, c) =>
        {
            m.Query.Add(new("from", "1"));
            m.Query.Add(new("to", Number(c.HeadSize)));
        }),
        Anonymous("GET", "/v1/log/entries/{index:long}", (m, _) => m.RouteValues.Add(new("index", "0"))),
        Anonymous("GET", "/v1/jwks", (m, c) => m.Query.Add(new("agent", c.Seed.Id))),
        Anonymous("GET", "/v1/posts/{postId}", (m, c) =>
        {
            m.RouteValues.Add(new("postId", c.SeedQuestionId));
            m.Query.Add(new("marking", "datamark"));
            m.Headers.Add(new HeaderEntry("If-None-Match", "\"x\"", []));

            // The framework's file result reads these three on this route's 200 (the coverage of
            // reads found them); with no validator passed to it, none changes the answer.
            m.Headers.Add(new HeaderEntry("If-Match", "\"x\"", []));
            m.Headers.Add(new HeaderEntry("If-Modified-Since", "Sun, 16 Aug 2026 12:00:00 GMT", []));
            m.Headers.Add(new HeaderEntry("If-Unmodified-Since", "Sun, 16 Aug 2026 12:00:00 GMT", []));
        }),
        Anonymous("GET", "/v1/threads/{rootPostId}", (m, c) =>
        {
            m.RouteValues.Add(new("rootPostId", c.SeedQuestionId));
            m.Query.Add(new("marking", "datamark"));
        }),
        Anonymous("GET", "/v1/boards/{board}/posts", (m, _) =>
        {
            m.RouteValues.Add(new("board", FuzzContext.SeedBoard));
            m.Query.Add(new("marking", "datamark"));
        }),
        Anonymous("GET", "/v1/search", (m, c) =>
        {
            m.Query.Add(new("q", FuzzContext.SeedWord));
            m.Query.Add(new("board", FuzzContext.SeedBoard));
            m.Query.Add(new("author", c.Seed.Id));
            m.Query.Add(new("kind", "question"));
            m.Query.Add(new("tags", "jcs"));
            m.Query.Add(new("cursor", c.SearchCursor));
            m.Query.Add(new("limit", "10"));
            m.Query.Add(new("why", "true"));
            m.Query.Add(new("min_verification", "V0"));
            m.Query.Add(new("marking", "datamark"));
        }),
        Authenticated("GET", "/v1/inbox", (m, c) =>
        {
            m.Query.Add(new("board", FuzzContext.SeedBoard));
            m.Query.Add(new("tags", "jcs"));
            m.Query.Add(new("cursor", c.InboxCursor));
            m.Query.Add(new("limit", "10"));
            m.Query.Add(new("marking", "datamark"));
        }),
        Authenticated("GET", "/v1/flags", (_, _) => { }),
        Authenticated("GET", "/v1/posts/{postId}/flags", (m, c) => m.RouteValues.Add(new("postId", c.OwnQuestionId))),
        Anonymous("POST", "/v1/posts/batch", (m, c) =>
        {
            m.Query.Add(new("marking", "datamark"));
            Json(m, new JsonObject { ["digests"] = new JsonArray(c.SeedDigest) });
        }),

        // Group 2: writes that create no posts.
        new("POST", "/oauth/token", "urlencoded", (c, _) => Task.FromResult(TokenRequest(c, multipart: false))),
        new("POST", "/oauth/token", "multipart", (c, _) => Task.FromResult(TokenRequest(c, multipart: true))),
        new("POST", "/v1/agents", "plain",
            (c, _) =>
            {
                var enrollee = c.Prepared?.Enrollee ?? ForumAgent.Create("https://agents.example/fuzz-enroll", "fuzz-enroll");
                var m = new RequestModel("POST", "/v1/agents");
                Json(m, new JsonObject
                {
                    ["agent_id"] = enrollee.AgentId,
                    ["kid"] = enrollee.Kid,
                    ["alg"] = "ES256",
                    ["public_key"] = enrollee.PublicKeyBase64,
                });
                m.Fresh.Add("json:/agent_id");
                m.Fresh.Add("json:/kid");
                return Task.FromResult(m);
            },
            Prepare: (c, _) =>
            {
                var suffix = Guid.NewGuid().ToString("N")[..12];
                c.Prepared = new Prepared(Enrollee: ForumAgent.Create("https://agents.example/fuzz-enroll-" + suffix, "fuzz-enroll-" + suffix));
                return Task.CompletedTask;
            }),
        new("POST", "/v1/posts/{postId}/flags", "plain",
            async (c, ct) =>
            {
                var m = await WriteAsync(c, "/v1/posts/{postId}/flags", ct);
                m.RouteValues.Add(new("postId", c.Prepared?.PostId ?? c.SeedQuestionId));
                Json(m, new JsonObject { ["kind"] = "spam", ["rationale"] = "r" });
                m.Caps["json:/rationale"] = RationaleCap;
                return m;
            },
            Prepare: async (c, ct) =>
            {
                var (postId, _) = await c.AskAsync(c.Second, FuzzContext.OwnBoard, ct);
                c.Prepared = new Prepared(PostId: postId);
            }),
        new("POST", "/v1/posts/{postId}/accept", "plain",
            async (c, ct) =>
            {
                var m = await WriteAsync(c, "/v1/posts/{postId}/accept", ct);
                m.RouteValues.Add(new("postId", c.Prepared?.PostId ?? c.SeedQuestionId));
                return m;
            },
            Prepare: async (c, ct) =>
            {
                var (question, _) = await c.AskAsync(c.Agent, FuzzContext.OwnBoard, ct);
                var (answer, _) = await c.AnswerAsync(c.Second, FuzzContext.OwnBoard, question, ct);
                c.Prepared = new Prepared(PostId: answer);
            }),

        // Group 3: POST /v1/posts, one row per wire kind, each on a fixture of its own.
        Post("question", (c, now) => c.Agent.Agent.SignQuestionNotDuplicate(
                FuzzContext.PostBoard, FuzzContext.Entropy("A question body"), FuzzContext.Entropy("A question"), now,
                "not the same question: a different case", ["jcs"]),
            envelope => envelope["model_hint"] = "none"),
        Post("answer", (c, now) => c.Agent.Agent.SignAnswer(FuzzContext.SeedBoard, FuzzContext.Entropy("An answer body"), c.SeedQuestionId, now)),
        Post("comment", (c, now) => c.Agent.Agent.Sign(
            PostKind.Comment, FuzzContext.SeedBoard, FuzzContext.Entropy("A comment body"), title: null, c.SeedQuestionId, now)),
        Post("revision",
            (c, now) => c.Agent.Agent.SignRevision(
                c.Prepared?.Board ?? FuzzContext.PostBoard, FuzzContext.Entropy("A revision body"),
                c.Prepared?.PostId ?? c.OwnQuestionId, c.Prepared?.Digest ?? c.SeedDigest, now),
            prepare: async (c, ct) =>
            {
                var (postId, digest) = await c.AskAsync(c.Agent, FuzzContext.PostBoard, ct);
                c.Prepared = new Prepared(PostId: postId, Digest: digest, Board: FuzzContext.PostBoard);
            }),
        Post("vote",
            (c, now) => c.Agent.Agent.SignVote(FuzzContext.SeedBoard, c.Prepared?.Digest ?? c.SeedDigest, now),
            prepare: PrepareResultAsync),
        Post("verification",
            (c, now) => c.Agent.Agent.SignVerification(FuzzContext.SeedBoard, c.Prepared?.Digest ?? c.SeedDigest, "reproduced", now),
            envelope => envelope["artifact_digest"] = "sha256:" + new string('a', 64),
            PrepareResultAsync),
        Post("finding",
            (c, now) => c.Agent.Agent.Sign(
                PostKind.Finding, FuzzContext.PostBoard, FuzzContext.Entropy("A finding body"), FuzzContext.Entropy("A finding"), parent: null, now),
            prepare: (c, ct) => c.RaiseToT2Async(ct)),
    ];

    private static string Number(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static ExemplarRow Anonymous(string method, string pattern, Action<RequestModel, FuzzContext>? fill = null) =>
        new(method, pattern, "plain", (c, _) =>
        {
            var m = new RequestModel(method, pattern);
            fill?.Invoke(m, c);
            return Task.FromResult(m);
        });

    private static ExemplarRow Authenticated(string method, string pattern, Action<RequestModel, FuzzContext> fill) =>
        new(method, pattern, "plain", (c, _) =>
        {
            var m = new RequestModel(method, pattern);
            c.Authenticate(m, c.Agent, write: false);
            fill(m, c);
            return Task.FromResult(m);
        });

    /// <summary>A write as the fuzz agent, with the nonce the Forum issued at this clock instant.</summary>
    private static async Task<RequestModel> WriteAsync(FuzzContext c, string pattern, CancellationToken ct)
    {
        await c.EnsureNonceAsync(c.Agent, ct);
        var m = new RequestModel("POST", pattern);
        c.Authenticate(m, c.Agent, write: true);
        return m;
    }

    /// <summary>Every JSON body is declared <c>application/json; charset=utf-8</c>, so the charset is a part of every body route.</summary>
    private static void Json(RequestModel m, JsonNode root, string? signed = null, string? signature = null)
    {
        m.Body = new JsonBody(root, signed, signature);
        m.Headers.Add(new HeaderEntry("Content-Type", "application/json", [new HeaderParameter("charset", "utf-8")]));
    }

    /// <summary>A result-bearing target owned by the second agent, which a vote and a verification need (R8.55, R8.58).</summary>
    private static async Task PrepareResultAsync(FuzzContext c, CancellationToken ct)
    {
        var (postId, digest) = await c.AnswerAsync(c.Second, FuzzContext.SeedBoard, c.SeedQuestionId, ct);
        c.Prepared = new Prepared(PostId: postId, Digest: digest);
    }

    /// <summary>
    /// A token request as an agent library sends one, urlencoded or multipart, its form declared
    /// <c>charset=utf-8</c> (on the request, or on each part), and its proof a JWS part.
    /// </summary>
    private static RequestModel TokenRequest(FuzzContext c, bool multipart)
    {
        var agent = c.Agent;
        var now = c.Now.ToUnixTimeSeconds();
        var m = new RequestModel("POST", "/oauth/token");
        m.Jws["assertion"] = new JwsEntry(
            new JsonObject { ["alg"] = "ES256", ["kid"] = agent.Agent.Kid, ["typ"] = "JWT" },
            new JsonObject
            {
                ["iss"] = agent.Id, ["sub"] = agent.Id, ["aud"] = FuzzContext.TokenEndpoint,
                ["iat"] = now, ["exp"] = now + 60, ["jti"] = Guid.NewGuid().ToString("N"),
            },
            agent.Agent.AssertionKey);
        m.Jws["proof"] = c.Proof(agent, "POST", ath: false, nonce: false);
        m.Body = new FormBody(
            [
                new FormField("grant_type", "client_credentials"),
                new FormField("client_id", agent.Id),
                new FormField("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"),
                new FormField("client_assertion", string.Empty, "assertion"),
                new FormField("scope", "question:create answer:create"),
            ],
            multipart);
        m.Headers.Add(new HeaderEntry("DPoP", string.Empty, [], "proof"));
        m.Headers.Add(multipart
            ? new HeaderEntry("Content-Type", "multipart/form-data", [new HeaderParameter("boundary", FormBody.Boundary)])
            : new HeaderEntry("Content-Type", "application/x-www-form-urlencoded", [new HeaderParameter("charset", "utf-8")]));
        foreach (var claim in (string[])["iat", "exp", "jti"]) m.Fresh.Add("jws:assertion:claims/" + claim);
        m.Fresh.Add("jws:proof:claims/iat");
        m.Fresh.Add("jws:proof:claims/jti");
        return m;
    }

    /// <summary>
    /// <c>POST /v1/posts</c> for one kind: the envelope <see cref="ForumAgent"/> signs, re-signed by
    /// the fuzzer on every send over what it carries. Its title and body are high-entropy and, with
    /// its <c>created_at</c> and <c>nonce</c>, regenerated per send. Every envelope string is capped
    /// at R6.39's 262,144 bytes.
    /// </summary>
    private static ExemplarRow Post(
        string kind,
        Func<FuzzContext, DateTimeOffset, byte[]> sign,
        Action<JsonObject>? edit = null,
        Func<FuzzContext, CancellationToken, Task>? prepare = null) =>
        new("POST", "/v1/posts", kind, async (c, ct) =>
        {
            var m = await WriteAsync(c, "/v1/posts", ct);
            var root = JsonNode.Parse(sign(c, c.Now))!.AsObject();
            var envelope = root["envelope"]!.AsObject();
            edit?.Invoke(envelope);
            m.Jws["post"] = new JwsEntry(JwsBuilder.Segment(root["signature"]!.GetValue<string>(), 0), null, c.Agent.Agent.AssertionKey);
            Json(m, root, "/envelope", "/signature");
            foreach (var member in (string[])["created_at", "nonce", "title", "body"])
                if (envelope.ContainsKey(member)) m.Fresh.Add("json:/envelope/" + member);
            foreach (var (pointer, node) in RawJson.Walk(envelope))
                if (RawJson.KindOf(node) == PartValueKind.String) m.Caps["json:/envelope" + pointer] = EnvelopeStringCap;
            return m;
        },
        prepare,
        CreatesPosts: true);
}
