using System.Diagnostics.CodeAnalysis;
using Curia.AuthN.Tests.Support;
using Curia.Domain.Primitives;
using Xunit;

namespace Curia.AuthN.Tests;

[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class AccessTokenValidatorKeyLivenessTests
{
    private static Task<Result<ValidatedRequest>> ValidateAsync(AccessTokenScenario scenario, IncomingRequest request) =>
        AccessTokenValidator.ValidateRequestAsync(request, scenario.Context, TestContext.Current.CancellationToken);

    [Fact]
    public async Task R5_22_AnActiveKeyIsAccepted()
    {
        var scenario = new AccessTokenScenario();

        var result = await ValidateAsync(scenario, scenario.ValidRequest());

        Assert.True(result.TryGetValue(out var validated, out var error), error?.Detail);
        Assert.Equal("scriptor-key-1", validated.Claims.AssertionKid);
    }

    [Theory]
    [InlineData(KeyStatus.Retired)]
    [InlineData(KeyStatus.Compromised)]
    [InlineData(KeyStatus.Unbound)]
    public async Task R5_22_AKeyThatIsNotActiveRefusesTheToken(KeyStatus status)
    {
        var scenario = new AccessTokenScenario();
        scenario.KeyLiveness.Status = status;

        var result = await ValidateAsync(scenario, scenario.ValidRequest());

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/token-key-not-live", error!.Type);
    }

    [Fact]
    public async Task R5_22_ATokenNamingNoKeyIsRefused()
    {
        var scenario = new AccessTokenScenario();
        var payload = scenario.ValidAccessTokenPayload();
        payload.Remove("curia_akid");

        var result = await ValidateAsync(scenario, scenario.ValidRequest(scenario.SignAccessToken(payload: payload)));

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/authn/token-names-no-key", error!.Type);
    }

    [Fact]
    public async Task R5_22_AReadThatFailsRefusesTheRequestWithItsOwnError()
    {
        var scenario = new AccessTokenScenario();
        scenario.KeyLiveness.Failure = new Error("curia/log/unreadable", "The event log could not be read");

        var result = await ValidateAsync(scenario, scenario.ValidRequest());

        Assert.False(result.TryGetValue(out _, out var error));
        Assert.Equal("curia/log/unreadable", error!.Type);
    }
}
