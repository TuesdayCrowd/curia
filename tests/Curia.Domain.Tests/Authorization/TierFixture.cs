using Curia.Domain.Authorization;

namespace Curia.Domain.Tests.Authorization;

/// <summary>
/// Mints an <see cref="EvaluatedTier"/> directly, which only a test assembly can do --
/// <c>Curia.Domain.csproj</c> grants <c>InternalsVisibleTo</c> to this project and production
/// <c>Curia.Application</c> is deliberately not on that list (R7.7; see
/// <see cref="EvaluatedTier"/>).
///
/// <para>Exists so that a test about Table 10 can name the column it is testing instead of
/// constructing posture facts that happen to evaluate to it. <c>TierPolicyTests</c> covers the
/// evaluation itself; conflating the two would make every Table 10 test depend on Table 11 being
/// right, and a failure in either would then implicate both.</para>
/// </summary>
internal static class TierFixture
{
    /// <summary>
    /// The instant is arbitrary and never read by <see cref="AccessPolicy"/> -- it decides from
    /// the cell and the credential state alone -- so a fixed epoch keeps these tests independent
    /// of when they run.
    /// </summary>
    internal static EvaluatedTier As(PrincipalTier tier) => new(tier, DateTimeOffset.UnixEpoch);
}
