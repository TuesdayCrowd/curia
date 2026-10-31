using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R9.52, R8.82, R11.71 (errata G52): the one edge for a fault of a store the Forum owns. Declared by the seam and <b>not registered</b>
/// (<see cref="UseStoreFaults"/> is not called by <c>Program.Build</c>), so no request meets it and no status moves; the middleware passes the request on
/// and the factory is unused. Task 2 registers it and gives it the behaviour.
/// </summary>
internal static class StoreFaults
{
    /// <summary>The log could not be read; nothing was recorded.</summary>
    internal const string LogUnreadableType = "curia/log/unreadable";

    /// <summary>Any other store could not complete the request; a write may or may not have been recorded.</summary>
    internal const string StoreUnavailableType = "curia/store/unavailable";

    /// <summary>An exception nothing names.</summary>
    internal const string ServerFaultType = "curia/server/fault";

    internal static IApplicationBuilder UseStoreFaults(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<StoreFaultsMiddleware>();
    }

    internal static IResult LogUnreadable(string cause) =>
        new ServerFault(StatusCodes.Status503ServiceUnavailable, new Error(LogUnreadableType, "The event log could not be read", cause));

    internal static IResult Unavailable(string cause) =>
        new ServerFault(StatusCodes.Status503ServiceUnavailable, new Error(StoreUnavailableType, "The Forum's store could not complete the request", cause));

    internal static IResult Fault(string cause) =>
        new ServerFault(StatusCodes.Status500InternalServerError, new Error(ServerFaultType, "The Forum failed to complete the request", cause));
}

/// <summary>The outermost middleware of errata G52. Inert in the seam: it passes the request on. Public so a fact can drive it over a fake context (CS-5).</summary>
public sealed class StoreFaultsMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context) => next(context);
}
