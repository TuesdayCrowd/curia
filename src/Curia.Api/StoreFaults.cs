using Curia.Api.Issuer;
using Curia.Application.Credentials;
using Curia.Application.Ports;
using Curia.Domain.Primitives;

namespace Curia.Api;

/// <summary>
/// R9.52, R8.82, R11.71 (errata G52): the one edge for a fault of a store the Forum owns, and its one factory. <see cref="UseStoreFaults"/> registers the
/// outermost middleware (beneath the developer exception page, so a fault it has answered never reaches the page, in every hosting environment); the factory
/// builds the three answers, so no route writes a type or a title of its own.
/// </summary>
internal static class StoreFaults
{
    /// <summary>The log could not be read; nothing was recorded.</summary>
    internal const string LogUnreadableType = LogBoundKeys.LogUnreadableType;

    /// <summary>Any other store could not complete the request; a write may or may not have been recorded.</summary>
    internal const string StoreUnavailableType = "curia/store/unavailable";

    /// <summary>An exception nothing names.</summary>
    internal const string ServerFaultType = "curia/server/fault";

    internal static IApplicationBuilder UseStoreFaults(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<StoreFaultsMiddleware>();
    }

    /// <summary>True where <paramref name="type"/> is one of the two types a fault of a store is served as (<c>503</c>, whatever status a caller chose).</summary>
    internal static bool IsStoreFaultType(string type) =>
        string.Equals(type, LogUnreadableType, StringComparison.Ordinal) || string.Equals(type, StoreUnavailableType, StringComparison.Ordinal);

    internal static IResult LogUnreadable(string cause) =>
        new ServerFault(StatusCodes.Status503ServiceUnavailable, new Error(LogUnreadableType, LogBoundKeys.LogUnreadableTitle, cause));

    internal static IResult Unavailable(string cause) =>
        new ServerFault(StatusCodes.Status503ServiceUnavailable, new Error(StoreUnavailableType, "The Forum's store could not complete the request", cause));

    internal static IResult Fault(string cause) =>
        new ServerFault(StatusCodes.Status500InternalServerError, new Error(ServerFaultType, "The Forum failed to complete the request", cause));
}

/// <summary>
/// The outermost middleware of errata G52 (R11.71). A <c>try</c> around the rest of the pipeline: an exception it can name is answered as a problem document
/// with no detail, and everything it says goes to the log.
///
/// <list type="bullet">
/// <item>A <see cref="LogUnreadableException"/> (the event store's, anywhere in the exception's chain) is <c>503</c> <c>curia/log/unreadable</c>.</item>
/// <item>An exception the <see cref="IStoreFaultClassifier"/> names (a driver fault of any other store, the gates', the nonce store's, the key store's) is
/// <c>503</c> <c>curia/store/unavailable</c>.</item>
/// <item>Any other exception is <c>500</c> <c>curia/server/fault</c>: a problem document, never a body-less answer or a developer page.</item>
/// <item>At <c>/oauth/token</c> each is RFC 6749's error object instead (<c>temporarily_unavailable</c> for a store, <c>server_error</c> otherwise).</item>
/// </list>
///
/// It does nothing where the response has started (the status cannot change; the exception goes on to the host), where the request's own token is cancelled
/// (the caller went away: that is not a fault of a store), and for a <see cref="BadHttpRequestException"/> (a <c>4xx</c> is left alone). Public so a fact can
/// drive it over a fake context (CS-5).
/// </summary>
public sealed partial class StoreFaultsMiddleware(RequestDelegate next)
{
    private const string TokenPath = "/oauth/token";
    private const int ChainDepth = 8;

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception e) when (!context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted && e is not BadHttpRequestException)
        {
            await AnswerAsync(context, e).ConfigureAwait(false);
        }
    }

    private static async Task AnswerAsync(HttpContext context, Exception exception)
    {
        var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger<StoreFaultsMiddleware>();
        var unreadable = LogUnreadableIn(exception);
        var store = unreadable is not null || context.RequestServices.GetRequiredService<IStoreFaultClassifier>().IsUnavailable(exception);
        var isToken = IsTokenRoute(context.Request.Path);

        IResult answer;
        if (store)
        {
            var cause = unreadable?.Cause ?? exception.GetType().Name;
            StoreFault(logger, exception, context.Request.Method, unreadable is not null ? StoreFaults.LogUnreadableType : StoreFaults.StoreUnavailableType);
            answer = isToken
                ? new OAuthRefusal("temporarily_unavailable", OAuthRefusal.StoreUnavailable, new Error(StoreFaults.StoreUnavailableType, "store unavailable", cause))
                : unreadable is not null ? StoreFaults.LogUnreadable(cause) : StoreFaults.Unavailable(cause);
        }
        else
        {
            UnhandledFault(logger, exception, context.Request.Method);
            var cause = exception.GetType().Name;
            answer = isToken
                ? new OAuthRefusal("server_error", OAuthRefusal.CouldNotIssue, new Error(StoreFaults.ServerFaultType, "server fault", cause))
                : StoreFaults.Fault(cause);
        }

        // Nothing a handler set before it threw (a Location, a Cache-Control, a nonce) belongs on the fault's answer: the response has not started, so it is cleared.
        context.Response.Clear();
        await answer.ExecuteAsync(context).ConfigureAwait(false);
    }

    /// <summary>The token route as routing reads it: case-insensitive, and a trailing slash is the same route.</summary>
    private static bool IsTokenRoute(PathString path) =>
        string.Equals(path.Value?.TrimEnd('/'), TokenPath, StringComparison.OrdinalIgnoreCase);

    private static LogUnreadableException? LogUnreadableIn(Exception exception)
    {
        var current = exception;
        for (var depth = 0; current is not null && depth <= ChainDepth; depth++, current = current.InnerException)
            if (current is LogUnreadableException found) return found;

        return null;
    }

    [LoggerMessage(
        EventId = 5002,
        EventName = "StoreFault",
        Level = LogLevel.Error,
        Message = "A store fault was served as {Type} ({Method}); the exception is attached")]
    private static partial void StoreFault(ILogger logger, Exception exception, string method, string type);

    // Not published by G52 (the entry names 5000 and 5002 only): the log line for an exception nothing names, so the operator keeps its stack.
    [LoggerMessage(
        EventId = 5003,
        EventName = "UnhandledFault",
        Level = LogLevel.Error,
        Message = "An unclassified exception was served as 500 ({Method}); the exception is attached")]
    private static partial void UnhandledFault(ILogger logger, Exception exception, string method);
}
