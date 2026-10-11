using System.Diagnostics.CodeAnalysis;
using Curia.Application.Ports;
using Curia.AuthN;
using Xunit;

namespace Curia.Api.Tests;

/// <summary>
/// R11.34 (errata G18), spec section 4.7: a <c>kid</c> that is no <c>Kid</c>, refused in
/// <c>Curia.AuthN</c> before any store is asked, is answered exactly as a <c>kid</c> the store does
/// not hold. <c>Curia.AuthN</c> cannot reference <c>Curia.Application</c> (CS-5), so it writes that
/// answer itself; this host test, which sees both assemblies, holds the two to one answer.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement IDs they enforce verbatim.")]
public sealed class KeyNotRegisteredParityTests
{
    [Theory]
    [InlineData("issuer", "")]
    [InlineData("agent://forum/alice", "kid-alices")]
    public void R11_34_AKidNoStoreCouldHoldIsAnsweredAsOneTheStoreDoesNotHold(string agentId, string kid) =>
        Assert.Equal(AuthorKeyErrors.NotRegisteredToAgent(agentId, kid), AuthNErrors.KeyNotRegistered(agentId, kid));
}
