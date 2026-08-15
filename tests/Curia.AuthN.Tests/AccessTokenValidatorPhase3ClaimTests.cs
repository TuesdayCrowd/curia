using System.Diagnostics.CodeAnalysis;
using Curia.AuthN.Tests.Support;
using Xunit;

namespace Curia.AuthN.Tests;

/// <summary>Phase 3: R5.16's skew boundary, R5.2's TTL ceiling, expiry, and errata A17's <c>nbf</c>
/// addition (Table 8's SHOULD, missing from the printed algorithm).</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs (R5.2, A17) they pin verbatim, mirroring " +
        "Curia.Architecture.Tests.LayeringTests' CS-6/CS-7 precedent.")]
public sealed class AccessTokenValidatorPhase3ClaimTests
{
    [Fact]
    public async Task IatExactlyAtThePermittedSkewBoundaryIsAccepted()
    {
        var scenario = new AccessTokenScenario();
        var iat = scenario.Clock.GetUtcNow() + TimeSpan.FromSeconds(30);
        var payload = scenario.ValidAccessTokenPayload()
            .WithClaim("iat", TestJwt.ToUnixSeconds(iat))
            .WithClaim("exp", TestJwt.ToUnixSeconds(iat + TimeSpan.FromSeconds(60)));
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
    }

    [Fact]
    public async Task IatOneSecondPastThePermittedSkewBoundaryIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var iat = scenario.Clock.GetUtcNow() + TimeSpan.FromSeconds(31);
        var payload = scenario.ValidAccessTokenPayload()
            .WithClaim("iat", TestJwt.ToUnixSeconds(iat))
            .WithClaim("exp", TestJwt.ToUnixSeconds(iat + TimeSpan.FromSeconds(60)));
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/issued-in-future", error!.Type);
    }

    [Fact]
    public async Task TtlExactlyAtTheThreeHundredSecondCeilingIsAccepted()
    {
        var scenario = new AccessTokenScenario();
        var payload = scenario.ValidAccessTokenPayload()
            .WithClaim("iat", TestJwt.ToUnixSeconds(scenario.Iat))
            .WithClaim("exp", TestJwt.ToUnixSeconds(scenario.Iat + TimeSpan.FromSeconds(300)));
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
    }

    [Fact]
    public async Task R52_TtlOneSecondBeyondTheThreeHundredSecondCeilingIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var payload = scenario.ValidAccessTokenPayload()
            .WithClaim("iat", TestJwt.ToUnixSeconds(scenario.Iat))
            .WithClaim("exp", TestJwt.ToUnixSeconds(scenario.Iat + TimeSpan.FromSeconds(301)));
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/ttl-exceeded", error!.Type);
    }

    [Fact]
    public async Task ExpiredTokenIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var iat = scenario.Clock.GetUtcNow() - TimeSpan.FromSeconds(400);
        var exp = scenario.Clock.GetUtcNow() - TimeSpan.FromSeconds(1); // already in the past
        var payload = scenario.ValidAccessTokenPayload()
            .WithClaim("iat", TestJwt.ToUnixSeconds(iat))
            .WithClaim("exp", TestJwt.ToUnixSeconds(exp));
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/expired", error!.Type);
    }

    [Fact]
    public async Task TokenExpiringAtExactlyNowIsRejected()
    {
        // "require now() < claims.exp" -- exp is an exclusive upper bound, so now() == exp must
        // already read as expired, not as the last valid instant.
        var scenario = new AccessTokenScenario();
        var payload = scenario.ValidAccessTokenPayload().WithClaim("exp", TestJwt.ToUnixSeconds(scenario.Clock.GetUtcNow()));
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/expired", error!.Type);
    }

    [Fact]
    public async Task A17_NbfInTheFutureBeyondSkewIsRejected_TokenOtherwiseEntirelyValid()
    {
        // Errata A17: "a nbf check to Phase 3 to match Table 8's SHOULD." Every other claim here
        // is exactly the scenario's valid baseline; only nbf is wrong.
        var scenario = new AccessTokenScenario();
        var nbf = scenario.Clock.GetUtcNow() + TimeSpan.FromSeconds(45); // beyond the 30s skew
        var payload = scenario.ValidAccessTokenPayload().WithClaim("nbf", TestJwt.ToUnixSeconds(nbf));
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/not-yet-valid", error!.Type);
    }

    [Fact]
    public async Task A17_NbfWithinSkewIsAccepted()
    {
        var scenario = new AccessTokenScenario();
        var nbf = scenario.Clock.GetUtcNow() + TimeSpan.FromSeconds(30); // exactly at the skew boundary
        var payload = scenario.ValidAccessTokenPayload().WithClaim("nbf", TestJwt.ToUnixSeconds(nbf));
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
    }

    [Fact]
    public async Task AbsentNbfIsAccepted()
    {
        // Table 8 marks nbf SHOULD, not SHALL -- an access token that omits it entirely must
        // still validate.
        var scenario = new AccessTokenScenario();
        var request = scenario.ValidRequest();

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
    }

    [Fact]
    public async Task WrongIssuerIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var payload = scenario.ValidAccessTokenPayload().WithClaim("iss", "https://not-the-issuer.example");
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/issuer-mismatch", error!.Type);
    }

    [Fact]
    public async Task WrongAudienceIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var payload = scenario.ValidAccessTokenPayload().WithClaim("aud", "https://not-this-api.example");
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/audience-mismatch", error!.Type);
    }

    [Fact]
    public async Task AudienceAsAnArrayContainingTheResourceServerIsAccepted()
    {
        var scenario = new AccessTokenScenario();
        var payload = scenario.ValidAccessTokenPayload()
            .WithClaim("aud", new[] { "https://something-else.example", AccessTokenScenario.ResourceServer });
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
    }

    [Fact]
    public async Task ClientIdNotMatchingSubjectIsRejected()
    {
        // Table 8: "client_id SHALL Same as sub for this profile -- Consistency check," present
        // in the table but absent from the printed §5.5 pseudocode. See the Stage C report.
        var scenario = new AccessTokenScenario();
        var payload = scenario.ValidAccessTokenPayload().WithClaim("client_id", "agent://curia.example/someone/else");
        var token = scenario.SignAccessToken(payload: payload);
        var request = scenario.ValidRequest(accessToken: token);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/client-id-mismatch", error!.Type);
    }
}
