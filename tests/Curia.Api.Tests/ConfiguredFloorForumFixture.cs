using Microsoft.AspNetCore.Hosting;

namespace Curia.Api.Tests;

/// <summary>
/// R10.54 (rev.), R9.31: the Forum as <see cref="Program"/> composes it with the search surface's floor
/// configured at V1 through <c>Curia:Retrieval:Floors</c>, the path a deployment uses (R10.45 rev.). No
/// deployment configures above V0 today, so a clamp has nothing to clamp without this.
/// </summary>
public sealed class ConfiguredFloorForumFixture : ForumFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        base.ConfigureWebHost(builder);
        builder.UseSetting("Curia:Retrieval:Floors:rest-search", "V1");
    }
}
