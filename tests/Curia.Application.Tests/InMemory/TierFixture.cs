using Curia.Domain.Authorization;

namespace Curia.Application.Tests.InMemory;

/// <summary>
/// Mints an <see cref="EvaluatedTier"/> directly. Only a test assembly can:
/// <c>Curia.Domain.csproj</c> grants <c>InternalsVisibleTo</c> to this project, while production
/// <c>Curia.Application</c> -- the assembly these tests cover -- is deliberately absent from that
/// list, which is what makes R7.7 a compile-time property rather than a convention.
///
/// <para>That asymmetry is the point worth noticing: the code under test <i>cannot</i> build one
/// of these, so a PDP call assembled from a token claim would not compile in the very assembly
/// that would be tempted to write one.</para>
/// </summary>
internal static class TierFixture
{
    internal static EvaluatedTier As(PrincipalTier tier) => new(tier, DateTimeOffset.UnixEpoch);
}
