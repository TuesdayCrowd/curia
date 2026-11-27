using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace Curia.Api.Tests;

/// <summary>
/// D41's probe fixtures (errata G82): hosts whose name, scheme, path base or forwarding differs from the plain fixture's, so that a probe can send the request spelled a way the origin is not. Each one
/// that names itself (<see cref="ForumFixture.PublicOrigin"/>) is held by ORG15 to dial what it was configured with.
/// </summary>
public sealed class OriginSettingFixture(Action<IWebHostBuilder> setOrigin) : ForumFixture
{
    /// <summary>ORG1 to ORG3: the host is told its name, or not told, by <paramref name="setOrigin"/> and nothing else.</summary>
    protected override void ConfigureOrigin(IWebHostBuilder builder) => setOrigin(builder);
}

/// <summary>ORG7: a Forum named <c>https://forum.test</c> that is served over <c>http</c>, as a TLS-terminating front serves it; its clients send <c>Host: forum.test</c>.</summary>
public sealed class HttpsNamedForumFixture : ForumFixture
{
    internal override string PublicOrigin => "https://forum.test";

    /// <summary>R11.112 (errata G85): a Forum that holds a public origin runs Production, the shape the hosted Forum runs; Development with it is refused at start.</summary>
    protected override string? HostingEnvironment => "Production";

    protected override void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Host = "forum.test";
    }
}

/// <summary>ORG8: the Forum mounted under <c>/forum</c> by the host's own code (the only door a path base has: no setting sets one, T0-5). Its name has no path.</summary>
public sealed class PathBaseForumFixture : ForumFixture
{
    internal const string Prefix = "/forum";

    protected override void ConfigureStore(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IStartupFilter>(new PathBaseFilter());
    }

    private sealed class PathBaseFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.UsePathBase(Prefix);
            next(app);
        };
    }
}

/// <summary>ORG9(i): the framework's own door, <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED=true</c> as a setting, which changes <c>Request.Scheme</c> on the unmodified Forum (T0-5).</summary>
public sealed class ForwardedDoorForumFixture : ForumFixture
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        base.ConfigureWebHost(builder);
        builder.UseSetting("ForwardedHeaders_Enabled", "true");
    }
}

/// <summary>ORG9(ii): <c>ForwardedHeaders</c> configured in code with <c>XForwardedHost</c> and <c>XForwardedProto</c>, which the default door never applies, so that a Forum that read the parsed <c>Host</c> would be moved by it.</summary>
public sealed class ForwardedHostForumFixture : ForumFixture
{
    protected override void ConfigureStore(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IStartupFilter>(new ForwardingFilter());
    }

    private sealed class ForwardingFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto };
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            app.UseForwardedHeaders(options);
            next(app);
        };
    }
}

/// <summary>
/// ORG20: a Forum on a real socket whose origin is an internationalized name in its A-label form, <c>xn--bcher-kva.example</c>, which the framework hands the code <b>decoded</b> (T0-2). Its clients dial the
/// reserved loopback port with <c>Host: xn--bcher-kva.example:{port}</c> forced, the shape of <c>RoleForumFixture</c>. Every other fixture in the suite is ASCII.
/// </summary>
public sealed class IdnOriginKestrelFixture : ForumFixture
{
    internal const string Label = "xn--bcher-kva.example";

    public IdnOriginKestrelFixture() => UseReservedKestrelPort(port => $"http://{Label}:{port}");

    /// <summary>R11.112 (errata G85): a Forum that holds a public origin runs Production, the shape the hosted Forum runs; Development with it is refused at start.</summary>
    protected override string? HostingEnvironment => "Production";

    internal string Authority => new Uri(PublicOrigin).Authority;

    protected override void ConfigureClient(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Host = Authority;
    }
}
