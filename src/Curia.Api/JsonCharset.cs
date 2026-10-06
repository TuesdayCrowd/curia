using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Curia.Api;

/// <summary>
/// R11.33 (errata G17): the minimal-API JSON binder throws for a Content-Type whose charset it does
/// not know, before any endpoint filter runs, which answered 500 to anyone. A JSON body is read as
/// UTF-8 only (RFC 8259 §8.1), so one declaring another charset is refused 415 before binding.
/// The comparison is on the raw parameter, because the binder does not unquote it, and a guard more
/// permissive than the binder lets the binder's throw through: a quoted <c>"utf-8"</c> is refused
/// (Task 8's review, I1).
/// </summary>
internal static class JsonCharset
{
    internal static bool IsRefused(string? contentType)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var media)) return false;
        var type = media.MediaType;
        var json = StringSegment.Equals(type, "application/json", StringComparison.OrdinalIgnoreCase)
            || type.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
        if (!json) return false;
        if (!media.Charset.HasValue) return false;
        return !StringSegment.Equals(media.Charset, "utf-8", StringComparison.OrdinalIgnoreCase);
    }

    public static WebApplication UseUtf8JsonBodies(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/oauth", StringComparison.Ordinal) && IsRefused(context.Request.ContentType))
            {
                await Results.Json(
                    new Problem("curia/request/unsupported-charset", "A JSON body is read as UTF-8 only (RFC 8259 §8.1); declare charset=utf-8, unquoted, or none", null),
                    statusCode: StatusCodes.Status415UnsupportedMediaType).ExecuteAsync(context).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        return app;
    }
}
