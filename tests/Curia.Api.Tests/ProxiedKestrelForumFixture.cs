using Curia.Tests.Shared;

namespace Curia.Api.Tests;

/// <summary>
/// D41 (errata G82, amendment 1 #3): the Forum on a real socket behind a <see cref="CountingProxy"/> <b>that is the Forum's name</b>. The Forum builds every URL a credential is compared with from its one
/// configured origin, and a client signs over the address it dials, so the address a test dials has to be the origin: the proxy's address is the origin and the client's <c>BaseAddress</c>, and the
/// proxy forwards the client's own <c>Host</c> unchanged, which is the origin's authority. The proxy is started first and holds its port for the fixture's life (<see cref="CountingProxy.Start"/>), so
/// there is no reserve-then-bind race for it; the Forum's own port is any port, because nothing names it. A test counts what it relays by <see cref="Proxy"/>'s <see cref="CountingProxy.Mark"/>.
/// </summary>
public sealed class ProxiedKestrelForumFixture : ForumFixture
{
    private readonly CountingProxy _proxy = CountingProxy.Start();
    private bool _targeted;

    public ProxiedKestrelForumFixture() => UseKestrel(0);

    internal override string PublicOrigin => _proxy.Address;

    internal CountingProxy Proxy => _proxy;

    /// <summary>
    /// A client of the Forum <b>through the proxy</b>. The factory gives a Kestrel client the Forum's own socket as its <c>BaseAddress</c> after <c>ConfigureClient</c> has run, so the address is read from the
    /// client it made (the proxy's target, once) and replaced here: what the client dials is the proxy, which is the origin.
    /// </summary>
    internal override HttpClient Client
    {
        get
        {
            var client = base.Client;
            if (!_targeted)
            {
                _proxy.SetTarget(client.BaseAddress!);
                _targeted = true;
            }

            client.BaseAddress = new Uri(_proxy.Address);
            return client;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        _proxy.Dispose();
        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
