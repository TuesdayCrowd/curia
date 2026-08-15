using System.Diagnostics.CodeAnalysis;
using Curia.AuthN.Tests.Support;
using Xunit;

namespace Curia.AuthN.Tests;

/// <summary>
/// R5.1's client assertion lifetime ceiling, RFC 7523's <c>iss</c>/<c>sub</c> consistency
/// requirement, and R5.14's replay cache -- the "for both client assertions and DPoP proofs"
/// half of that requirement <see cref="AccessTokenValidatorReplayTests"/> does not cover.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs (R5.1, R5.14) they pin verbatim, mirroring " +
        "Curia.Architecture.Tests.LayeringTests' CS-6/CS-7 precedent.")]
public sealed class ClientAssertionValidatorTests
{
    [Fact]
    public async Task FullyValidAssertionIsAccepted()
    {
        var scenario = new ClientAssertionScenario();
        var assertion = scenario.SignValid();

        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out var claims, out var error), error?.Detail);
        Assert.Equal(ClientAssertionScenario.AgentId, claims.Sub);
    }

    [Fact]
    public async Task AlgNoneIsRejectedBeforeAnySignatureWork()
    {
        var scenario = new ClientAssertionScenario();
        var header = scenario.ValidHeader().With("alg", "none");
        var assertion = scenario.SignValid(header: header);

        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/alg-not-allowed", error!.Type);
    }

    [Fact]
    public async Task AlgIsPinnedBeforeTypEvenWhenTypWouldAlsoFail()
    {
        var scenario = new ClientAssertionScenario();
        var header = scenario.ValidHeader().With("alg", "HS256").With("typ", "at+jwt");
        var assertion = scenario.SignValid(header: header);

        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/alg-not-allowed", error!.Type);
    }

    [Fact]
    public async Task KidNotInTheConfiguredResolverIsRejectedWithoutEverNamingAUrl()
    {
        var scenario = new ClientAssertionScenario();
        var header = scenario.ValidHeader().With("kid", "no-such-kid").With("jku", "https://attacker.example/jwks.json");
        var assertion = scenario.SignValid(header: header);

        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/kid-not-found", error!.Type);
        Assert.Equal("no-such-kid", error.Detail);
    }

    [Fact]
    public async Task IssuerNotEqualToSubjectIsRejected()
    {
        var scenario = new ClientAssertionScenario();
        var payload = scenario.ValidPayload().WithClaim("iss", "agent://curia.example/someone/else");
        var assertion = scenario.SignValid(payload: payload);

        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/issuer-subject-mismatch", error!.Type);
    }

    [Fact]
    public async Task SubjectNotMatchingTheResolverScopeIsRejected()
    {
        // The signature verifies fine (still signed by the agent's own key) but claims a
        // different subject than the one the caller resolved AgentKeyResolver against.
        var scenario = new ClientAssertionScenario();
        var payload = scenario.ValidPayload()
            .WithClaim("iss", "agent://curia.example/someone/else")
            .WithClaim("sub", "agent://curia.example/someone/else");
        var assertion = scenario.SignValid(payload: payload);

        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/subject-mismatch", error!.Type);
    }

    [Fact]
    public async Task R51_AudNotExactlyTheTokenEndpointIsRejected()
    {
        var scenario = new ClientAssertionScenario();
        var payload = scenario.ValidPayload().WithClaim("aud", "https://a-different-issuer.example/oauth2/token");
        var assertion = scenario.SignValid(payload: payload);

        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/audience-mismatch", error!.Type);
    }

    [Fact]
    public async Task R51_TtlExactlyAtTheSixtySecondCeilingIsAccepted()
    {
        var scenario = new ClientAssertionScenario();
        var payload = scenario.ValidPayload()
            .WithClaim("iat", TestJwt.ToUnixSeconds(scenario.Iat))
            .WithClaim("exp", TestJwt.ToUnixSeconds(scenario.Iat + TimeSpan.FromSeconds(60)));
        var assertion = scenario.SignValid(payload: payload);

        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
    }

    [Fact]
    public async Task R51_TtlOneSecondBeyondTheSixtySecondCeilingIsRejected()
    {
        var scenario = new ClientAssertionScenario();
        var payload = scenario.ValidPayload()
            .WithClaim("iat", TestJwt.ToUnixSeconds(scenario.Iat))
            .WithClaim("exp", TestJwt.ToUnixSeconds(scenario.Iat + TimeSpan.FromSeconds(61)));
        var assertion = scenario.SignValid(payload: payload);

        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/ttl-exceeded", error!.Type);
    }

    [Fact]
    public async Task ExpiredAssertionIsRejected()
    {
        var scenario = new ClientAssertionScenario();
        var payload = scenario.ValidPayload()
            .WithClaim("iat", TestJwt.ToUnixSeconds(scenario.Clock.GetUtcNow() - TimeSpan.FromSeconds(120)))
            .WithClaim("exp", TestJwt.ToUnixSeconds(scenario.Clock.GetUtcNow() - TimeSpan.FromSeconds(1)));
        var assertion = scenario.SignValid(payload: payload);

        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/expired", error!.Type);
    }

    [Fact]
    public async Task R514_SecondUseOfTheSameAssertionJtiIsRejectedAsAReplay()
    {
        var scenario = new ClientAssertionScenario();
        var assertion = scenario.SignValid(payload: scenario.ValidPayload(jti: "fixed-jti"));
        var ct = TestContext.Current.CancellationToken;

        var first = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, ct);
        var second = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, ct);

        Assert.True(first.TryGetValue(out _, out var firstError), firstError?.Detail);
        Assert.False(second.TryGetValue(out _, out var secondError));
        Assert.Equal("curia/authn/replay", secondError!.Type);
    }

    [Fact]
    public async Task R514_ReplayCacheIsSharedWithDpopProofs()
    {
        // R5.14: "A jti replay cache SHALL be maintained for both client assertions and DPoP
        // proofs" -- read as one shared cache, not two independent ones with the same shape.
        // A DPoP proof jti inserted first makes the identical value unusable as a client
        // assertion jti against a context wired to the same IReplayCache instance.
        var scenario = new ClientAssertionScenario();
        var ct = TestContext.Current.CancellationToken;
        var sharedJti = "shared-across-artifact-types";

        var preInserted = await scenario.ReplayCache.TryInsertAsync(sharedJti, scenario.Clock.GetUtcNow().AddMinutes(5), ct);
        Assert.True(preInserted.TryGetValue(out var wasFirst, out _) && wasFirst);

        var assertion = scenario.SignValid(payload: scenario.ValidPayload(jti: sharedJti));
        var result = await ClientAssertionValidator.ValidateAsync(assertion, scenario.Context, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/replay", error!.Type);
    }
}
