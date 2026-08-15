using Curia.AuthN.Tests.InMemory;

namespace Curia.AuthN.Tests.Support;

/// <summary>
/// A fully-valid Phase 1-4 baseline (real Ed25519 issuer key, real Ed25519 DPoP key, a
/// correctly cnf.jkt-bound access token, a correctly htm/htu/ath-bound DPoP proof, all claims
/// inside every ceiling) plus one-field mutation helpers, so each negative test can start from
/// "this passes" and change exactly the one thing it means to test -- the shape the task's
/// ordering tests need ("construct an input where a later check would also reject, so the test
/// can only pass if the earlier check ran first").
/// </summary>
internal sealed class AccessTokenScenario
{
    public const string Issuer = "https://auth.curia.example";
    public const string ResourceServer = "https://api.curia.example";
    public const string Subject = "agent://curia.example/tuesdaycrowd/scriptor";
    public const string HttpMethod = "POST";
    public const string CanonicalUrl = "https://api.curia.example/v1/posts";

    public ManualTimeProvider Clock { get; }
    public TestKeyPair IssuerKey { get; }
    public TestKeyPair DpopKey { get; }
    public InMemoryReplayCache ReplayCache { get; }
    public InMemoryDpopNonceStore NonceStore { get; }
    public AccessTokenValidationContext Context { get; }
    public DateTimeOffset Iat { get; }
    public DateTimeOffset Exp { get; }

    public AccessTokenScenario()
    {
        Clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));
        IssuerKey = TestKeys.Ed25519("issuer-2026-Q3");
        DpopKey = TestKeys.Ed25519("dpop-key-irrelevant"); // DPoP proofs resolve by embedded jwk, never kid.
        ReplayCache = new InMemoryReplayCache();
        NonceStore = new InMemoryDpopNonceStore(Clock);
        Iat = Clock.GetUtcNow();
        Exp = Iat + TimeSpan.FromSeconds(300);

        Context = new AccessTokenValidationContext(
            ConfiguredIssuer: Issuer,
            ResourceServer: ResourceServer,
            IssuerKeyResolver: new InMemoryJwsKeyResolver(IssuerKey.Kid, IssuerKey.PublicKey),
            ReplayCache: ReplayCache,
            VerifiersByAlg: TestKeys.Verifiers(),
            Clock: Clock,
            DpopNonceStore: NonceStore);
    }

    public Dictionary<string, object> ValidAccessTokenHeader() => new()
    {
        ["alg"] = IssuerKey.Alg,
        ["kid"] = IssuerKey.Kid,
        ["typ"] = "at+jwt",
    };

    public Dictionary<string, object?> ValidAccessTokenPayload(string? jti = null) => new()
    {
        ["iss"] = Issuer,
        ["sub"] = Subject,
        ["aud"] = ResourceServer,
        ["client_id"] = Subject,
        ["iat"] = TestJwt.ToUnixSeconds(Iat),
        ["exp"] = TestJwt.ToUnixSeconds(Exp),
        ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
        ["scope"] = "post:create",
        ["cnf"] = new Dictionary<string, object?> { ["jkt"] = DpopKey.Thumbprint },
        ["owner"] = "owner:tuesdaycrowd",
        ["tier"] = "T2",
    };

    public string SignAccessToken(
        Dictionary<string, object>? header = null,
        Dictionary<string, object?>? payload = null,
        TestKeyPair? key = null) =>
        TestJwt.Sign(header ?? ValidAccessTokenHeader(), payload ?? ValidAccessTokenPayload(), key ?? IssuerKey);

    public Dictionary<string, object> ValidDpopHeader(TestKeyPair? key = null)
    {
        var k = key ?? DpopKey;
        return new Dictionary<string, object>
        {
            ["alg"] = k.Alg,
            ["typ"] = "dpop+jwt",
            ["jwk"] = k.Jwk,
        };
    }

    public Dictionary<string, object?> ValidDpopPayload(string accessToken, string? jti = null, string? nonce = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["htm"] = HttpMethod,
            ["htu"] = CanonicalUrl,
            ["iat"] = TestJwt.ToUnixSeconds(Iat),
            ["jti"] = jti ?? Guid.NewGuid().ToString("N"),
            ["ath"] = TestJwt.ComputeAth(accessToken),
        };

        if (nonce is not null)
            payload["nonce"] = nonce;

        return payload;
    }

    public string SignDpopProof(
        string accessToken,
        Dictionary<string, object>? header = null,
        Dictionary<string, object?>? payload = null,
        TestKeyPair? key = null) =>
        TestJwt.Sign(header ?? ValidDpopHeader(), payload ?? ValidDpopPayload(accessToken), key ?? DpopKey);

    /// <summary>A complete, everything-valid request: real signatures, correct binding, every
    /// claim inside its ceiling. <see cref="AccessTokenValidator.ValidateRequestAsync"/> on this
    /// exact output is expected to succeed -- see <c>AccessTokenValidatorHappyPathTests</c>.</summary>
    public IncomingRequest ValidRequest(
        string? accessToken = null,
        string? dpopProof = null,
        bool requireNonce = false,
        string? authorizationScheme = "DPoP")
    {
        var token = accessToken ?? SignAccessToken();
        var proof = dpopProof ?? SignDpopProof(token);

        return new IncomingRequest(
            Authorization: authorizationScheme is null ? null : $"{authorizationScheme} {token}",
            DpopProof: proof,
            HttpMethod: HttpMethod,
            CanonicalUrl: CanonicalUrl,
            RequireDpopNonce: requireNonce);
    }
}
