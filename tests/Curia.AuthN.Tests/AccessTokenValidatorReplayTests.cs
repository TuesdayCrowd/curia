using System.Diagnostics.CodeAnalysis;
using Curia.AuthN.Tests.InMemory;
using Curia.AuthN.Tests.Support;
using Xunit;

namespace Curia.AuthN.Tests;

/// <summary>R5.14/R5.15: the DPoP proof <c>jti</c> replay cache, exercised end to end through
/// <see cref="AccessTokenValidator"/> rather than directly against <see cref="InMemoryReplayCache"/>
/// (see <c>InMemoryReplayCacheTests</c> for the port-level atomicity proof).</summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement ID (R5.15) they pin verbatim, mirroring " +
        "Curia.Architecture.Tests.LayeringTests' CS-6/CS-7 precedent.")]
public sealed class AccessTokenValidatorReplayTests
{
    [Fact]
    public async Task SecondUseOfTheSameDpopProofIsRejectedAsAReplay()
    {
        var scenario = new AccessTokenScenario();
        var request = scenario.ValidRequest();
        var ct = TestContext.Current.CancellationToken;

        var first = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, ct);
        var second = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, ct);

        Assert.True(first.TryGetValue(out _, out var firstError), firstError?.Detail);
        Assert.False(second.TryGetValue(out _, out var secondError));
        Assert.Equal("curia/authn/replay", secondError!.Type);
    }

    [Fact]
    public async Task R515_ReplayIsRejectedAcrossTwoResourceServerInstancesSharingOneCache()
    {
        // R5.15: "The cache SHALL be shared across all instances of a resource server ... a
        // per-process cache means an attacker replays against a different pod and succeeds."
        // Two independently constructed validation contexts, standing in for two RS instances,
        // deliberately share nothing except the one IReplayCache -- exactly what a Redis-backed
        // deployment would share and nothing else.
        var scenario = new AccessTokenScenario();
        var instanceTwoContext = scenario.Context with
        {
            IssuerKeyResolver = new InMemoryJwsKeyResolver(scenario.IssuerKey.Kid, scenario.IssuerKey.PublicKey),
        };
        var request = scenario.ValidRequest();
        var ct = TestContext.Current.CancellationToken;

        var onInstanceOne = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, ct);
        var onInstanceTwo = await AccessTokenValidator.ValidateRequestAsync(request, instanceTwoContext, ct);

        Assert.True(onInstanceOne.TryGetValue(out _, out var firstError), firstError?.Detail);
        Assert.False(onInstanceTwo.TryGetValue(out _, out var secondError));
        Assert.Equal("curia/authn/replay", secondError!.Type);
    }

    [Fact]
    public async Task R515_ADifferentSharedCacheDoesNotSeeTheReplay()
    {
        // The negative control for the test above: two contexts with genuinely independent
        // caches (the R5.15 violation -- "a per-process cache") both accept the same proof,
        // proving the rejection above came from the shared cache and not from anything else
        // (signature, binding, freshness) also failing on reuse.
        var scenario = new AccessTokenScenario();
        var independentCacheContext = scenario.Context with { ReplayCache = new InMemoryReplayCache() };
        var request = scenario.ValidRequest();
        var ct = TestContext.Current.CancellationToken;

        var onInstanceOne = await AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, ct);
        var onInstanceTwo = await AccessTokenValidator.ValidateRequestAsync(request, independentCacheContext, ct);

        Assert.True(onInstanceOne.TryGetValue(out _, out var firstError), firstError?.Detail);
        Assert.True(onInstanceTwo.TryGetValue(out _, out var secondError), secondError?.Detail);
    }
}
