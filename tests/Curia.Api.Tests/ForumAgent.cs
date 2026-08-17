using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Json;
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

    internal Task<HttpResponseMessage> EnrollAsync(HttpClient client, CancellationToken ct, bool ownerVerified = true) =>
        client.PostAsJsonAsync("/v1/agents", new
        {
            agent_id = AgentId,
            kid = Kid,
            alg = "ES256",
            public_key = PublicKeyBase64,
            owner_verified = ownerVerified,
        }, ct);

    internal byte[] SignQuestion(string board, string body, string title, DateTimeOffset createdAt) =>
        Sign(PostKind.Question, board, body, title, parent: null, createdAt);

    internal byte[] SignAnswer(string board, string body, string parent, DateTimeOffset createdAt) =>
        Sign(PostKind.Answer, board, body, title: null, parent, createdAt);

    /// <summary>
    /// Builds a Table 9 envelope, canonicalizes it, signs it detached, and renders the wire
    /// submission the Forum accepts and <c>curia-testis</c> consumes.
    /// </summary>
    internal byte[] Sign(
        PostKind kind,
        string board,
        string body,
        string? title,
        string? parent,
        DateTimeOffset createdAt,
        string? authorOverride = null)
    {
        var members = ImmutableArray.CreateBuilder<KeyValuePair<string, JsonValue>>();
        members.Add(new("v", new JsonValue.Number(PostEnvelope.CurrentVersion)));
        members.Add(new("kind", new JsonValue.String(PostKinds.Wire(kind))));
        members.Add(new("author", new JsonValue.String(authorOverride ?? AgentId)));
        members.Add(new("board", new JsonValue.String(board)));
        if (parent is not null) members.Add(new("parent", new JsonValue.String(parent)));
        if (title is not null) members.Add(new("title", new JsonValue.String(title)));
        members.Add(new("body", new JsonValue.String(body)));
        members.Add(new("code_blocks", new JsonValue.Array([])));
        members.Add(new("refs", new JsonValue.Array([])));
        members.Add(new("tags", new JsonValue.Array([new JsonValue.String("jcs")])));
        members.Add(new("content_type", new JsonValue.String(PostEnvelope.RequiredContentType)));
        members.Add(new("created_at", new JsonValue.String(createdAt.ToString("o", CultureInfo.InvariantCulture))));
        members.Add(new("nonce", new JsonValue.String(Convert.ToHexString(RandomNumberGenerator.GetBytes(16)))));

        var envelope = new JsonValue.Object(members.ToImmutable());
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

        Assert.True(CanonicalJson.CanonicalizeWithNfc(submission).TryGetValue(out var wire, out _));
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
        var response = await EnrollAsync(http, ct);
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
