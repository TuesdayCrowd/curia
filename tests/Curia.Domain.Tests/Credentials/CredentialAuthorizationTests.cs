using Curia.Domain.Credentials;
using Xunit;

namespace Curia.Domain.Tests.Credentials;

/// <summary>Table 6 columns 3 ("Can authenticate?") and 4 ("Can post?"), verbatim.</summary>
public sealed class CredentialAuthorizationTests
{
    [Theory]
    [InlineData(CredentialState.Pending, CredentialAuthenticationScope.Denied)]
    [InlineData(CredentialState.Active, CredentialAuthenticationScope.Unrestricted)]
    [InlineData(CredentialState.Suspended, CredentialAuthenticationScope.Denied)]
    [InlineData(CredentialState.Quarantined, CredentialAuthenticationScope.ReadOnly)]
    [InlineData(CredentialState.Retired, CredentialAuthenticationScope.Denied)]
    [InlineData(CredentialState.Compromised, CredentialAuthenticationScope.Denied)]
    [InlineData(CredentialState.Expired, CredentialAuthenticationScope.Denied)]
    public void AuthenticationScopeMatchesTable6ColumnThree(CredentialState state, CredentialAuthenticationScope expected) =>
        Assert.Equal(expected, state.AuthenticationScope());

    [Theory]
    [InlineData(CredentialState.Pending, CredentialPostingEligibility.Denied)]
    [InlineData(CredentialState.Active, CredentialPostingEligibility.PerTier)]
    [InlineData(CredentialState.Suspended, CredentialPostingEligibility.Denied)]
    [InlineData(CredentialState.Quarantined, CredentialPostingEligibility.Denied)]
    [InlineData(CredentialState.Retired, CredentialPostingEligibility.Denied)]
    [InlineData(CredentialState.Compromised, CredentialPostingEligibility.Denied)]
    [InlineData(CredentialState.Expired, CredentialPostingEligibility.Denied)]
    public void PostingEligibilityMatchesTable6ColumnFour(CredentialState state, CredentialPostingEligibility expected) =>
        Assert.Equal(expected, state.PostingEligibility());

    [Fact]
    public void RetiredAndCompromisedStayDistinctStatesDespiteIdenticalAuthorizationOutcomes()
    {
        // R4.23: the two SHALL stay distinguishable in the public record even though, at this
        // layer, they currently produce the identical (Denied, Denied) authorization pair --
        // the distinction survives on CredentialState itself, never collapsed into one value.
        Assert.NotEqual(CredentialState.Retired, CredentialState.Compromised);
        Assert.Equal(CredentialAuthenticationScope.Denied, CredentialState.Retired.AuthenticationScope());
        Assert.Equal(CredentialAuthenticationScope.Denied, CredentialState.Compromised.AuthenticationScope());
    }
}
