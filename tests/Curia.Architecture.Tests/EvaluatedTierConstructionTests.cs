using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Curia.Domain.Authorization;
using Xunit;

namespace Curia.Architecture.Tests;

/// <summary>
/// R7.7: "Tier SHALL be computed from live state at decision time, never read solely from a token
/// claim."
///
/// <para>The design makes that a construction rule rather than a convention:
/// <see cref="AuthorizationRequest"/> takes an <see cref="EvaluatedTier"/>, and an
/// <see cref="EvaluatedTier"/> can only be produced by <see cref="TierPolicy.Evaluate"/> or
/// <see cref="EvaluatedTier.Anonymous"/> because its constructor is internal to
/// <c>Curia.Domain</c>. A composition root that parsed a tier out of a JWT would have a
/// <see cref="PrincipalTier"/> and no way to turn it into the thing the PDP accepts.</para>
///
/// <para><b>What actually holds that up</b> is two guards, and neither alone is enough:</para>
/// <list type="number">
/// <item>the constructor stays <see langword="internal"/> -- asserted here, because a public
/// constructor would dissolve the whole property in one keyword;</item>
/// <item><c>Curia.Domain</c>'s <c>InternalsVisibleTo</c> list stays exactly what it is --
/// asserted by <see cref="EventStoreWriteSurfaceTests"/>'s
/// <c>CS15_InternalsVisibleToGrantIsExactlyIntended</c>, so adding production
/// <c>Curia.Application</c> to that list in order to mint a tier fails a test that already
/// exists.</item>
/// </list>
///
/// <para><b>The remaining hole, stated rather than papered over.</b> <c>Curia.Infrastructure</c>
/// is on the grant list (it needs internals for the event store), so it <i>can</i> construct an
/// <see cref="EvaluatedTier"/>. That is where a projection-to-request mapper legitimately lives,
/// so the access is wanted -- but it does mean the guarantee is "production Application and every
/// future host project cannot mint one", not "nothing can". Narrowing it further would take the
/// full IL walk <see cref="EventStoreWriteSurfaceTests"/> does for
/// <c>AppendedEvent</c>; that is worth doing when Infrastructure actually grows a tier mapper,
/// and would be premature theatre before it does.</para>
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1707:Identifiers should not contain underscores",
    Justification = "Test names carry the requirement ID (R7.7) they enforce verbatim.")]
public sealed class EvaluatedTierConstructionTests
{
    [Fact]
    public void R7_7_EvaluatedTierConstructorStaysInternal()
    {
        var ctors = typeof(EvaluatedTier).GetConstructors(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        var minting = Assert.Single(ctors, c => c.GetParameters().Length == 2);

        Assert.False(
            minting.IsPublic,
            "EvaluatedTier's (tier, instant) constructor must not be public: R7.7 depends on a tier " +
            "being impossible to mint outside Curia.Domain.");
        Assert.True(
            minting.IsAssembly,
            "EvaluatedTier's (tier, instant) constructor must be internal rather than private -- " +
            "Curia.Domain.Tests and Curia.Application.Tests need assembly-level access to build fixtures.");
    }

    /// <summary>
    /// There must be no public conversion from <see cref="PrincipalTier"/>. A cast operator, a
    /// public factory, or a settable property would each reopen the hole the internal constructor
    /// closes, and none of them would look like a security change at review time.
    /// </summary>
    [Fact]
    public void R7_7_NoPublicPathTurnsAPrincipalTierIntoAnEvaluatedTier()
    {
        var offenders = typeof(EvaluatedTier)
            .GetMembers(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
            .OfType<MethodBase>()
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(PrincipalTier)))
            .Select(m => m.Name)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The one public producer is <see cref="EvaluatedTier.Anonymous"/>, and it takes only an
    /// instant. Asserted so that "anonymous is the sole public factory" is a checked claim rather
    /// than something a reader has to confirm by scanning the type.
    /// </summary>
    [Fact]
    public void R7_7_TheOnlyPublicFactoryIsAnonymous()
    {
        var factories = typeof(EvaluatedTier)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(EvaluatedTier))
            .ToArray();

        var anonymous = Assert.Single(factories);
        Assert.Equal(nameof(EvaluatedTier.Anonymous), anonymous.Name);
        Assert.Equal([typeof(DateTimeOffset)], anonymous.GetParameters().Select(p => p.ParameterType));
    }
}
