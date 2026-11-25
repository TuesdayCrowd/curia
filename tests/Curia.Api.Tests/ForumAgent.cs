using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Curia.Canon.Canonical;
using Curia.Canon.Json;
using Curia.Canon.Jws;
using Curia.Domain.Content;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// A client agent: an ES256 key pair, an identity, and the ability to produce a signed submission.
///
/// <para>Real cryptography over the BCL's <see cref="ECDsa"/>, not a stub. The tests that use this
/// are about whether a signature made by an outside party verifies inside the Forum -- and, in
/// <c>OfflineVerificationTests</c>, whether a second implementation agrees. A stub signer would
/// make every one of those assertions vacuous.</para>
///
/// <para>Shared by both API test classes rather than duplicated: two copies of the code that
/// decides what a submission looks like would drift, and a drift here would look like a Forum bug.</para>
/// </summary>
internal sealed class ForumAgent
{
    private readonly ECDsa _key;

    private ForumAgent(string agentId, string kid, ECDsa key)
    {
        AgentId = agentId;
        Kid = kid;
        _key = key;
    }

    internal string AgentId { get; }

    internal string Kid { get; }

    internal static ForumAgent Create(string agentId, string kid) =>
        new(agentId, kid, ECDsa.Create(ECCurve.NamedCurves.nistP256));

    internal string PublicKeyBase64 => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    /// <summary>
    /// The agent's own signing key, for the client-assertion half of §5. Exposed to the test
    /// project only -- the same key the Registrar registered, which is what makes the assertion
    /// authenticate <i>this</i> agent rather than merely some holder of some key.
    /// </summary>
    internal ECDsa AssertionKey => _key;

    private SigningKey SigningKey => new("ES256", Kid, _key.ExportPkcs8PrivateKey());

    /// <summary>
    /// The Forum's clock for each client the fixture handed out (R4.46: a proof's <c>iat</c> is judged by the Forum's clock, which a test moves and which is not the wall clock). Filled by
    /// <see cref="ForumFixture"/> for every client it creates; <see cref="EnrollAsync(HttpClient, CancellationToken)"/> reads it. A client with no clock recorded is refused, never given the wall clock:
    /// a fallback would fail forty tests with a proof out of its window and read as forty defects of the Forum.
    /// </summary>
    private static readonly ConditionalWeakTable<HttpClient, TimeProvider> ForumClocks = new();

    internal static void RecordClock(HttpClient client, TimeProvider clock) => ForumClocks.AddOrUpdate(client, clock);

    private static DateTimeOffset ClockOf(HttpClient client) =>
        ForumClocks.TryGetValue(client, out var clock)
            ? clock.GetUtcNow()
            : throw new InvalidOperationException(
                "this HttpClient has no recorded Forum clock: it was not created by a ForumFixture, so a proof's iat cannot be made against the Forum's clock; pass the clock to EnrollAsync(client, now, ct)");

    private static readonly ConditionalWeakTable<HttpClient, string> ForumOrigins = new();

    internal static void RecordOrigin(HttpClient client, string origin) => ForumOrigins.AddOrUpdate(client, origin);

    /// <summary>
    /// The enrollment route's absolute URL as the Forum names itself (R4.46 (rev.), errata G82): the origin the fixture configured the host with, recorded per client, <b>never the client's
    /// <c>BaseAddress</c> or <c>Host</c> header</b>, so that ORG15's agreement of the two is a fact the suite holds and not a restatement.
    /// </summary>
    internal static string EnrollmentAudience(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return ForumOrigins.TryGetValue(client, out var origin)
            ? origin + "/v1/agents"
            : throw new InvalidOperationException("this HttpClient has no recorded Forum origin: it was not created by a ForumFixture, so the enrollment route's audience cannot be named");
    }

    /// <summary>
    /// Enrolls this agent, with the proof of R4.46 signed by its own key: fetches the Forum's nonce from <c>GET /v1/enroll/nonce</c> and sends the request with a proof made at the clock the fixture
    /// recorded for <paramref name="client"/>. Throws if none is recorded (see <see cref="ForumClocks"/>).
    /// </summary>
    internal Task<HttpResponseMessage> EnrollAsync(HttpClient client, CancellationToken ct) => EnrollAsync(client, ClockOf(client), ct);

    /// <summary>Enrolls with a proof made at <paramref name="now"/>, which must be the Forum's clock (R4.46 allows 30 s): <see cref="AuthenticateAsync"/> passes the instant it signs the token request at.</summary>
    internal async Task<HttpResponseMessage> EnrollAsync(HttpClient client, DateTimeOffset now, CancellationToken ct) =>
        await client.PostAsJsonAsync("/v1/agents", new
        {
            agent_id = AgentId,
            kid = Kid,
            alg = "ES256",
            public_key = PublicKeyBase64,
            proof = await ProofAsync(client, now, ct),
        }, ct);

    /// <summary>The proof of R4.46 for this agent's own request, signed by its registered key, with the nonce the Forum serves now.</summary>
    internal Task<string> ProofAsync(HttpClient client, DateTimeOffset now, CancellationToken ct) =>
        ProofForAsync(client, now, AgentId, Kid, Convert.FromBase64String(PublicKeyBase64), ct);

    /// <summary>
    /// A proof, signed by this agent's key, for an enrollment request that names <paramref name="agentId"/>, <paramref name="kid"/> and <paramref name="publicKey"/> (stored form): the raw-body sites that send an
    /// identifier a test chose, not the agent's own.
    /// </summary>
    internal async Task<string> ProofForAsync(HttpClient client, DateTimeOffset now, string agentId, string kid, byte[] publicKey, CancellationToken ct)
    {
        using var response = await client.GetAsync(new Uri("/v1/enroll/nonce", UriKind.Relative), ct);
        Assert.True(response.IsSuccessStatusCode, $"GET /v1/enroll/nonce answered {(int)response.StatusCode}");
        var nonce = System.Text.Json.Nodes.JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!["nonce"]!.GetValue<string>();
        return Curia.Tests.Shared.EnrollProofKit.Build(
            "ES256", agentId, kid, publicKey, EnrollmentAudience(client), now, nonce, Curia.Tests.Shared.EnrollProofKit.NewJti(), Curia.Tests.Shared.EnrollProofKit.SignWith(_key));
    }

    internal byte[] SignQuestion(
        string board, string body, string title, DateTimeOffset createdAt, string[]? tags = null) =>
        Sign(PostKind.Question, board, body, title, parent: null, createdAt, tags: tags);

    /// <summary>R8.20: the same question, signed with <c>not_duplicate: true</c> and its rationale.</summary>
    internal byte[] SignQuestionNotDuplicate(
        string board, string body, string title, DateTimeOffset createdAt, string rationale, string[]? tags = null) =>
        Sign(PostKind.Question, board, body, title, parent: null, createdAt, tags: tags, extra:
        [
            new("not_duplicate", new JsonValue.Bool(true)),
            new("duplicate_rationale", new JsonValue.String(rationale)),
        ]);

    internal byte[] SignAnswer(string board, string body, string parent, DateTimeOffset createdAt, JsonValue? refs = null, JsonValue? codeBlocks = null) =>
        Sign(PostKind.Answer, board, body, title: null, parent, createdAt, refs: refs, codeBlocks: codeBlocks);

    /// <summary>
    /// Builds a Table 9 envelope, canonicalizes it, signs it detached, and renders the wire
    /// submission the Forum accepts and <c>curia-testis</c> consumes.
    /// </summary>
    /// <summary>Signs a revision: Table 12 chains it to its predecessor by digest (R6.7).</summary>
    internal byte[] SignRevision(string board, string body, string parent, string? prev, DateTimeOffset createdAt) =>
        Sign(PostKind.Revision, board, body, title: null, parent, createdAt, prev: prev);

    /// <summary>R8.55: a vote on a result, by envelope digest, carrying R8.29's meta-prediction and R8.49's epoch.</summary>
    internal byte[] SignVote(
        string board, string target, DateTimeOffset createdAt, bool endorse = true, int predictedBp = 5000, long epoch = 1) =>
        SignSignal(PostKind.Vote, board, createdAt,
        [
            new("target", new JsonValue.String(target)),
            new("endorse", new JsonValue.Bool(endorse)),
            new("predicted_endorsement_bp", new JsonValue.Number(predictedBp)),
            new("epoch", new JsonValue.Number(epoch)),
            new("refs", new JsonValue.Array([])),
        ]);

    /// <summary>R8.56: a reproduction report on a result, with evidence unless the test is about its absence.</summary>
    internal byte[] SignVerification(
        string board, string target, string result, DateTimeOffset createdAt,
        string body = "Ran the reproduction as described.", string method = "ran the reproduction", bool withEvidence = true) =>
        SignSignal(PostKind.Verification, board, createdAt,
        [
            new("target", new JsonValue.String(target)),
            new("method", new JsonValue.String(method)),
            new("result", new JsonValue.String(result)),
            new("body", new JsonValue.String(body)),
            new("refs", withEvidence
                ? new JsonValue.Array([new JsonValue.Object([
                    new("kind", new JsonValue.String("url")),
                    new("target", new JsonValue.String("https://example.test/trace")),
                ])])
                : new JsonValue.Array([])),
        ]);

    /// <summary>The common Table 9 members plus a signal kind's own, canonicalised, signed and rendered like <see cref="Sign"/>.</summary>
    private byte[] SignSignal(PostKind kind, string board, DateTimeOffset createdAt, KeyValuePair<string, JsonValue>[] own)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(kind))));
        members.Add(new("author", new JsonValue.String(AgentId)));
        members.Add(new("board", new JsonValue.String(board)));
        members.AddRange(own);
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(createdAt.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)))));

        return SignEnvelope(new JsonValue.Object(members.ToImmutable()));
    }

    internal byte[] Sign(
        PostKind kind,
        string board,
        string body,
        string? title,
        string? parent,
        DateTimeOffset createdAt,
        string? authorOverride = null,
        string? prev = null,
        string[]? tags = null,
        KeyValuePair<string, JsonValue>[]? extra = null,
        bool wireInNfc = true,
        JsonValue? refs = null,
        JsonValue? codeBlocks = null)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        if (extra is not null) members.AddRange(extra);
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(kind))));
        members.Add(new("author", new JsonValue.String(authorOverride ?? AgentId)));
        members.Add(new("board", new JsonValue.String(board)));
        if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));
        if (prev is not null) members.Add(new("prev", new JsonValue.String(prev)));
        if (title is not null) members.Add(new("title", new JsonValue.String(title)));
        members.Add(new("body", new JsonValue.String(body)));

        // R8.66 (errata G28): the raw value of either member when a fact signs what a client's draft cannot spell (a `value`-spelled
        // item, a non-object), so the Forum is shown a post whose evidence a reader will not read.
        members.Add(new("code_blocks", codeBlocks ?? new JsonValue.Array([])));
        members.Add(new("refs", refs ?? new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array(
            [.. (tags ?? ["jcs"]).Select(t => (JsonValue)new JsonValue.String(t))])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(createdAt.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)))));

        return SignEnvelope(new JsonValue.Object(members.ToImmutable()), wireInNfc);
    }

    /// <summary>
    /// Signs the NFC canonical form, as every client must. The wire is NFC too unless
    /// <paramref name="wireInNfc"/> is false, when every string is sent as written, as a hand-built
    /// client may send it.
    /// </summary>
    private byte[] SignEnvelope(JsonValue.Object envelope, bool wireInNfc = true)
    {
        Assert.True(CanonicalJson.CanonicalizeWithNfc(envelope).TryGetValue(out var canonical, out _));

        var jws = new DetachedJws(
            new Dictionary<string, IContentSigner> { ["ES256"] = new Es256Signer() },
            new Dictionary<string, IContentVerifier>());

        Assert.True(jws.Sign(canonical, SigningKey).TryGetValue(out var signature, out var e), e?.Type);

        var submission = new JsonValue.Object(
        [
            new("envelope", envelope),
            new("signature", new JsonValue.String(signature!.Compact)),
        ]);

        var rendered = wireInNfc ? CanonicalJson.CanonicalizeWithNfc(submission) : CanonicalJson.Canonicalize(submission);
        Assert.True(rendered.TryGetValue(out var wire, out _));
        return wire.ToArray();
    }

    /// <summary>
    /// Enrolls, obtains a DPoP-bound access token, and returns a client that can post with it.
    ///
    /// <para>Every write path goes through this now. PEP-1 refuses an unauthenticated submission
    /// (that refusal has its own test), so a test that posted without a token would be asserting
    /// against a 401 rather than against the behaviour it meant to check.</para>
    /// </summary>
    internal async Task<(DpopClient Client, string Token)> AuthenticateAsync(
        HttpClient http, string tokenEndpoint, DateTimeOffset now, CancellationToken ct)
    {
        var response = await EnrollAsync(http, now, ct);
        Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);

        var client = DpopClient.For(this, AssertionKey);
        return (client, await client.GetTokenAsync(http, tokenEndpoint, now, ct));
    }

    /// <summary>ES256 over the BCL. JWS wants the fixed-width r||s form, not DER.</summary>
    private sealed class Es256Signer : IContentSigner
    {
        public byte[] Sign(ReadOnlySpan<byte> input, SigningKey key)
        {
            using var signer = ECDsa.Create();
            signer.ImportPkcs8PrivateKey(key.Private.Span, out _);
            return signer.SignData(
                input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
    }
}
