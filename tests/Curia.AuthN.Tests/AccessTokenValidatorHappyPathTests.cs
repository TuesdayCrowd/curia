using Curia.AuthN.Tests.InMemory;
using Curia.AuthN.Tests.Support;
using Xunit;

namespace Curia.AuthN.Tests;

public sealed class AccessTokenValidatorHappyPathTests
{
    [Fact]
    public async Task FullyValidRequestIsAccepted()
    {
        var scenario = new AccessTokenScenario();
        var request = scenario.ValidRequest();

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out var validated, out var error), error?.Detail);
        Assert.Equal(AccessTokenScenario.Subject, validated.Claims.Sub);
        Assert.Equal(scenario.DpopKey.Thumbprint, validated.DpopKeyThumbprint);
    }

    [Fact]
    public async Task Es256AccessTokenAndDpopProofAreAlsoAccepted()
    {
        // R4.15/Table 8 permit EdDSA or ES256; the happy path above already exercises EdDSA
        // end to end, so this proves the other half of the allow-list actually verifies too,
        // not just that it is accepted by the Phase 1 alg pin.
        var scenario = new AccessTokenScenario();
        var issuerKey = TestKeys.Es256("issuer-es256");
        var dpopKey = TestKeys.Es256("dpop-es256-irrelevant");

        var context = scenario.Context with
        {
            IssuerKeyResolver = new InMemoryJwsKeyResolver(issuerKey.Kid, issuerKey.PublicKey),
        };

        var header = new Dictionary<string, object> { ["alg"] = "ES256", ["kid"] = issuerKey.Kid, ["typ"] = "at+jwt" };
        var payload = scenario.ValidAccessTokenPayload();
        payload["cnf"] = new Dictionary<string, object?> { ["jkt"] = dpopKey.Thumbprint };

        var token = scenario.SignAccessToken(header, payload, issuerKey);
        var proof = scenario.SignDpopProof(token, scenario.ValidDpopHeader(dpopKey), scenario.ValidDpopPayload(token), dpopKey);
        var request = new IncomingRequest($"DPoP {token}", proof, AccessTokenScenario.HttpMethod, AccessTokenScenario.CanonicalUrl);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
    }
}
