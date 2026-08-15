using System.Diagnostics.CodeAnalysis;
using Curia.AuthN.Tests.InMemory;
using Curia.AuthN.Tests.Support;
using Xunit;

namespace Curia.AuthN.Tests;

/// <summary>
/// R5.9's own words: "several classic vulnerabilities are ordering bugs." Every test here
/// constructs an input where *two* checks would independently reject it, and asserts on the
/// *specific* error the algorithm actually returned -- so the test can only pass if the earlier
/// check in §5.5's stated order is the one that actually ran first. A test that only asserted
/// "the request was rejected" would pass even if the checks ran in the wrong order; these do not.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs (R5.9, R5.10) they pin verbatim, mirroring " +
        "Curia.Architecture.Tests.LayeringTests' CS-6/CS-7 precedent.")]
public sealed class AccessTokenValidatorOrderingTests
{
    [Fact]
    public async Task R59_AlgorithmIsPinnedBeforeKidResolutionEvenWhenKidWouldAlsoFail()
    {
        // header.alg = "none" (not in the allow-list) AND header.kid names a key the resolver
        // does not have. If kid resolution ran first, this would fail with kid-not-found; R5.9
        // requires the alg pin to run first, so it must fail with alg-not-allowed instead.
        var scenario = new AccessTokenScenario();
        var header = scenario.ValidAccessTokenHeader().With("alg", "none").With("kid", "no-such-key");
        var token = scenario.SignAccessToken(header: header);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/alg-not-allowed", error!.Type);
    }

    [Fact]
    public async Task R59_AlgorithmIsPinnedBeforeTypCheckEvenWhenTypWouldAlsoFail()
    {
        // Both header.alg and header.typ are wrong. The printed algorithm checks alg, then typ
        // (in that order) -- so the returned error must be alg-not-allowed, not typ-mismatch.
        var scenario = new AccessTokenScenario();
        var header = scenario.ValidAccessTokenHeader().With("alg", "HS256").With("typ", "JWT");
        var token = scenario.SignAccessToken(header: header);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/alg-not-allowed", error!.Type);
    }

    [Fact]
    public async Task R59_AlgorithmConfusion_Rs256TokenIsRejectedByTheAlgPinAlone()
    {
        // R14.3's named negative test: "Algorithm confusion: RS256 token verified with the
        // public key as an HMAC secret -> rejected." AllowedAlgorithms contains only EdDSA and
        // ES256 (R4.15), so an RS256-headed token is rejected before verify_signature is ever
        // reached -- there is no HMAC/RSA verifier in VerifiersByAlg for it to reach in the first
        // place, but the alg pin is what stops it, not a missing dictionary entry discovered later.
        var scenario = new AccessTokenScenario();
        var header = scenario.ValidAccessTokenHeader().With("alg", "RS256");
        var token = scenario.SignAccessToken(header: header); // signed with the scenario's real Ed25519 key; alg header lies
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/alg-not-allowed", error!.Type);
    }

    [Fact]
    public async Task R510_KidIsResolvedOnlyWithinTheConfiguredResolverNeverFromAUrlInTheToken()
    {
        // The header carries a jku-shaped field pointing at an attacker URL. IJwsKeyResolver's
        // signature accepts only a kid string, so there is no way for this field to ever reach
        // it -- resolution proceeds by kid alone and fails with kid-not-found because "no-such-kid"
        // (not any value derived from "jku") is not in the configured resolver.
        var scenario = new AccessTokenScenario();
        var header = new Dictionary<string, object>
        {
            ["alg"] = scenario.IssuerKey.Alg,
            ["kid"] = "no-such-kid",
            ["typ"] = "at+jwt",
            ["jku"] = "https://attacker.example/jwks.json",
        };
        var token = scenario.SignAccessToken(header: header);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/kid-not-found", error!.Type);
        Assert.Equal("no-such-kid", error.Detail); // proves the failure names the kid, never the jku URL
    }

    [Fact]
    public async Task R510_ResolverIsNeverAskedForAnythingOtherThanTheKidString()
    {
        // A resolver that recorded every kid it was ever asked to resolve, exercised against a
        // request whose header also carries a jku field: the recorded call list contains only
        // the real kid, proving the jku value was never read, let alone passed anywhere.
        var scenario = new AccessTokenScenario();
        var recordingResolver = new RecordingKeyResolver(scenario.IssuerKey.Kid, scenario.IssuerKey.PublicKey);
        var context = scenario.Context with { IssuerKeyResolver = recordingResolver };

        var header = new Dictionary<string, object>
        {
            ["alg"] = scenario.IssuerKey.Alg,
            ["kid"] = scenario.IssuerKey.Kid,
            ["typ"] = "at+jwt",
            ["jku"] = "https://attacker.example/jwks.json",
        };
        var token = scenario.SignAccessToken(header: header);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
        Assert.Equal([scenario.IssuerKey.Kid], recordingResolver.RequestedKids);
    }

    [Fact]
    public async Task SignatureVerificationRunsBeforeClaimValidationEvenWhenClaimsWouldAlsoFail()
    {
        // Wrong aud (a Phase 3 claim failure) AND a tampered signature. Phase 2 runs before
        // Phase 3 in the printed algorithm, so this must fail with signature-invalid, not
        // audience-mismatch.
        var scenario = new AccessTokenScenario();
        var payload = scenario.ValidAccessTokenPayload().WithClaim("aud", "https://not-this-api.example");
        var token = TestJwt.SignWithTamperedSignature(scenario.ValidAccessTokenHeader(), payload, scenario.IssuerKey);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/signature-invalid", error!.Type);
    }

    /// <summary>Records every <c>kid</c> it is asked to resolve, so a test can assert on exactly
    /// what reached the port -- the only way to prove a value (like a header's <c>jku</c>) was
    /// never read at all, as opposed to merely "the request was rejected for some reason."</summary>
    private sealed class RecordingKeyResolver(string kid, Curia.Canon.Jws.PublicKeyMaterial key) : Curia.AuthN.Ports.IJwsKeyResolver
    {
        public List<string> RequestedKids { get; } = [];

        public Task<Curia.Domain.Primitives.Result<Curia.Canon.Jws.PublicKeyMaterial>> ResolveAsync(
            string requestedKid, CancellationToken cancellationToken = default)
        {
            RequestedKids.Add(requestedKid);
            return Task.FromResult(requestedKid == kid
                ? Curia.Domain.Primitives.Result<Curia.Canon.Jws.PublicKeyMaterial>.Ok(key)
                : Curia.Domain.Primitives.Result<Curia.Canon.Jws.PublicKeyMaterial>.Fail(AuthNErrors.KidNotFound(requestedKid)));
        }
    }
}
