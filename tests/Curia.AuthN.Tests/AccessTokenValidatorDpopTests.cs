using System.Diagnostics.CodeAnalysis;
using Curia.AuthN.Tests.Support;
using Xunit;

namespace Curia.AuthN.Tests;

/// <summary>Phase 4: proof of possession, including errata A17's <c>typ</c> addition and Stage
/// C's own DPoP algorithm pin (documented as Stage C's own hardening, not an errata item -- see
/// the Stage C report).</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs (A17, R14.3) they pin verbatim, mirroring " +
        "Curia.Architecture.Tests.LayeringTests' CS-6/CS-7 precedent.")]
public sealed class AccessTokenValidatorDpopTests
{
    [Fact]
    public async Task MissingDpopProofIsRejected_R511UnboundTokenNeverAccepted()
    {
        var scenario = new AccessTokenScenario();
        var request = scenario.ValidRequest() with { DpopProof = null };

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/missing-dpop-proof", error!.Type);
    }

    [Fact]
    public async Task A17_DpopProofTypMismatchIsRejected_ProofOtherwiseEntirelyValid()
    {
        // Errata A17's first named gap: "the DPoP proof's typ never checked." Every other field
        // -- signature, binding, htm, htu, iat, ath, jti -- is exactly what a valid proof carries.
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var header = scenario.ValidDpopHeader().With("typ", "JWT"); // wrong: not "dpop+jwt"
        var proof = scenario.SignDpopProof(token, header: header);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/typ-mismatch", error!.Type);
        Assert.Equal("JWT", error.Detail); // AuthNErrors.TypMismatch's Detail is the *actual* value received
    }

    [Fact]
    public async Task A17_DpopTypIsCheckedBeforeTheBindingThumbprintEvenWhenBindingWouldAlsoFail()
    {
        // Wrong typ AND a jwk whose thumbprint does not match cnf.jkt (a later Phase 4 check).
        // If binding ran first, this would fail with binding-mismatch; A17 places typ earlier.
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var wrongKey = TestKeys.Ed25519("some-other-key");
        var header = scenario.ValidDpopHeader(wrongKey).With("typ", "JWT");
        var proof = scenario.SignDpopProof(token, header: header, payload: scenario.ValidDpopPayload(token), key: wrongKey);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/typ-mismatch", error!.Type);
    }

    [Fact]
    public async Task DpopAlgNotAllowedIsRejected_Stage3sOwnHardening()
    {
        // Not an errata item: R5.9's "pin the algorithm before any signature work" reasoning
        // applied to the DPoP proof's own header, which the printed §5.5 pseudocode never pins
        // explicitly. See the Stage C report.
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var header = scenario.ValidDpopHeader().With("alg", "none");
        var proof = scenario.SignDpopProof(token, header: header);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/alg-not-allowed", error!.Type);
    }

    /// <summary>
    /// R11.33 (errata G17): a proof whose <c>jwk</c> is no point on P-256, under a token bound to
    /// that jwk -- which the token endpoint issues, since it reads a proof's key without building it
    /// (register D29) -- is refused as a malformed key, never thrown. It threw: the key was built with
    /// <c>ECDsa.Create</c>, which refuses a point off the curve with an exception nothing caught, and
    /// every route behind authentication answered 500.
    /// </summary>
    [Fact]
    public async Task R11_33_AProofKeyThatIsNoPointOnTheCurveIsRefusedNotThrown()
    {
        var scenario = new AccessTokenScenario();
        var x = Enumerable.Repeat((byte)1, 32).ToArray();
        var y = Enumerable.Repeat((byte)2, 32).ToArray();
        var payload = scenario.ValidAccessTokenPayload();
        payload["cnf"] = new Dictionary<string, object?> { ["jkt"] = TestThumbprint.ForP256(x, y) };
        var token = scenario.SignAccessToken(payload: payload);
        var header = new Dictionary<string, object>
        {
            ["alg"] = "ES256",
            ["typ"] = "dpop+jwt",
            ["jwk"] = new Dictionary<string, object>
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["x"] = System.Buffers.Text.Base64Url.EncodeToString(x),
                ["y"] = System.Buffers.Text.Base64Url.EncodeToString(y),
            },
        };
        var anyKey = TestKeys.Es256("not-the-embedded-key");
        var proof = scenario.SignDpopProof(token, header: header, payload: scenario.ValidDpopPayload(token), key: anyKey);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/malformed-jwk", error!.Type);
    }

    /// <summary>
    /// R11.33 (errata G17): a proof the bound DPoP key genuinely signed, whose <c>iat</c> is a number
    /// <see cref="DateTimeOffset"/> cannot hold, is refused, never thrown. It reached
    /// <see cref="DateTimeOffset.FromUnixTimeSeconds"/> unchecked after the signature verified, and
    /// every route behind authentication answered 500 to any enrolled agent.
    /// </summary>
    [Theory]
    [InlineData(10000000000000L)]
    [InlineData(-100000000000L)]
    public async Task R11_33_AProofWhoseIatIsOutOfRangeIsRefusedNotThrown(long iat)
    {
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var payload = scenario.ValidDpopPayload(token).WithClaim("iat", iat);
        var proof = scenario.SignDpopProof(token, payload: payload, key: scenario.DpopKey);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/malformed", error!.Type);
    }

    [Fact]
    public async Task DpopBindingMismatchIsRejected_ThumbprintDoesNotMatchCnfJkt()
    {
        // R14.3: "DPoP proof whose key thumbprint does not match cnf.jkt -> rejected." A proof
        // that is otherwise perfectly valid, signed by a different (also genuine) key.
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var otherKey = TestKeys.Ed25519("not-the-bound-key");
        var proof = scenario.SignDpopProof(
            token, header: scenario.ValidDpopHeader(otherKey), payload: scenario.ValidDpopPayload(token), key: otherKey);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/binding-mismatch", error!.Type);
    }

    /// <summary>
    /// R5.21 (errata G16): a proof whose header names <c>ES256</c> over an embedded Ed25519
    /// <c>jwk</c>, genuinely signed by that key, is refused by name. The binding holds -- the jwk is the
    /// token's -- so only the pin can refuse it; before the pin the header chose the ES256 verifier,
    /// and the refusal read as a bad signature.
    /// </summary>
    [Fact]
    public async Task R5_21_AProofWhoseHeaderNamesAnotherAlgorithmThanItsJwkIsRefusedByName()
    {
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var proof = scenario.SignDpopProof(token, header: scenario.ValidDpopHeader().With("alg", "ES256"));
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/alg-key-mismatch header=ES256 key=EdDSA", $"{error!.Type} {error.Detail}");
    }

    [Fact]
    public async Task DpopProofWithTamperedSignatureIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var proof = TestJwt.SignWithTamperedSignature(scenario.ValidDpopHeader(), scenario.ValidDpopPayload(token), scenario.DpopKey);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/signature-invalid", error!.Type);
    }

    [Fact]
    public async Task R143_DpopProofForADifferentMethodIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var payload = scenario.ValidDpopPayload(token).WithClaim("htm", "DELETE");
        var proof = scenario.SignDpopProof(token, payload: payload);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/method-mismatch", error!.Type);
    }

    [Fact]
    public async Task R143_DpopProofForADifferentUriIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var payload = scenario.ValidDpopPayload(token).WithClaim("htu", "https://api.curia.example/v1/other");
        var proof = scenario.SignDpopProof(token, payload: payload);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/url-mismatch", error!.Type);
    }

    [Fact]
    public async Task DpopProofIatExactlyAtTheSkewBoundaryIsAccepted()
    {
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var iat = scenario.Clock.GetUtcNow() - TimeSpan.FromSeconds(30);
        var payload = scenario.ValidDpopPayload(token).WithClaim("iat", TestJwt.ToUnixSeconds(iat));
        var proof = scenario.SignDpopProof(token, payload: payload);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
    }

    [Fact]
    public async Task DpopProofIatOneSecondPastTheSkewBoundaryIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var iat = scenario.Clock.GetUtcNow() - TimeSpan.FromSeconds(31);
        var payload = scenario.ValidDpopPayload(token).WithClaim("iat", TestJwt.ToUnixSeconds(iat));
        var proof = scenario.SignDpopProof(token, payload: payload);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/proof-window-exceeded", error!.Type);
    }

    [Fact]
    public async Task DpopProofAthNotMatchingTheAccessTokenIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var payload = scenario.ValidDpopPayload(token).WithClaim("ath", TestJwt.ComputeAth("a-different-token"));
        var proof = scenario.SignDpopProof(token, payload: payload);
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/ath-mismatch", error!.Type);
    }
}
