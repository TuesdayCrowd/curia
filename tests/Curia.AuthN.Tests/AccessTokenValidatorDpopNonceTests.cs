using Curia.AuthN.Tests.InMemory;
using Curia.AuthN.Tests.Support;
using Xunit;

namespace Curia.AuthN.Tests;

/// <summary>Errata B4/R5.19: DPoP server nonces, required only on write paths
/// (<see cref="IncomingRequest.RequireDpopNonce"/>) and only when this resource server has
/// adopted the SHOULD by configuring an <see cref="Curia.AuthN.Ports.IDpopNonceStore"/> at all.</summary>
public sealed class AccessTokenValidatorDpopNonceTests
{
    [Fact]
    public async Task WritePathWithoutANonceClaimIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var request = scenario.ValidRequest(requireNonce: true); // proof carries no nonce claim

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/nonce-missing", error!.Type);
    }

    [Fact]
    public async Task WritePathWithAnUnissuedNonceIsRejected()
    {
        var scenario = new AccessTokenScenario();
        var token = scenario.SignAccessToken();
        var proof = scenario.SignDpopProof(token, payload: scenario.ValidDpopPayload(token, nonce: "a-nonce-nobody-issued"));
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof, requireNonce: true);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/nonce-stale", error!.Type);
    }

    [Fact]
    public async Task WritePathWithTheCurrentlyIssuedNonceIsAccepted()
    {
        var scenario = new AccessTokenScenario();
        var ct = TestContext.Current.CancellationToken;
        var issued = await scenario.NonceStore.IssueAsync(ct);
        Assert.True(issued.TryGetValue(out var nonce, out var issueError), issueError?.Detail);

        var token = scenario.SignAccessToken();
        var proof = scenario.SignDpopProof(token, payload: scenario.ValidDpopPayload(token, nonce: nonce.Value));
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof, requireNonce: true);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, ct);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
    }

    [Fact]
    public async Task RotatingToANewNonceMakesThePreviousOneStale()
    {
        var scenario = new AccessTokenScenario();
        var ct = TestContext.Current.CancellationToken;
        var firstIssued = await scenario.NonceStore.IssueAsync(ct);
        Assert.True(firstIssued.TryGetValue(out var firstNonce, out _));

        // Rotate: a fresh nonce is issued, and R5.19's rotation means the old one is no longer current.
        var secondIssued = await scenario.NonceStore.IssueAsync(ct);
        Assert.True(secondIssued.TryGetValue(out _, out _));

        var token = scenario.SignAccessToken();
        var proof = scenario.SignDpopProof(token, payload: scenario.ValidDpopPayload(token, nonce: firstNonce.Value));
        var request = scenario.ValidRequest(accessToken: token, dpopProof: proof, requireNonce: true);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, ct);

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/nonce-stale", error!.Type);
    }

    [Fact]
    public async Task ReadPathDoesNotRequireANonceEvenWithoutOne()
    {
        var scenario = new AccessTokenScenario();
        var request = scenario.ValidRequest(requireNonce: false);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
    }

    [Fact]
    public async Task WritePathIsNotEnforcedWhenNoNonceStoreIsConfigured()
    {
        // A deployment that has not adopted the R5.19 SHOULD yet: DpopNonceStore is null, so the
        // write path proceeds exactly as it would have before this stage existed.
        var scenario = new AccessTokenScenario();
        var contextWithoutNonceStore = scenario.Context with { DpopNonceStore = null };
        var request = scenario.ValidRequest(requireNonce: true);

        var result = await AccessTokenValidator.ValidateRequestAsync(request, contextWithoutNonceStore, TestContext.Current.CancellationToken);

        Assert.True(result.TryGetValue(out _, out var error), error?.Detail);
    }
}
