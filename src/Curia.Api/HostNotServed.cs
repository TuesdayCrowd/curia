namespace Curia.Api;

/// <summary>
/// R5.27 (errata G82, item 374): a request whose <c>Host</c> is not the name the Forum was configured with is refused by the Forum, in its own words, on every route
/// without exception (<c>/health</c> and the Reader Contract included), before any route is selected and before any store is read. The framework's own host filtering
/// answers in HTML, outside the contract and outside the request fuzzer's oracle; this answers a problem document, or at <c>/oauth/token</c> RFC 6749's error object
/// (R11.33), with no detail, and nothing of the request in the body or any header.
///
/// <para><b>The comparison reads the header as received</b> (<see cref="PublicOrigin.Serves"/>): not <c>HttpRequest.Host</c>, which decodes an internationalized
/// name and is rewritten by a forwarding middleware. <b>This refusal is not what makes R5.26 hold</b>: the binding refuses a credential made for a spelling of the
/// Forum's own host that the origin does not use, on a <c>Host</c> this check admits, and has its own probes.</para>
/// </summary>
internal static class HostNotServed
{
    internal const string Type = "curia/request/host-not-served";
    internal const string Title = "The Forum is not served under this name";

    /// <summary>
    /// Registered as a startup filter, and so outside every middleware the application adds, <b>and outside any forwarding middleware a later registration or an operator's code adds</b>:
    /// <c>ForwardedHeadersMiddleware</c> rewrites the <c>Host</c> header from <c>X-Forwarded-Host</c> when it is configured to, and a refusal that ran after it would read a name the
    /// request's forwarding header chose (R5.26 forbids it). The framework's own forwarded-headers door (<c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c>) applies no host and is registered
    /// before this filter.
    /// </summary>
    internal sealed class Filter(PublicOrigin origin) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);
            return app =>
            {
                app.Use((context, following) => Invoke(context, following, origin));
                next(app);
            };
        }
    }

    /// <summary>Not async, so the one read of the header is made by this method itself (the fence's one reasoned row names it).</summary>
    internal static Task Invoke(HttpContext context, Func<Task> next, PublicOrigin origin) =>
        origin.Serves(context.Request.Headers.Host) ? next() : Refuse(context);

    private static Task Refuse(HttpContext context)
    {
        object body = context.Request.Path.Equals("/oauth/token", StringComparison.Ordinal)
            ? new { error = "invalid_request", error_description = Title }
            : new Problem(Type, Title, null);
        return Results.Json(body, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
    }
}
