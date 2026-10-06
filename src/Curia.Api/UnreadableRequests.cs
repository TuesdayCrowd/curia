namespace Curia.Api;

/// <summary>
/// R11.33 (errata G17): a request no handler can read is answered with a 4xx that is an RFC 9457
/// problem document. The binder's refusals (400, 415) and routing's (404, 405) are written by the
/// framework before any Forum code runs, with no body; this gives each one a type and a title, and
/// no detail, so nothing the framework said is echoed. <c>UseStatusCodePages</c> fires only on a
/// response that has no body, so every response a handler composed is untouched. A 5xx stays
/// <see cref="ServerFault"/>'s, and <c>/oauth</c> is left alone, because the token endpoint composes
/// RFC 6749's errors itself (Task 8's second review, I2).
/// </summary>
internal static class UnreadableRequests
{
    public static WebApplication UseUnreadableRequests(this WebApplication app)
    {
        app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;
            var status = http.Response.StatusCode;
            if (status < 400 || status > 499) return;
            if (http.Request.Path.StartsWithSegments("/oauth", StringComparison.Ordinal)) return;

            var (type, title) = status switch
            {
                StatusCodes.Status400BadRequest => ("curia/request/unreadable", "The request could not be read"),
                StatusCodes.Status404NotFound => ("curia/request/no-route", "No route reads this request"),
                StatusCodes.Status405MethodNotAllowed => ("curia/request/method-not-allowed", "This route does not take this method"),
                StatusCodes.Status413PayloadTooLarge => ("curia/request/too-large", "The request is larger than this route reads"),
                StatusCodes.Status415UnsupportedMediaType => ("curia/request/unsupported-media-type", "This route does not read this media type"),
                _ => ("curia/request/refused", "The request was refused"),
            };

            await Results.Json(new Problem(type, title, null), statusCode: status)
                .ExecuteAsync(http)
                .ConfigureAwait(false);
        });
        return app;
    }
}
